using System.Net.Sockets;
using Microsoft.Extensions.Options;
using MuSite.Services;

namespace MuSite.Live;

/// <summary>
/// Answers "is the game up, and how busy is it?" every 30 seconds, and records the answer.
///
/// THREE SOURCES, DELIBERATELY LAYERED
///
///   1. The connect server's server list, spoken as a game client would. No credential, gives a
///      load percentage per game server, and proves the port that players actually use is
///      answering - not merely that something is listening on it.
///   2. A plain TCP connect, if the protocol exchange fails. A server mid-restart accepts the
///      connection before it can answer, and that is "up" for the purposes of the status strip.
///   3. OpenMU's /api/status, when an API key is configured, for the EXACT player count. The
///      connect server truncates load to whole percent of the player cap, so at the default cap of
///      1000 it cannot distinguish nine players from none.
///
/// Each layer degrades into the next, so the site keeps working with no key, with an old server
/// build that has no status API, and while the game is down.
///
/// IT IS A PROBE, NOT A SUBSCRIPTION, AND IT CLOSES WHAT IT OPENS
///
/// The connect server refuses new connections from an address that already holds
/// MaxConnectionsPerAddress (30) of them, and only releases one on disconnect. Every socket here is
/// in a `using`, and the interval is far longer than the exchange, so at most one is ever open.
/// </summary>
public sealed class ServerProbe(
    IOptionsMonitor<SiteOptions> options,
    OpenMuStatusClient status,
    ServerMetrics metrics,
    ILogger<ServerProbe> logger) : BackgroundService
{
    /// <summary>
    /// Matches Vector's host metrics scrape interval, so the game series and the VPS series land on
    /// the same buckets and can be read against each other on one screen.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Budget for one connect-server exchange.</summary>
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);

    private volatile ProbeState _state = new(false, null, "not checked yet");

    /// <summary>The most recent probe result.</summary>
    public ProbeState State => this._state;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var state = await this.ProbeAsync(stoppingToken).ConfigureAwait(false);
                this._state = state;
                await this.RecordAsync(state, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A probe that throws must not end the loop: the strip would then freeze on
                // whatever it last said, which for an "Online" badge is worse than saying nothing.
                logger.LogError(ex, "The server probe failed. It will try again in {Seconds}s.", Interval.TotalSeconds);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Takes one measurement across all configured endpoints.</summary>
    private async Task<ProbeState> ProbeAsync(CancellationToken cancellationToken)
    {
        var endpoints = options.CurrentValue.ProbeEndpoints;
        if (endpoints.Length == 0)
        {
            return new ProbeState(false, DateTimeOffset.UtcNow, "no probe endpoints configured");
        }

        foreach (var endpoint in endpoints)
        {
            var parts = endpoint.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            {
                logger.LogWarning("Ignoring malformed probe endpoint {Endpoint} - expected host:port.", endpoint);
                continue;
            }

            var host = parts[0];

            // Layer 1: the real protocol.
            try
            {
                var servers = await ConnectServerClient
                    .ListServersAsync(host, port, ExchangeTimeout, cancellationToken)
                    .ConfigureAwait(false);

                if (servers.Count > 0)
                {
                    // The busiest server is the one that matters for "is it overworked"; an average
                    // across a quiet second server would hide the first one filling up.
                    var load = servers.Max(server => server.LoadPercent);
                    var players = await status.GetPlayerCountAsync(cancellationToken).ConfigureAwait(false);
                    return new ProbeState(true, DateTimeOffset.UtcNow, null, players, load, servers.Count);
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException
                                       or InvalidDataException or IOException or EndOfStreamException)
            {
                // Falls through to the plain connect below, which distinguishes "the port is shut"
                // from "the port answered but the exchange did not finish".
            }

            // Layer 2: is anything listening at all?
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ExchangeTimeout);
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

                var players = await status.GetPlayerCountAsync(cancellationToken).ConfigureAwait(false);
                return new ProbeState(
                    true, DateTimeOffset.UtcNow, "connected, but the server list request was not answered",
                    players, null, null);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // Try the next endpoint; only report down when none answer.
            }
        }

        return new ProbeState(false, DateTimeOffset.UtcNow, "no configured endpoint accepted a connection");
    }

    /// <summary>Stores the measurement, without letting a database problem stop the probing.</summary>
    private async Task RecordAsync(ProbeState state, CancellationToken cancellationToken)
    {
        try
        {
            await metrics.WriteSampleAsync(
                new ServerSample(state.IsUp, state.Players, state.LoadPercent, state.Servers),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The strip on the front page is fed from memory and stays correct; only the history
            // loses a point. Logged at warning so a table that is missing after a deploy is visible
            // without being alarming.
            logger.LogWarning(ex, "Could not record the server sample.");
        }
    }
}

/// <summary>The result of the last probe.</summary>
/// <param name="IsUp">Whether any configured endpoint accepted a TCP connection.</param>
/// <param name="CheckedAt">When the probe last ran, or null before the first run.</param>
/// <param name="Detail">Why the probe reports down or degraded, when it does.</param>
/// <param name="Players">Exact players online, or null when no API key is configured.</param>
/// <param name="LoadPercent">Busiest game server's load percentage, or null.</param>
/// <param name="Servers">How many game servers the connect server listed, or null.</param>
public sealed record ProbeState(
    bool IsUp,
    DateTimeOffset? CheckedAt,
    string? Detail,
    int? Players = null,
    int? LoadPercent = null,
    int? Servers = null);
