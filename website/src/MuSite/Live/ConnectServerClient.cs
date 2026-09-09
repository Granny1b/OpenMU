using System.Buffers.Binary;
using System.Net.Sockets;

namespace MuSite.Live;

/// <summary>The load of one game server, as the connect server reports it to a game client.</summary>
/// <param name="ServerId">The server id shown in the client's server list.</param>
/// <param name="LoadPercent">Percent of the server's player cap that is in use, 0-100.</param>
public sealed record ServerLoad(int ServerId, int LoadPercent);

/// <summary>
/// Asks the connect server for its server list, the same way a game client does.
///
/// WHY SPEAK THE PROTOCOL AT ALL
///
/// OpenMU keeps the number of connected players in memory only - there is no table to read - and
/// the all-in-one image ships no metrics exporter. The connect server, however, publishes a load
/// percentage for every game server to any client that asks, with no credential. That is enough to
/// draw "is a server filling up", and it keeps working when the admin panel is down.
///
/// WHAT THE NUMBER IS WORTH
///
/// LoadPercentage is <c>(byte)(currentConnections * 100f / maximumPlayers)</c>, so it is truncated
/// to whole percent. At OpenMU's default cap of 1000 players one percent is ten players, and a
/// server with nine online reports zero. It is a load gauge, not a player counter - the exact count
/// comes from <see cref="OpenMuStatusClient"/> when an API key is configured.
///
/// The packet layout is taken from src/Network/Packets/ConnectServer/ConnectServerPackets.cs in
/// this repository, not from a description of the protocol. Note that it mixes endianness: the
/// packet length and the server count are big endian, the server id inside each entry is little
/// endian.
/// </summary>
public static class ConnectServerClient
{
    /// <summary>C1 header, code 0xF4, sub code 0x06: "send me the server list".</summary>
    private static readonly byte[] ServerListRequest = [0xC1, 0x04, 0xF4, 0x06];

    /// <summary>A server list of 100 servers is 407 bytes; anything beyond this is not one.</summary>
    private const int MaxPacketSize = 8192;

    /// <summary>How many packets to skip while waiting for the response.</summary>
    /// <remarks>
    /// The connect server sends an unsolicited "Hello" (C1 04 00 01) the moment it accepts the
    /// connection, so at least one packet arrives that is not the answer. The request is sent
    /// without waiting for it - the server does not require the greeting to be acknowledged - which
    /// means the two can arrive in either order.
    /// </remarks>
    private const int MaxPacketsBeforeAnswer = 8;

    /// <summary>
    /// Connects, asks for the server list and returns what came back.
    /// </summary>
    /// <param name="host">Host name of the connect server.</param>
    /// <param name="port">Client listener port, 44405 in the all-in-one deployment.</param>
    /// <param name="timeout">Budget for the whole exchange, connect included.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per game server, or an empty list when the exchange did not complete.</returns>
    public static async Task<IReadOnlyList<ServerLoad>> ListServersAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The connection is closed as soon as the answer is read, and that matters: the connect
        // server refuses a new connection once one address holds MaxConnectionsPerAddress (30 by
        // default) at the same time, and it only forgets a connection when it is disconnected. A
        // leaked socket here would, after a couple of hours of probing, lock the website out of the
        // very server it is monitoring.
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
        await using var stream = client.GetStream();

        await stream.WriteAsync(ServerListRequest, deadline.Token).ConfigureAwait(false);

        for (var packets = 0; packets < MaxPacketsBeforeAnswer; packets++)
        {
            var packet = await ReadPacketAsync(stream, deadline.Token).ConfigureAwait(false);
            if (packet.Length >= 7 && packet[0] == 0xC2 && packet[3] == 0xF4 && packet[4] == 0x06)
            {
                return Parse(packet);
            }
        }

        return [];
    }

    /// <summary>
    /// Reads exactly one packet, whose length is carried in its own header.
    /// </summary>
    /// <remarks>
    /// TCP is a byte stream: the greeting and the answer can arrive in one segment, and a single
    /// packet can be split across several. Reading "whatever is available" would work in testing
    /// and then mis-frame under load, so every read here is for an exact number of bytes.
    /// </remarks>
    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new byte[3];

        // Byte 0 is the header type, and it decides how wide the length field is.
        await stream.ReadExactlyAsync(head.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);

        int total;
        int headerRead;
        switch (head[0])
        {
            case 0xC1:
            case 0xC3:
                // One length byte, counting the whole packet.
                await stream.ReadExactlyAsync(head.AsMemory(1, 1), cancellationToken).ConfigureAwait(false);
                total = head[1];
                headerRead = 2;
                break;

            case 0xC2:
            case 0xC4:
                // Two length bytes, BIG endian, counting the whole packet.
                await stream.ReadExactlyAsync(head.AsMemory(1, 2), cancellationToken).ConfigureAwait(false);
                total = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1, 2));
                headerRead = 3;
                break;

            default:
                throw new InvalidDataException($"Unknown packet header type 0x{head[0]:X2}.");
        }

        if (total < headerRead || total > MaxPacketSize)
        {
            throw new InvalidDataException($"Packet claims a length of {total} bytes.");
        }

        var packet = new byte[total];
        head.AsSpan(0, headerRead).CopyTo(packet);
        await stream.ReadExactlyAsync(packet.AsMemory(headerRead), cancellationToken).ConfigureAwait(false);
        return packet;
    }

    /// <summary>
    /// Reads the entries out of a ServerListResponse.
    /// </summary>
    /// <remarks>
    /// Layout: a five byte C2 header with sub code, then the server count as a BIG endian ushort at
    /// offset 5, then four bytes per server from offset 7 - server id as a LITTLE endian ushort,
    /// load percentage, and one byte of padding.
    /// </remarks>
    private static IReadOnlyList<ServerLoad> Parse(byte[] packet)
    {
        var claimed = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(5, 2));

        // Trust the packet's real size over the count it declares. A truncated or hostile packet
        // that says "60000 servers" must not make this walk off the end of the array.
        var fit = (packet.Length - 7) / 4;
        var count = Math.Min(claimed, fit);

        var servers = new List<ServerLoad>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = packet.AsSpan(7 + (i * 4), 4);
            servers.Add(new ServerLoad(
                BinaryPrimitives.ReadUInt16LittleEndian(entry),
                entry[2]));
        }

        return servers;
    }
}
