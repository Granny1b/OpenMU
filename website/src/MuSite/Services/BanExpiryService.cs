using Dapper;
using MuSite.Auth;
using MuSite.Data;
using MuSite.Game;

namespace MuSite.Services;

/// <summary>
/// Lifts temporary bans when they expire.
///
/// data."Account" has NO ban-expiry column, so TemporarilyBanned in stock OpenMU means "until a
/// human unbans". The expiry lives in the site's own web_ban table, which means the reinstatement
/// only happens while this container is running: if the site is down for a week, a one-day ban
/// lasts a week. That is a real limitation of putting the expiry outside the game's own schema, and
/// it is why the ban page tells the administrator the ban lifts "automatically" rather than at a
/// promised time.
/// </summary>
public sealed class BanExpiryService(
    SiteDataSources sources,
    SessionStore sessions,
    AuditLog audit,
    ILogger<BanExpiryService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await this.LiftExpiredAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not lift expired bans.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task LiftExpiredAsync(CancellationToken cancellationToken)
    {
        await using var site = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await site.QueryAsync<ExpiredBan>(new CommandDefinition(
            """
            UPDATE web_ban SET lifted_at = now()
             WHERE lifted_at IS NULL AND expires_at IS NOT NULL AND expires_at <= now()
            RETURNING account_id AS AccountId, login_name AS LoginName, prior_state AS PriorState, created_at AS CreatedAt
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Only the newest expired row of an account decides what to restore; an older one records
        // a state from before an earlier ban.
        var expired = rows
            .GroupBy(ban => ban.AccountId)
            .Select(bans => bans.OrderByDescending(ban => ban.CreatedAt).First())
            .ToList();

        if (expired.Count == 0)
        {
            return;
        }

        await using var game = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var ban in expired)
        {
            // Another ban which is still open - e.g. one placed after this one - is not ended by
            // this expiry. Restoring the state here would lift it early.
            var hasOpenBan = await site.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM web_ban WHERE account_id = @accountId AND lifted_at IS NULL)",
                new { accountId = ban.AccountId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (hasOpenBan)
            {
                logger.LogInformation(
                    "Ban record for {LoginName} expired, but another ban on the account is still open - left as is.",
                    ban.LoginName);
                continue;
            }

            // Only move the account if it is still in the temporary-ban state. If an administrator
            // has since made the ban permanent, or the account was changed by hand, the expiry must
            // not quietly undo that.
            var updated = await game.ExecuteAsync(new CommandDefinition(
                """
                UPDATE data."Account" SET "State" = @priorState
                 WHERE "Id" = @accountId AND "State" = @temporarilyBanned
                """,
                new { ban.AccountId, ban.PriorState, temporarilyBanned = GameEnums.AccountState.TemporarilyBanned },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (updated == 0)
            {
                logger.LogInformation(
                    "Ban record for {LoginName} expired but the account is no longer temporarily banned - left as is.",
                    ban.LoginName);
                continue;
            }

            await sessions.RevokeAllAsync(ban.AccountId, null, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync("ban.expired", "system", ban.AccountId, ban.LoginName, null,
                $"restored to state {ban.PriorState}", cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Temporary ban on {LoginName} expired; restored to state {State}.", ban.LoginName, ban.PriorState);
        }
    }

    private sealed record ExpiredBan(Guid AccountId, string LoginName, int PriorState, DateTimeOffset CreatedAt);
}
