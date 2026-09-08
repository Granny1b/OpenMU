using Dapper;
using Microsoft.Extensions.Options;
using MuSite.Data;

namespace MuSite.Services;

/// <summary>
/// The ceiling on how many accounts can be created in a day, on top of the per-IP rate limit.
///
/// A per-IP limit bounds one visitor, not the total: a botnet, or one person with a proxy pool,
/// walks straight past it. And the write role deliberately has no DELETE on data."Account", so the
/// site cannot clean up what it creates - an unbounded flood would have to be cleared out by hand,
/// in the game database.
///
/// So the daily total is counted, and past the cap the site closes registration itself and records
/// why. Reopening is a deliberate act by the owner on the admin dashboard.
/// </summary>
public sealed class RegistrationThrottle(
    SiteDataSources sources,
    SiteSettings settings,
    AuditLog audit,
    IOptionsMonitor<SiteOptions> options,
    ILogger<RegistrationThrottle> logger)
{
    /// <summary>Whether registration is currently accepting accounts.</summary>
    public Task<bool> IsOpenAsync(CancellationToken cancellationToken)
        => settings.GetBoolAsync(SiteSettings.RegistrationOpen, fallback: false, cancellationToken);

    /// <summary>How many accounts were registered today, from the game database itself.</summary>
    public async Task<int> CountTodayAsync(CancellationToken cancellationToken)
    {
        await using var connection = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*)::int FROM data."Account"
             WHERE "RegistrationDate" >= date_trunc('day', now() AT TIME ZONE 'UTC')
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Called after a successful registration. Closes registration when the day's cap is reached.
    /// Counting from data."Account" rather than an internal counter means a restart cannot reset it.
    /// </summary>
    public async Task NoteRegistrationAsync(string loginName, string? ip, CancellationToken cancellationToken)
    {
        await audit.WriteAsync("registration", loginName, null, loginName, ip, "self-service", cancellationToken).ConfigureAwait(false);

        var cap = options.CurrentValue.RegistrationDailyCap;
        if (cap <= 0)
        {
            return;
        }

        var today = await this.CountTodayAsync(cancellationToken).ConfigureAwait(false);
        if (today < cap)
        {
            return;
        }

        await settings.SetBoolAsync(SiteSettings.RegistrationOpen, false, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync("registration.auto_closed", "system", null, null, ip,
            $"{today} accounts today reached the cap of {cap}", cancellationToken).ConfigureAwait(false);

        logger.LogWarning("Registration closed automatically: {Today} accounts today, cap {Cap}.", today, cap);
    }
}
