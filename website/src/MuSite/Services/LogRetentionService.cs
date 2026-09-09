using Dapper;
using Microsoft.Extensions.Options;
using MuSite.Data;

namespace MuSite.Services;

/// <summary>
/// Removes server_log rows older than the retention window.
///
/// Without this the table only grows. A busy server writes every warning and error the game logs,
/// and a table nobody prunes eventually fills the VPS disk - at which point PostgreSQL stops
/// accepting writes and the game server stops with it. A log system that can take the server down
/// is worse than no log system.
///
/// This is why server_log is the ONE table mu_web_app may DELETE from. audit_log keeps its
/// no-DELETE guarantee: that is the record of who did what, and it is not operational telemetry
/// with a retention policy. See db/web/002_server_log.sql.
/// </summary>
public sealed class LogRetentionService(
    SiteDataSources sources,
    IOptionsMonitor<SiteOptions> options,
    ILogger<LogRetentionService> logger) : BackgroundService
{
    /// <summary>
    /// Deleted per pass. A single unbounded DELETE over weeks of rows takes a long lock and bloats
    /// the table; a bounded one leaves the rest for the next hour, and the table stays usable while
    /// it works through a backlog.
    /// </summary>
    private const int BatchSize = 20_000;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hourly. The window is measured in days, so anything finer is churn for nothing.
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await this.PruneAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately not fatal. If the log table cannot be pruned the site must keep
                // working - the consequence is disk, and the operator needs the site up to see it.
                logger.LogError(ex, "Could not prune the server log.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        var days = options.CurrentValue.LogRetentionDays;
        if (days <= 0)
        {
            // 0 means "keep everything", for someone who prunes by another route. Say so once an
            // hour rather than silently doing nothing, because unbounded growth is a real risk.
            logger.LogDebug("Server log retention is disabled (LogRetentionDays = {Days}).", days);
            return;
        }

        await using var site = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // ExecuteAsync, not ExecuteScalarAsync: the affected-row count is what "how many did we
        // prune" means. A scalar over a RETURNING clause would hand back the first row's value.
        var deleted = await site.ExecuteAsync(new CommandDefinition(
            """
            WITH old AS (
                SELECT id FROM server_log
                 WHERE at < now() - make_interval(days => @days)
                 ORDER BY at
                 LIMIT @batch
            )
            DELETE FROM server_log WHERE id IN (SELECT id FROM old)
            """,
            new { days, batch = BatchSize },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (deleted > 0)
        {
            logger.LogInformation(
                "Pruned {Deleted} server log row(s) older than {Days} day(s).", deleted, days);
        }
    }
}
