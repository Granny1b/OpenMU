using Dapper;
using MuSite.Auth;
using MuSite.Data;
using MuSite.Services;

namespace MuSite.Game;

/// <summary>One row of the admin account search.</summary>
public sealed record AccountSearchRow(Guid AccountId, string LoginName, int State, DateTime Registered, int Characters);

/// <summary>Everything the admin account page shows.</summary>
public sealed record AccountDetail(
    Guid AccountId, string LoginName, string Email, DateTime Registered, int State,
    DateTime? ChatBanUntil, IReadOnlyList<OwnCharacter> Characters);

/// <summary>An entry from the site's own ban history.</summary>
public sealed record BanRecord(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LiftedAt, int PriorState, string Reason, string Actor);

/// <summary>Why an administrative action was refused.</summary>
public enum AdminActionResult
{
    /// <summary>The action was applied.</summary>
    Done,

    /// <summary>No account with that id.</summary>
    NotFound,

    /// <summary>The target is an administrator or the owner.</summary>
    Protected,

    /// <summary>The acting administrator did not confirm with their own password.</summary>
    NotConfirmed,

    /// <summary>Nothing to do - already banned, or not banned.</summary>
    NoChange,
}

/// <summary>
/// The administrative writes, all of them keyed on the account's uuid.
///
/// NEVER on the login name. data."Account"."LoginName" carries a case-SENSITIVE unique index, so
/// 'Valdrenn' and 'valdrenn' can be two different accounts, while the admin search matches with
/// ILIKE. A guard that resolved the name case-insensitively and an UPDATE that resolved it
/// case-sensitively would act on different rows - the guard clears one account and the write lands
/// on another. Resolving to an id once, up front, removes the entire class of bug.
/// </summary>
public sealed class AdminActions(
    SiteDataSources sources,
    RoleResolver roles,
    SessionStore sessions,
    SessionState sessionState,
    AuditLog audit,
    ILogger<AdminActions> logger)
{
    /// <summary>Searches accounts by login name prefix, or by the name of a character on them.</summary>
    public async Task<IReadOnlyList<AccountSearchRow>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT a."Id"               AS AccountId,
                   a."LoginName"        AS LoginName,
                   a."State"            AS State,
                   a."RegistrationDate" AS Registered,
                   (SELECT count(*)::int FROM data."Character" c WHERE c."AccountId" = a."Id") AS Characters
              FROM data."Account" a
             WHERE lower(a."LoginName") LIKE lower(@query) || '%'
                OR EXISTS (SELECT 1 FROM data."Character" c
                            WHERE c."AccountId" = a."Id" AND lower(c."Name") LIKE lower(@query) || '%')
             ORDER BY a."LoginName"
             LIMIT @limit
            """;

        await using var connection = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AccountSearchRow>(
            new CommandDefinition(sql, new { query, limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>Reads one account by id, with its characters.</summary>
    public async Task<AccountDetail?> GetAsync(Guid accountId, GameAccount accounts, CancellationToken cancellationToken)
    {
        await using var connection = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var account = await connection.QuerySingleOrDefaultAsync<AccountHeader>(new CommandDefinition(
            """
            SELECT "Id" AS AccountId, "LoginName", "EMail" AS Email, "RegistrationDate" AS Registered,
                   "State", "ChatBanUntil"
              FROM data."Account" WHERE "Id" = @accountId
            """,
            new { accountId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (account is null)
        {
            return null;
        }

        var characters = await accounts.GetOwnCharactersAsync(accountId, cancellationToken).ConfigureAwait(false);

        return new AccountDetail(
            account.AccountId, account.LoginName, account.Email, account.Registered,
            account.State, account.ChatBanUntil, characters);
    }

    /// <summary>The site's ban history for an account.</summary>
    public async Task<IReadOnlyList<BanRecord>> GetBanHistoryAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<BanRecord>(new CommandDefinition(
            """
            SELECT id AS Id, created_at AS CreatedAt, expires_at AS ExpiresAt, lifted_at AS LiftedAt,
                   prior_state AS PriorState, reason AS Reason, actor AS Actor
              FROM web_ban WHERE account_id = @accountId ORDER BY created_at DESC LIMIT 20
            """,
            new { accountId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>
    /// Bans an account. <paramref name="until"/> null means permanent.
    ///
    /// The web_ban row is written FIRST and the State second. If the second write fails you get an
    /// expiry record with no ban - visible on the admin page and harmless - rather than a permanent
    /// ban with nothing recording when it should end or what state to restore.
    /// </summary>
    public async Task<AdminActionResult> BanAsync(Guid accountId, DateTimeOffset? until, string reason, AdminContext actor, CancellationToken cancellationToken)
    {
        var target = await this.ResolveTargetAsync(accountId, actor, cancellationToken).ConfigureAwait(false);
        if (target.Result != AdminActionResult.Done)
        {
            return target.Result;
        }

        if (GameEnums.AccountState.IsBanned(target.State))
        {
            return AdminActionResult.NoChange;
        }

        var newState = until is null ? GameEnums.AccountState.Banned : GameEnums.AccountState.TemporarilyBanned;

        await using (var site = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await site.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO web_ban (id, account_id, login_name, prior_state, reason, expires_at, actor)
                VALUES (@id, @accountId, @loginName, @priorState, @reason, @expiresAt, @actor)
                """,
                new
                {
                    id = Guid.NewGuid(),
                    accountId,
                    loginName = target.LoginName,
                    priorState = target.State,
                    reason,
                    expiresAt = until,
                    actor = actor.LoginName,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await this.SetStateAsync(accountId, newState, cancellationToken).ConfigureAwait(false);
        await this.CutOffAsync(accountId, cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync(until is null ? "ban.permanent" : "ban.temporary", actor.LoginName, accountId,
            target.LoginName, actor.Ip,
            until is null ? reason : $"{reason} (until {until:u})", cancellationToken).ConfigureAwait(false);

        logger.LogWarning("{Actor} banned {Target} until {Until}.", actor.LoginName, target.LoginName, until?.ToString("u") ?? "forever");
        return AdminActionResult.Done;
    }

    /// <summary>
    /// Lifts a ban, restoring the state recorded when it was placed.
    ///
    /// Restoring the RECORDED state rather than writing 0: a game master who was banned would
    /// otherwise come back as an ordinary player, silently losing their role.
    /// </summary>
    public async Task<AdminActionResult> UnbanAsync(Guid accountId, AdminContext actor, CancellationToken cancellationToken)
    {
        var target = await this.ResolveTargetAsync(accountId, actor, cancellationToken).ConfigureAwait(false);
        if (target.Result != AdminActionResult.Done)
        {
            return target.Result;
        }

        if (!GameEnums.AccountState.IsBanned(target.State))
        {
            return AdminActionResult.NoChange;
        }

        int restoreTo;
        await using (var site = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var prior = await site.ExecuteScalarAsync<int?>(new CommandDefinition(
                """
                UPDATE web_ban SET lifted_at = now()
                 WHERE account_id = @accountId AND lifted_at IS NULL
                RETURNING prior_state
                """,
                new { accountId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            // No record means the ban was placed elsewhere - in the OpenMU panel, or straight in the
            // database. Normal is the only safe assumption then.
            restoreTo = prior ?? GameEnums.AccountState.Normal;
        }

        await this.SetStateAsync(accountId, restoreTo, cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync("ban.lifted", actor.LoginName, accountId, target.LoginName, actor.Ip,
            $"restored to state {restoreTo}", cancellationToken).ConfigureAwait(false);

        return AdminActionResult.Done;
    }

    /// <summary>
    /// Sets a new random password and returns it. Owner only - it is the one action that hands
    /// somebody else's account to whoever is looking at the screen.
    /// </summary>
    public async Task<(AdminActionResult Result, string? Password)> ResetPasswordAsync(Guid accountId, AdminContext actor, CancellationToken cancellationToken)
    {
        if (actor.Role != SiteRole.Owner)
        {
            return (AdminActionResult.Protected, null);
        }

        var target = await this.ResolveTargetAsync(accountId, actor, cancellationToken).ConfigureAwait(false);
        if (target.Result != AdminActionResult.Done)
        {
            return (target.Result, null);
        }

        var password = GeneratePassword();

        await using (var auth = await sources.GameAuth.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await auth.ExecuteAsync(new CommandDefinition(
                """UPDATE data."Account" SET "PasswordHash" = @hash WHERE "Id" = @accountId""",
                new { accountId, hash = BCrypt.Net.BCrypt.HashPassword(password) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await this.CutOffAsync(accountId, cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync("password.reset", actor.LoginName, accountId, target.LoginName, actor.Ip,
            "reset by owner", cancellationToken).ConfigureAwait(false);

        return (AdminActionResult.Done, password);
    }

    /// <summary>Counts accounts registered today and active bans, for the dashboard.</summary>
    public async Task<(int Today, int Week, int ActiveBans)> GetDashboardCountsAsync(CancellationToken cancellationToken)
    {
        RegistrationCounts counts;
        await using (var game = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            // A named shape, not a ValueTuple: Dapper cannot map to positional tuples at all. It
            // matches a record's constructor parameters to the reader's columns BY POSITION,
            // comparing names pairwise at each index - so the column order here has to stay in
            // RegistrationCounts' declaration order (Today, Week), not merely carry both names.
            counts = await game.QuerySingleAsync<RegistrationCounts>(new CommandDefinition(
                """
                SELECT count(*) FILTER (WHERE "RegistrationDate" >= date_trunc('day', now() AT TIME ZONE 'UTC'))::int AS Today,
                       count(*) FILTER (WHERE "RegistrationDate" >= now() - interval '7 days')::int                  AS Week
                  FROM data."Account"
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await using var site = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var bans = await site.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM web_ban WHERE lifted_at IS NULL",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (counts.Today, counts.Week, bans);
    }

    /// <summary>Which of OpenMU's seeded development accounts still exist, for the dashboard warning.</summary>
    public async Task<IReadOnlyList<string>> GetSurvivingSeedAccountsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            """SELECT "LoginName" FROM data."Account" WHERE "LoginName" = ANY(@names) ORDER BY "LoginName" """,
            new { names = SeedAccounts.All.ToArray() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>A readable random password for the owner to hand over.</summary>
    private static string GeneratePassword()
    {
        // No look-alike characters: this gets read off a screen and typed into a game client.
        const string alphabet = "abcdefghijkmnopqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        return string.Concat(bytes.Select(b => alphabet[b % alphabet.Length]));
    }

    /// <summary>
    /// Resolves the target and refuses protected ones.
    ///
    /// An administrator may not act on another administrator; only the owner may. Nobody may act on
    /// the owner - the lever for a rogue administrator is removing them from MUSITE_ADMINS, which
    /// lives in configuration rather than in a form anybody can post to.
    /// </summary>
    private async Task<(AdminActionResult Result, string LoginName, int State)> ResolveTargetAsync(Guid accountId, AdminContext actor, CancellationToken cancellationToken)
    {
        await using var connection = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var target = await connection.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition(
            """SELECT "LoginName", "State" FROM data."Account" WHERE "Id" = @accountId""",
            new { accountId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (target is null)
        {
            return (AdminActionResult.NotFound, string.Empty, 0);
        }

        var targetRole = roles.Resolve(target.LoginName, target.State);

        if (targetRole == SiteRole.Owner)
        {
            return (AdminActionResult.Protected, target.LoginName, target.State);
        }

        if (targetRole == SiteRole.Admin && actor.Role != SiteRole.Owner)
        {
            return (AdminActionResult.Protected, target.LoginName, target.State);
        }

        return (AdminActionResult.Done, target.LoginName, target.State);
    }

    private async Task SetStateAsync(Guid accountId, int state, CancellationToken cancellationToken)
    {
        await using var connection = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """UPDATE data."Account" SET "State" = @state WHERE "Id" = @accountId""",
            new { accountId, state },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends every website session of an account and drops them from the revalidation cache, so the
    /// action takes effect on the target's very next request rather than within a minute.
    ///
    /// It does NOT disconnect them from the GAME. ChatCommandPlugInBase disconnects the player
    /// before writing the state; the website can only write the state, so an in-game ban takes hold
    /// at the player's next login. There is no lever here to change that.
    /// </summary>
    private async Task CutOffAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var site = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var ids = await site.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM web_session WHERE account_id = @accountId AND revoked_at IS NULL",
            new { accountId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await sessions.RevokeAllAsync(accountId, null, cancellationToken).ConfigureAwait(false);

        foreach (var id in ids)
        {
            sessionState.Evict(id);
        }
    }

    private sealed record AccountHeader(Guid AccountId, string LoginName, string Email, DateTime Registered, int State, DateTime? ChatBanUntil);

    private sealed record TargetRow(string LoginName, int State);

    private sealed record RegistrationCounts(int Today, int Week);
}

/// <summary>Who is performing an administrative action.</summary>
/// <param name="LoginName">The acting administrator.</param>
/// <param name="Role">Their resolved website role.</param>
/// <param name="Ip">Their address, for the audit row.</param>
public sealed record AdminContext(string LoginName, SiteRole Role, string? Ip);
