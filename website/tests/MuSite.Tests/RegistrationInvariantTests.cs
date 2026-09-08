using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MuSite;
using MuSite.Data;
using MuSite.Game;
using Npgsql;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Registration writes a row that the GAME has to accept. Several of the invariants fail silently
/// and permanently if they are wrong - the account is created, the player sees no error, and
/// something is broken forever. Each test names the OpenMU line it protects.
///
/// Skipped when MUSITE_TEST_DB is unset.
/// </summary>
public sealed class RegistrationInvariantTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MUSITE_TEST_DB");

    private GameAccount _accounts = null!;
    private NpgsqlDataSource _dataSource = null!;

    private static bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        this._dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var command = this._dataSource.CreateCommand(await File.ReadAllTextAsync("StandInSchema.sql")))
        {
            await command.ExecuteNonQueryAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:GameRead"] = ConnectionString,
            ["ConnectionStrings:GameAuth"] = ConnectionString,
            ["ConnectionStrings:GameReg"] = ConnectionString,
            ["ConnectionStrings:Site"] = ConnectionString,
        }).Build();

        var settings = new SiteOptions { MinPassword = 8, MaxPassword = 20 };
        this._accounts = new GameAccount(
            SiteDataSources.Create(configuration),
            new StaticOptions(settings),
            NullLogger<GameAccount>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (Enabled)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task ARegisteredAccountSatisfiesEveryInvariantTheGameRequires()
    {
        Skip.IfNot(Enabled);

        Assert.Equal(RegisterResult.Created, await this._accounts.RegisterAsync("newbie", "hunter2hunter2", "a@b.c", default));

        await using var command = this._dataSource.CreateCommand(
            """
            SELECT "PasswordHash", "SecurityCode", "VaultPassword", "EMail", "LanguageIsoCode",
                   "State", "TimeZone", "IsBot", "IsTemplate", "IsVaultExtended", "VaultId",
                   "RegistrationDate"
              FROM data."Account" WHERE "LoginName" = 'newbie'
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        // BCrypt.Verify must accept it - AccountRepository.cs:125 is what the game runs at login.
        Assert.True(BCrypt.Net.BCrypt.Verify("hunter2hunter2", reader.GetString(0)));

        // DeleteCharacterAction.cs:48 reads `checkAsPassword = string.IsNullOrEmpty(SecurityCode)`.
        // Anything non-empty here means the player must type that exact string to delete a
        // character, forever, with no way to find out what it is.
        Assert.Equal(string.Empty, reader.GetString(1));

        // Player.cs:266 sets IsVaultLocked from !string.IsNullOrWhiteSpace(VaultPassword), and
        // UnlockVaultAction compares it as PLAINTEXT - anything here locks the vault behind a PIN
        // the player never chose.
        Assert.Equal(string.Empty, reader.GetString(2));

        Assert.Equal("a@b.c", reader.GetString(3));      // "EMail" is NOT NULL
        Assert.Equal("en", reader.GetString(4));         // "LanguageIsoCode" is NOT NULL, varchar(3)
        Assert.Equal(0, reader.GetInt32(5));             // AccountState.Normal
        Assert.Equal(0, reader.GetInt16(6));             // TimeZone
        Assert.False(reader.GetBoolean(7));              // IsBot - a bot is excluded from every board
        Assert.False(reader.GetBoolean(8));              // IsTemplate
        Assert.False(reader.GetBoolean(9));              // IsVaultExtended

        // The vault is left null on purpose: TalkNpcAction.cs:100 creates it on first use, so the
        // write role needs no access to data."ItemStorage".
        Assert.True(await reader.IsDBNullAsync(10));

        Assert.Equal(DateTimeKind.Utc, reader.GetDateTime(11).Kind);
    }

    [SkippableFact]
    public async Task AnEmptyEmailIsStoredAsEmptyRatherThanNull()
    {
        Skip.IfNot(Enabled);

        // "EMail" is NOT NULL in the schema; a null would be a constraint violation at insert time.
        Assert.Equal(RegisterResult.Created, await this._accounts.RegisterAsync("noemail", "hunter2hunter2", null, default));

        await using var command = this._dataSource.CreateCommand(
            """SELECT "EMail" FROM data."Account" WHERE "LoginName" = 'noemail'""");
        Assert.Equal(string.Empty, (string?)await command.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task TheRegisteredPasswordVerifiesOnTheSubsequentLogin()
    {
        Skip.IfNot(Enabled);

        await this._accounts.RegisterAsync("roundtrip", "correct horse", null, default);

        var ok = await this._accounts.LoginAsync("roundtrip", "correct horse", default);
        Assert.True(ok.Success);
        Assert.Equal("roundtrip", ok.LoginName);

        var bad = await this._accounts.LoginAsync("roundtrip", "wrong horse", default);
        Assert.False(bad.Success);
    }

    [SkippableFact]
    public async Task LoginIsCaseSensitiveJustAsTheGameIs()
    {
        Skip.IfNot(Enabled);

        await this._accounts.RegisterAsync("MixedCase", "hunter2hunter2", null, default);

        Assert.True((await this._accounts.LoginAsync("MixedCase", "hunter2hunter2", default)).Success);

        // AccountRepository compares LoginName with ==, so the game would reject these too. Accepting
        // them here would sign a player into an account they cannot use in the game.
        Assert.False((await this._accounts.LoginAsync("mixedcase", "hunter2hunter2", default)).Success);
        Assert.False((await this._accounts.LoginAsync("MIXEDCASE", "hunter2hunter2", default)).Success);
    }

    [SkippableFact]
    public async Task ANameThatDiffersOnlyByCapitalisationIsRefused()
    {
        Skip.IfNot(Enabled);

        Assert.Equal(RegisterResult.Created, await this._accounts.RegisterAsync("takenname", "hunter2hunter2", null, default));

        // The unique index is case-SENSITIVE, so the database would happily accept 'TakenName' as a
        // second account - a ready-made impersonation.
        Assert.Equal(RegisterResult.NameTaken, await this._accounts.RegisterAsync("TakenName", "hunter2hunter2", null, default));
        Assert.Equal(RegisterResult.NameTaken, await this._accounts.RegisterAsync("TAKENNAME", "hunter2hunter2", null, default));
    }

    [SkippableTheory]
    [InlineData("ab")]                  // shorter than 3
    [InlineData("elevenchars")]         // longer than the varchar(10) column
    [InlineData("has space")]
    [InlineData("dash-name")]
    [InlineData("üñí")]                 // the client's field is not Unicode-safe
    [InlineData("test7")]               // a seeded development account
    [InlineData("testgm")]              // a seeded account that ships as GameMaster
    public async Task UnusableNamesAreRefused(string loginName)
    {
        Skip.IfNot(Enabled);

        Assert.Equal(RegisterResult.InvalidName, await this._accounts.RegisterAsync(loginName, "hunter2hunter2", null, default));
    }

    [SkippableTheory]
    [InlineData("short")]                        // below MinPassword
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // above MaxPassword: the client truncates it, so the
                                                 // stored hash could never verify again
    public async Task PasswordsOutsideTheClientsRangeAreRefused(string password)
    {
        Skip.IfNot(Enabled);

        Assert.Equal(RegisterResult.InvalidPassword, await this._accounts.RegisterAsync("lengths", password, null, default));
    }

    [SkippableFact]
    public async Task ChangingAPasswordRequiresTheCurrentOneAndReHashes()
    {
        Skip.IfNot(Enabled);

        await this._accounts.RegisterAsync("changer", "firstpassword", null, default);
        var login = await this._accounts.LoginAsync("changer", "firstpassword", default);

        Assert.False(await this._accounts.ChangePasswordAsync(login.AccountId, "notmypassword", "secondpassword", default));
        Assert.True(await this._accounts.ChangePasswordAsync(login.AccountId, "firstpassword", "secondpassword", default));

        Assert.False((await this._accounts.LoginAsync("changer", "firstpassword", default)).Success);
        Assert.True((await this._accounts.LoginAsync("changer", "secondpassword", default)).Success);
    }

    [SkippableFact]
    public async Task ANewPasswordLongerThanTheClientCanSendIsRefused()
    {
        Skip.IfNot(Enabled);

        await this._accounts.RegisterAsync("toolong", "firstpassword", null, default);
        var login = await this._accounts.LoginAsync("toolong", "firstpassword", default);

        Assert.False(await this._accounts.ChangePasswordAsync(login.AccountId, "firstpassword", new string('x', 40), default));
        Assert.True((await this._accounts.LoginAsync("toolong", "firstpassword", default)).Success);
    }

    private sealed class StaticOptions(SiteOptions value) : IOptionsMonitor<SiteOptions>
    {
        public SiteOptions CurrentValue => value;

        public SiteOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SiteOptions, string?> listener) => null;
    }
}
