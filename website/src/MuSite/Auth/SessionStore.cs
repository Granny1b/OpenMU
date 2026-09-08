using Dapper;
using MuSite.Data;

namespace MuSite.Auth;

/// <summary>A live session row.</summary>
/// <param name="Id">The session id carried in the cookie.</param>
/// <param name="AccountId">The signed-in account.</param>
/// <param name="LoginName">The account's login name, in its stored capitalisation.</param>
public sealed record SessionRow(Guid Id, Guid AccountId, string LoginName);

/// <summary>
/// Server-side sessions, in the site's own database.
///
/// The cookie carries a session id and nothing else. That is what makes a ban, a password change or
/// a revoked role take effect on the next request instead of whenever the cookie happens to expire:
/// with the identity baked into the cookie there would be no way to take it back for two weeks.
/// </summary>
public sealed class SessionStore(SiteDataSources sources)
{
    /// <summary>How long a session lives without being renewed.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>Opens a session and returns its id.</summary>
    public async Task<Guid> CreateAsync(Guid accountId, string loginName, string? ip, string? userAgent, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();

        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO web_session (id, account_id, login_name, expires_at, ip, user_agent)
            VALUES (@id, @accountId, @loginName, @expiresAt, @ip::inet, @userAgent)
            """,
            new
            {
                id,
                accountId,
                loginName,
                expiresAt = DateTimeOffset.UtcNow.Add(Lifetime),
                ip,
                userAgent = Truncate(userAgent, 400),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return id;
    }

    /// <summary>Reads a session if it is live, and touches its last-seen stamp.</summary>
    public async Task<SessionRow?> ValidateAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            """
            UPDATE web_session
               SET last_seen_at = now()
             WHERE id = @sessionId AND revoked_at IS NULL AND expires_at > now()
            RETURNING id AS Id, account_id AS AccountId, login_name AS LoginName
            """,
            new { sessionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Revokes one session - sign-out.</summary>
    public async Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE web_session SET revoked_at = now() WHERE id = @sessionId AND revoked_at IS NULL",
            new { sessionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes every session of an account, optionally sparing one.
    ///
    /// Called on a password change (sparing the session that changed it) and on a ban or admin
    /// password reset (sparing nothing) - a stolen session must not outlive the password that
    /// was changed because of it.
    /// </summary>
    public async Task<int> RevokeAllAsync(Guid accountId, Guid? exceptSessionId, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE web_session
               SET revoked_at = now()
             WHERE account_id = @accountId
               AND revoked_at IS NULL
               AND (@exceptSessionId::uuid IS NULL OR id <> @exceptSessionId::uuid)
            """,
            new { accountId, exceptSessionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Counts an account's other live sessions, for the "signed in elsewhere" line.</summary>
    public async Task<int> CountOtherAsync(Guid accountId, Guid currentSessionId, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*)::int FROM web_session
             WHERE account_id = @accountId AND id <> @currentSessionId
               AND revoked_at IS NULL AND expires_at > now()
            """,
            new { accountId, currentSessionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Deletes sessions that expired long enough ago to be of no further interest.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM web_session WHERE expires_at < now() - interval '30 days'",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>Purges long-expired sessions once an hour.</summary>
public sealed class SessionPurgeService(SessionStore sessions, ILogger<SessionPurgeService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var purged = await sessions.PurgeAsync(stoppingToken).ConfigureAwait(false);
                if (purged > 0)
                {
                    logger.LogInformation("Purged {Count} expired sessions.", purged);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Session purge failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
