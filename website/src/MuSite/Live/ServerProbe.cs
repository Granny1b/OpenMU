using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace MuSite.Live;

/// <summary>
/// Answers "is the game up?" with a TCP connect to the connect server, every 15 seconds.
///
/// It is a probe rather than a call to OpenMU's /api/status because the website is never given an
/// API key: a key that could read the API is a key that sits in this container, and the site does
/// not need one to draw an Online/Offline strip. The endpoints are configuration, not constants,
/// because the deploy variants publish different ports - the all-in-one compose publishes 44405 and
/// 44406, the Traefik one publishes only 44405, so a hardcoded pair reports "offline" forever on
/// the wrong variant.
/// </summary>
public sealed class ServerProbe(IOptionsMonitor<SiteOptions> options, ILogger<ServerProbe> logger) : BackgroundService
{
    private volatile ProbeState _state = new(false, null, "not checked yet");

    /// <summary>The most recent probe result.</summary>
    public ProbeState State => this._state;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            this._state = await this.ProbeAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

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

            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                await client.ConnectAsync(parts[0], port, timeout.Token).ConfigureAwait(false);

                // One reachable endpoint is enough to call the server up.
                return new ProbeState(true, DateTimeOffset.UtcNow, null);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // Try the next endpoint; only report down when none answer.
            }
        }

        return new ProbeState(false, DateTimeOffset.UtcNow, "no configured endpoint accepted a connection");
    }
}

/// <summary>The result of the last probe.</summary>
/// <param name="IsUp">Whether any configured endpoint accepted a TCP connection.</param>
/// <param name="CheckedAt">When the probe last ran, or null before the first run.</param>
/// <param name="Detail">Why the probe reports down, when it does.</param>
public sealed record ProbeState(bool IsUp, DateTimeOffset? CheckedAt, string? Detail);
