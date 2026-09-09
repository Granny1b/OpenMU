using System.Net;
using System.Net.Sockets;
using MuSite.Live;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Checks the connect server protocol against packets OPENMU ITSELF WROTE.
///
/// The fixture in tests/fixtures/connectserver-serverlist.txt was produced by running OpenMU's own
/// ServerListResponse writer, so these tests compare the client against the real encoder rather
/// than against a second reading of the same documentation. That matters here more than usual: the
/// layout mixes endianness - the length and the server count are big endian while the server id
/// inside each entry is little endian - and a parser that got that backwards would still return
/// plausible-looking numbers for small server ids.
/// </summary>
public sealed class ConnectServerClientTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static readonly Dictionary<string, byte[]> Fixture = LoadFixture();

    [Fact]
    public async Task ItReadsTheServerListOpenMuActuallySends()
    {
        await using var server = FakeConnectServer.Start(async stream =>
        {
            await stream.WriteAsync(Fixture["hello"]);
            await ReadRequestAsync(stream);
            await stream.WriteAsync(Fixture["serverlist"]);
        });

        var servers = await ConnectServerClient.ListServersAsync("127.0.0.1", server.Port, Patience, default);

        Assert.Equal(4, servers.Count);
        Assert.Equal(new ServerLoad(0, 37), servers[0]);
        Assert.Equal(new ServerLoad(1, 0), servers[1]);

        // 258 is 0x0102. Read big endian this would be 513, so this row is what actually pins the
        // endianness of the server id.
        Assert.Equal(new ServerLoad(258, 100), servers[2]);
        Assert.Equal(new ServerLoad(65535, 7), servers[3]);
    }

    [Fact]
    public async Task ItSendsExactlyTheBytesAGameClientSends()
    {
        byte[]? received = null;
        await using var server = FakeConnectServer.Start(async stream =>
        {
            await stream.WriteAsync(Fixture["hello"]);
            received = await ReadRequestAsync(stream);
            await stream.WriteAsync(Fixture["serverlist"]);
        });

        await ConnectServerClient.ListServersAsync("127.0.0.1", server.Port, Patience, default);

        Assert.Equal(Fixture["request"], received);
    }

    [Fact]
    public async Task ItSurvivesTheAnswerArrivingOneByteAtATime()
    {
        // TCP is a byte stream, not a message queue. A parser that read "whatever is available"
        // would pass every other test here and then mis-frame on a busy network.
        await using var server = FakeConnectServer.Start(async stream =>
        {
            await stream.WriteAsync(Fixture["hello"]);
            await ReadRequestAsync(stream);
            foreach (var b in Fixture["serverlist"])
            {
                await stream.WriteAsync(new[] { b });
                await stream.FlushAsync();
            }
        });

        var servers = await ConnectServerClient.ListServersAsync("127.0.0.1", server.Port, Patience, default);

        Assert.Equal(4, servers.Count);
        Assert.Equal(100, servers[2].LoadPercent);
    }

    [Fact]
    public async Task ItSurvivesTheGreetingAndTheAnswerArrivingTogether()
    {
        await using var server = FakeConnectServer.Start(async stream =>
        {
            await ReadRequestAsync(stream);
            byte[] both = [.. Fixture["hello"], .. Fixture["serverlist"]];
            await stream.WriteAsync(both);
        });

        var servers = await ConnectServerClient.ListServersAsync("127.0.0.1", server.Port, Patience, default);

        Assert.Equal(4, servers.Count);
    }

    [Fact]
    public async Task ItSkipsUnrelatedPacketsBeforeTheAnswer()
    {
        // The greeting is not the only thing that can arrive first, and the client must not treat
        // an unexpected packet as a failure - it has to keep reading for the one it asked for.
        await using var server = FakeConnectServer.Start(async stream =>
        {
            await ReadRequestAsync(stream);
            await stream.WriteAsync(Fixture["hello"]);
            await stream.WriteAsync(Fixture["hello"]);
            await stream.WriteAsync(Fixture["serverlist"]);
        });

        var servers = await ConnectServerClient.ListServersAsync("127.0.0.1", server.Port, Patience, default);

        Assert.Equal(4, servers.Count);
    }

    [Fact]
    public async Task APacketClaimingMoreServersThanItCarriesDoesNotWalkOffTheEnd()
    {
        // Says four servers, carries two. A parser that trusted the count would read past the
        // array; one that returns what is really there is the only safe answer.
        var truncated = Fixture["serverlist"].AsSpan(0, 7 + (2 * 4)).ToArray();
        truncated[1] = 0;
        truncated[2] = (byte)truncated.Length;

        await using var server = FakeConnectServer.Start(async stream =>
        {
            await ReadRequestAsync(stream);
            await stream.WriteAsync(truncated);
        });

        var servers = await ConnectServerClient.ListServersAsync("127.0.0.1", server.Port, Patience, default);

        Assert.Equal(2, servers.Count);
    }

    [Fact]
    public async Task ItGivesUpWhenTheServerAcceptsButNeverAnswers()
    {
        // A game server mid-restart accepts the connection before it can serve anything. The probe
        // has to come back on its own rather than hang until the next tick.
        await using var server = FakeConnectServer.Start(async stream =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            GC.KeepAlive(stream);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConnectServerClient.ListServersAsync(
                "127.0.0.1", server.Port, TimeSpan.FromMilliseconds(300), default));
    }

    [Fact]
    public async Task AnUnknownHeaderIsRejectedRatherThanGuessedAt()
    {
        await using var server = FakeConnectServer.Start(async stream =>
        {
            await ReadRequestAsync(stream);
            await stream.WriteAsync(new byte[] { 0x7F, 0x04, 0x00, 0x00 });
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ConnectServerClient.ListServersAsync("127.0.0.1", server.Port, Patience, default));
    }

    /// <summary>Reads the four request bytes the client sends.</summary>
    private static async Task<byte[]> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[4];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    /// <summary>Reads the hex fixture into name -> bytes.</summary>
    private static Dictionary<string, byte[]> LoadFixture()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../fixtures/connectserver-serverlist.txt"));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The connect server packet fixture is missing at {path}. It is generated from " +
                "OpenMU's own ServerListResponse writer - see the header in the file itself.", path);
        }

        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
            found[parts[0]] = Convert.FromHexString(parts[1]);
        }

        return found;
    }

    /// <summary>A loopback listener that plays one scripted exchange and stops.</summary>
    private sealed class FakeConnectServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;
        private readonly CancellationTokenSource _stopping = new();

        private FakeConnectServer(TcpListener listener, Func<NetworkStream, Task> exchange)
        {
            this._listener = listener;
            this.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            this._serving = this.ServeAsync(exchange);
        }

        public int Port { get; }

        public static FakeConnectServer Start(Func<NetworkStream, Task> exchange)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeConnectServer(listener, exchange);
        }

        public async ValueTask DisposeAsync()
        {
            await this._stopping.CancelAsync();
            this._listener.Stop();
            try
            {
                // Bounded: a script that is deliberately still waiting - the "never answers" case -
                // must not add its own delay to the test run. The listener is already stopped.
                await this._serving.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                // The script is expected to be cut off when the test finishes with it.
            }

            this._stopping.Dispose();
        }

        private async Task ServeAsync(Func<NetworkStream, Task> exchange)
        {
            using var client = await this._listener.AcceptTcpClientAsync(this._stopping.Token);
            await using var stream = client.GetStream();
            await exchange(stream);
        }
    }
}
