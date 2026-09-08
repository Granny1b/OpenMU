using Dapper;
using Microsoft.Extensions.Options;
using MuSite.Data;

namespace MuSite.Game;

/// <summary>Why a registration attempt was refused.</summary>
public enum RegisterResult
{
    /// <summary>The account was created.</summary>
    Created,

    /// <summary>The login name is not usable.</summary>
    InvalidName,

    /// <summary>The password does not meet the length rules.</summary>
    InvalidPassword,

    /// <summary>The name is taken, or differs from a taken one only by capitalisation.</summary>
    NameTaken,

    /// <summary>Registration is closed.</summary>
    Closed,
}

/// <summary>The outcome of a sign-in attempt.</summary>
/// <param name="Success">Whether the credentials were correct.</param>
/// <param name="AccountId">The account, when the credentials were correct.</param>
/// <param name="LoginName">The stored login name, in its stored capitalisation.</param>
/// <param name="State">The account state, for the ban check and role resolution.</param>
public sealed record LoginResult(bool Success, Guid AccountId, string LoginName, int State);

/// <summary>
/// Account reads and writes against the GAME database.
///
/// Everything here re-implements an invariant that OpenMU normally enforces by living on the same
/// side of a compiler boundary. Each one carries the file and line it mirrors, because several fail
/// SILENTLY and PERMANENTLY rather than loudly:
///
///   * BCrypt with the library default work factor - the game verifies with BCrypt.Verify
///     (AccountRepository.cs:125) and hashes the same way (AccountInitializerBase.cs:92). A
///     mismatched algorithm creates an account that can never log in, with no error anywhere.
///   * SecurityCode MUST be empty. DeleteCharacterAction.cs:48 reads
///     `checkAsPassword = string.IsNullOrEmpty(SecurityCode)`: empty means character deletion asks
///     for the password, which is what a player expects. Writing anything there - a hash especially
///     - means the player must type that exact string to delete a character, forever.
///   * VaultPassword MUST be empty. Player.cs:266 sets IsVaultLocked from
///     `!string.IsNullOrWhiteSpace(VaultPassword)` and UnlockVaultAction compares it as PLAINTEXT,
///     so anything written there locks the new player's vault behind a PIN they never chose.
///   * The password may not exceed the client's field width. Longer input is truncated by the
///     client before it is sent, so the stored hash could never verify again.
///
/// The vault is deliberately NOT created here: TalkNpcAction.cs:100 does
/// `Account.Vault ??= CreateNew&lt;ItemStorage&gt;()` when the player first opens it, so registration
/// stays a single-row insert and the write role needs no access to data."ItemStorage".
/// Nor are UnlockedCharacterClasses seeded - ShowCharacterListPlugIn.cs:55 aggregates them from a
/// seed of 0, so an empty set is correct, and classes are unlocked by level in game.
/// </summary>
public sealed class GameAccount(SiteDataSources sources, IOptionsMonitor<SiteOptions> options, ILogger<GameAccount> logger)
{
    /// <summary>
    /// A valid BCrypt hash of a value nobody knows, verified against on the account-not-found path so
    /// a wrong name and a wrong password cost the same time. Without it, "no such account" returns in
    /// microseconds while a real account spends the full BCrypt work factor, and account names can be
    /// enumerated with a stopwatch.
    /// </summary>
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());

    /// <summary>Creates a game account. Returns <see cref="RegisterResult.Created"/> on success.</summary>
    public async Task<RegisterResult> RegisterAsync(string loginName, string password, string? email, CancellationToken cancellationToken)
    {
        var config = options.CurrentValue;

        if (!IsValidLoginName(loginName) || SeedAccounts.IsSeedName(loginName))
        {
            return RegisterResult.InvalidName;
        }

        if (password.Length < config.MinPassword || password.Length > config.MaxPassword)
        {
            return RegisterResult.InvalidPassword;
        }

        await using var connection = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Case-INSENSITIVE duplicate check, although the unique index is case-sensitive. 'Valdrenn'
        // and 'valdrenn' would be two different accounts to the game, which is a ready-made
        // impersonation and an endless source of "that is not my account" reports.
        var taken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """SELECT EXISTS (SELECT 1 FROM data."Account" WHERE lower("LoginName") = lower(@loginName))""",
            new { loginName },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (taken)
        {
            return RegisterResult.NameTaken;
        }

        const string insert =
            """
            INSERT INTO data."Account"
                ("Id", "LoginName", "PasswordHash", "SecurityCode", "EMail", "RegistrationDate",
                 "State", "TimeZone", "VaultPassword", "IsVaultExtended", "IsBot", "IsTemplate",
                 "LanguageIsoCode")
            VALUES
                (@id, @loginName, @passwordHash, '', @email, @registered,
                 0, 0, '', false, false, false,
                 'en')
            """;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(insert, new
            {
                id = Guid.NewGuid(),
                loginName,
                passwordHash = BCrypt.Net.BCrypt.HashPassword(password),
                email = email ?? string.Empty,     // "EMail" is NOT NULL; empty means "not given"
                registered = DateTime.UtcNow,      // timestamptz - Kind must be Utc
            }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            // Lost a race against a concurrent registration. The check above and this insert are two
            // statements, so only a unique index actually closes the window - and for the
            // case-INSENSITIVE case that has to be ux_account_loginname_lower, created by
            // db/02-indexes.sql, because OpenMU's own index on "LoginName" is case-sensitive and
            // would happily accept 'Valdrenn' alongside 'valdrenn'.
            return RegisterResult.NameTaken;
        }

        logger.LogInformation("Registered account {LoginName}.", loginName);
        return RegisterResult.Created;
    }

    /// <summary>
    /// Verifies credentials. The comparison is CASE-SENSITIVE, exactly as the game's
    /// AccountRepository is, so an account that can sign in here can always sign in there.
    /// </summary>
    public async Task<LoginResult> LoginAsync(string loginName, string password, CancellationToken cancellationToken)
    {
        await using var connection = await sources.GameAuth.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var account = await connection.QuerySingleOrDefaultAsync<AccountCredentials>(new CommandDefinition(
            """SELECT "Id", "LoginName", "PasswordHash", "State" FROM data."Account" WHERE "LoginName" = @loginName""",
            new { loginName },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (account is null)
        {
            // Spend the same time as a real verification, then fail.
            BCrypt.Net.BCrypt.Verify(password, DummyHash);
            return new LoginResult(false, Guid.Empty, string.Empty, 0);
        }

        if (!BCrypt.Net.BCrypt.Verify(password, account.PasswordHash))
        {
            return new LoginResult(false, Guid.Empty, string.Empty, 0);
        }

        return new LoginResult(true, account.Id, account.LoginName, account.State);
    }

    /// <summary>Changes a password after verifying the current one.</summary>
    public async Task<bool> ChangePasswordAsync(Guid accountId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var config = options.CurrentValue;
        if (newPassword.Length < config.MinPassword || newPassword.Length > config.MaxPassword)
        {
            return false;
        }

        await using var connection = await sources.GameAuth.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var hash = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """SELECT "PasswordHash" FROM data."Account" WHERE "Id" = @accountId""",
            new { accountId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (hash is null || !BCrypt.Net.BCrypt.Verify(currentPassword, hash))
        {
            return false;
        }

        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """UPDATE data."Account" SET "PasswordHash" = @newHash WHERE "Id" = @accountId""",
            new { accountId, newHash = BCrypt.Net.BCrypt.HashPassword(newPassword) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return updated == 1;
    }

    /// <summary>Reads the account overview shown on /account.</summary>
    public async Task<AccountOverview?> GetOverviewAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<AccountOverview>(new CommandDefinition(
            """
            SELECT "Id" AS AccountId, "LoginName", "EMail" AS Email, "RegistrationDate" AS Registered,
                   "State", "ChatBanUntil"
              FROM data."Account"
             WHERE "Id" = @accountId
            """,
            new { accountId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Reads the characters on an account, for its owner.</summary>
    public async Task<IReadOnlyList<OwnCharacter>> GetOwnCharactersAsync(Guid accountId, CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH stats AS (
                SELECT sa."CharacterId" AS cid,
                       MAX(sa."Value") FILTER (WHERE sa."DefinitionId" = @levelId)  AS lvl,
                       MAX(sa."Value") FILTER (WHERE sa."DefinitionId" = @masterId) AS mlvl,
                       MAX(sa."Value") FILTER (WHERE sa."DefinitionId" = @resetId)  AS resets
                  FROM data."StatAttribute" sa
                 WHERE sa."DefinitionId" IN (@levelId, @masterId, @resetId) AND sa."CharacterId" IS NOT NULL
                 GROUP BY sa."CharacterId"
            )
            SELECT c."Name"                     AS Name,
                   cl."Name"                    AS Class,
                   COALESCE(s.lvl, 0)::int      AS Level,
                   COALESCE(s.mlvl, 0)::int     AS MasterLevel,
                   COALESCE(s.resets, 0)::int   AS Resets,
                   g."Name"                     AS Guild
              FROM data."Character" c
              LEFT JOIN stats s                    ON s.cid = c."Id"
              LEFT JOIN config."CharacterClass" cl ON cl."Id" = c."CharacterClassId"
              LEFT JOIN guild."GuildMember" gm     ON gm."Id" = c."Id"
              LEFT JOIN guild."Guild" g            ON g."Id" = gm."GuildId"
             WHERE c."AccountId" = @accountId
             ORDER BY COALESCE(s.lvl, 0) DESC, c."Name" ASC
            """;

        await using var connection = await sources.GameReg.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<OwnCharacter>(new CommandDefinition(sql, new
        {
            accountId,
            levelId = StatIds.Level,
            masterId = StatIds.MasterLevel,
            resetId = StatIds.Resets,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>Reads just the account state, for session revalidation. Uses the read pool.</summary>
    public async Task<int?> GetStateAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """SELECT "State" FROM data."Account" WHERE "Id" = @accountId""",
            new { accountId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// A login name the game client can actually send: at most 10 characters (the column is
    /// varchar(10)), and ASCII letters and digits only. The client's input field is not Unicode-safe
    /// and a name it cannot reproduce is an account nobody can sign into.
    /// </summary>
    public static bool IsValidLoginName(string? loginName)
        => !string.IsNullOrWhiteSpace(loginName)
           && loginName.Length is >= 3 and <= 10
           && loginName.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');

    private sealed record AccountCredentials(Guid Id, string LoginName, string PasswordHash, int State);
}

/// <summary>What /account shows about the signed-in account.</summary>
public sealed record AccountOverview(Guid AccountId, string LoginName, string Email, DateTime Registered, int State, DateTime? ChatBanUntil);

/// <summary>One of the signed-in player's own characters.</summary>
public sealed record OwnCharacter(string Name, string? Class, int Level, int MasterLevel, int Resets, string? Guild);
