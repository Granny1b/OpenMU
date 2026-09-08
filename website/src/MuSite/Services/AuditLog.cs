using Dapper;
using MuSite.Data;

namespace MuSite.Services;

/// <summary>
/// The append-only record of who did what.
///
/// Append-only is enforced by the database, not by this class: mu_web_app holds INSERT and SELECT on
/// audit_log and is NOT its owner, so it cannot grant itself DELETE or TRUNCATE. A runtime that owned
/// its own audit table would have no append-only guarantee at all.
/// </summary>
public sealed class AuditLog(SiteDataSources sources, ILogger<AuditLog> logger)
{
    /// <summary>Records one action. Never throws: an audit failure must not undo the action.</summary>
    public async Task WriteAsync(string action, string actor, Guid? targetId, string? targetName, string? ip, string detail, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO audit_log (actor, action, target_id, target_name, detail, ip)
                VALUES (@actor, @action, @targetId, @targetName, @detail, @ip::inet)
                """,
                new { actor, action, targetId, targetName, detail, ip },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Losing an audit row is bad; losing the ban that the row was about is worse. Log loudly
            // and let the caller's work stand.
            logger.LogError(ex, "Could not write audit row {Action} by {Actor}.", action, actor);
        }
    }

    /// <summary>Reads the most recent entries for the admin dashboard.</summary>
    public async Task<IReadOnlyList<AuditEntry>> RecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AuditEntry>(new CommandDefinition(
            """
            SELECT at AS At, actor AS Actor, action AS Action, target_name AS TargetName, detail AS Detail
              FROM audit_log ORDER BY at DESC LIMIT @limit
            """,
            new { limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }
}

/// <summary>One row of the audit log.</summary>
public sealed record AuditEntry(DateTimeOffset At, string Actor, string Action, string? TargetName, string Detail);
