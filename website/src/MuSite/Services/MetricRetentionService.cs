using Dapper;
using Microsoft.Extensions.Options;
using MuSite.Data;

namespace MuSite.Services;

/// <summary>
/// Removes host_metric and server_sample rows older than the retention window.
///
/// Same reasoning as <see cref="LogRetentionService"/>, and the same consequence if it is missing:
/// the measurements arrive every thirty seconds forever, and a full disk stops PostgreSQL, which
/// stops the game. Monitoring that eventually takes the server down would be a poor trade.
///
/// The two tables are pruned in one pass because they share a retention setting and a schedule -
/// they are two halves of one dashboard, and keeping VPS history longer than the game history it is
/// read against would only produce graphs that stop at different places.
/// </summary>
public sealed class MetricRetentionService(
    SiteDataSources sources,
    IOptionsMonitor<SiteOptions> options,
    ILogger<MetricRetentionService> logger) : BackgroundService
{
    /// <summary>
    /// Rows deleted per table per pass. Higher than the log's batch because these rows are four
    /// small columns rather than a message and a stack trace, and there are far more of them.
    /// </summary>
    private const int BatchSize = 50_000;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await this.PruneAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not prune the metric tables.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        var days = options.CurrentValue.MetricsRetentionDays;
        if (days <= 0)
        {
            logger.LogDebug("Metric retention is disabled (MetricsRetentionDays = {Days}).", days);
            return;
        }

        await using var site = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var metrics = await PruneTableAsync(site, "host_metric", days, cancellationToken).ConfigureAwait(false);
        var samples = await PruneTableAsync(site, "server_sample", days, cancellationToken).ConfigureAwait(false);

        if (metrics + samples > 0)
        {
            logger.LogInformation(
                "Pruned {Metrics} measurement(s) and {Samples} server sample(s) older than {Days} day(s).",
                metrics, samples, days);
        }
    }

    /// <summary>
    /// Deletes one bounded batch of old rows from a metric table.
    /// </summary>
    /// <remarks>
    /// The delete is driven by ctid rather than a primary key because neither table has one - they
    /// are append-only time series where a surrogate key would be four bytes of index per row and
    /// buy nothing. ctid is the physical row address, which is exactly what a bounded DELETE needs.
    ///
    /// The table name is interpolated, and that is safe ONLY because both call sites pass a
    /// literal. It never touches user input, and must not start to.
    /// </remarks>
    private static Task<int> PruneTableAsync(
        Npgsql.NpgsqlConnection site, string table, int days, CancellationToken cancellationToken)
    {
        return site.ExecuteAsync(new CommandDefinition(
            $"""
             WITH old AS (
                 SELECT ctid FROM {table}
                  WHERE at < now() - make_interval(days => @days)
                  LIMIT @batch
             )
             DELETE FROM {table} WHERE ctid IN (SELECT ctid FROM old)
             """,
            new { days, batch = BatchSize },
            cancellationToken: cancellationToken));
    }
}
