using Microsoft.Extensions.Configuration;
using MuSite.Data;
using Npgsql;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Runs the real SQL from <see cref="PublicQueries"/> against a real PostgreSQL.
///
/// These queries are strings: the compiler cannot check a column name, a CTE, or a construct
/// PostgreSQL rejects. Everything else in this suite would pass with a query that throws on first
/// contact with a database, which is why this exists.
///
/// The fixture (StandInSchema.sql) is seeded with the hazards the queries have to survive - a
/// duplicate Level row, a character with no master level, one with no stats at all, one with a null
/// AccountId, and banned/temp-banned/bot/template accounts each holding a HIGHER-level character
/// than any visible one, so a missing exclusion shows up as a wrong first row rather than nothing.
///
/// Skipped when MUSITE_TEST_DB is unset, so `dotnet test` works without a database.
/// </summary>
public sealed class PublicQueriesTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MUSITE_TEST_DB");

    private PublicQueries _queries = null!;

    public static bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var command = dataSource.CreateCommand(await File.ReadAllTextAsync("StandInSchema.sql")))
        {
            await command.ExecuteNonQueryAsync();
        }

        // All four pools point at the same database here: this suite tests the SQL, not the grants.
        // The grants are verified separately, by db/01b-grants.sql's own checks.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:GameRead"] = ConnectionString,
            ["ConnectionStrings:GameAuth"] = ConnectionString,
            ["ConnectionStrings:GameReg"] = ConnectionString,
            ["ConnectionStrings:Site"] = ConnectionString,
        }).Build();

        this._queries = new PublicQueries(SiteDataSources.Create(configuration));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task LevelBoardExcludesBannedBotAndTemplateAccounts()
    {
        Skip.IfNot(Enabled);

        var rows = await this._queries.GetRankingAsync(RankingBoard.Level, 0, 50, null, default);

        // The excluded accounts hold levels 401-404, above every visible character.
        Assert.Equal(["Valdrenn", "Sirenya"], rows.Select(r => r.Name).ToArray());
    }

    [SkippableFact]
    public async Task DuplicateLevelRowsProduceOneRowAtTheHighestValue()
    {
        Skip.IfNot(Enabled);

        var rows = await this._queries.GetRankingAsync(RankingBoard.Level, 0, 50, null, default);
        var valdrenn = rows.Where(r => r.Name == "Valdrenn").ToArray();

        Assert.Single(valdrenn);
        Assert.Equal(400, valdrenn[0].Level);   // not the second row's 399
    }

    [SkippableFact]
    public async Task ACharacterWithNoMasterLevelRowReadsZeroRatherThanDisappearing()
    {
        Skip.IfNot(Enabled);

        var rows = await this._queries.GetRankingAsync(RankingBoard.Level, 0, 50, null, default);

        Assert.Equal(0, rows.Single(r => r.Name == "Sirenya").MasterLevel);
    }

    [SkippableFact]
    public async Task MasterBoardOmitsCharactersWithoutAMasterLevel()
    {
        Skip.IfNot(Enabled);

        var rows = await this._queries.GetRankingAsync(RankingBoard.Master, 0, 50, null, default);

        Assert.Equal(["Valdrenn"], rows.Select(r => r.Name).ToArray());
    }

    [SkippableFact]
    public async Task EveryBoardRunsAndItsCountMatchesItsRows()
    {
        Skip.IfNot(Enabled);

        foreach (var board in Enum.GetValues<RankingBoard>())
        {
            var rows = await this._queries.GetRankingAsync(board, 0, 500, null, default);
            var count = await this._queries.CountRankingAsync(board, null, default);

            Assert.Equal(rows.Count, count);
        }
    }

    [SkippableFact]
    public async Task SearchIsAPrefixMatchAndIsCaseInsensitive()
    {
        Skip.IfNot(Enabled);

        var rows = await this._queries.GetRankingAsync(RankingBoard.Level, 0, 50, "VALD", default);

        Assert.Equal(["Valdrenn"], rows.Select(r => r.Name).ToArray());
    }

    [SkippableFact]
    public async Task ProfileResolvesCaseInsensitivelyAndCarriesTheGuild()
    {
        Skip.IfNot(Enabled);

        var profile = await this._queries.GetCharacterAsync("valdrenn", default);

        Assert.NotNull(profile);
        Assert.Equal("Valdrenn", profile!.Name);
        Assert.Equal("Blade Master", profile.Class);
        Assert.Equal(400, profile.Level);
        Assert.Equal(37, profile.Resets);
        Assert.Equal(18400, profile.Strength);
        Assert.Equal("Tarkan", profile.Map);
        Assert.Equal("Ironveil", profile.Guild);
        Assert.Equal(2, profile.GuildPosition);      // GuildMaster
    }

    [SkippableTheory]
    [InlineData("BannedChar")]
    [InlineData("TempBanChar")]
    [InlineData("BotChar")]
    [InlineData("TemplateChar")]
    [InlineData("Orphaned")]
    [InlineData("NoSuchCharacter")]
    public async Task HiddenCharactersAreIndistinguishableFromMissingOnes(string name)
    {
        Skip.IfNot(Enabled);

        Assert.Null(await this._queries.GetCharacterAsync(name, default));
    }

    [SkippableFact]
    public async Task ACharacterWithNoStatRowsStillRendersAProfile()
    {
        Skip.IfNot(Enabled);

        var profile = await this._queries.GetCharacterAsync("NoStats", default);

        Assert.NotNull(profile);
        Assert.Equal(0, profile!.Level);
        Assert.Null(profile.Guild);
    }

    [SkippableFact]
    public async Task GuildListCountsOnlyMembersTheRosterWillShow()
    {
        Skip.IfNot(Enabled);

        var guilds = await this._queries.GetGuildsAsync(0, 50, null, default);

        Assert.Equal(0, guilds.Single(g => g.Name == "EmptyGuild").Members);
        Assert.Equal(2, guilds.Single(g => g.Name == "Ironveil").Members);
        Assert.Equal("Ironveil", guilds[0].Name);   // ordered by score

        // Nightfall's only member sits on a banned account. The list must agree with the roster page,
        // which excludes that member - otherwise the list advertises members the profile cannot show.
        Assert.Equal(0, guilds.Single(g => g.Name == "Nightfall").Members);
    }

    [SkippableFact]
    public async Task GuildRosterIsOrderedByRankAndExcludesHiddenAccounts()
    {
        Skip.IfNot(Enabled);

        var ironveil = await this._queries.GetGuildAsync("IRONVEIL", default);
        Assert.NotNull(ironveil);
        Assert.Equal(["Valdrenn", "Sirenya"], ironveil!.Members.Select(m => m.Name).ToArray());

        // Nightfall's only member sits on a banned account.
        var nightfall = await this._queries.GetGuildAsync("Nightfall", default);
        Assert.NotNull(nightfall);
        Assert.Empty(nightfall!.Members);
    }

    [SkippableFact]
    public async Task TotalsExcludeTheSameAccountsTheBoardsDo()
    {
        Skip.IfNot(Enabled);

        var totals = await this._queries.GetTotalsAsync(default);

        Assert.Equal(2, totals.Accounts);      // valdrenn and sirenya
        Assert.Equal(3, totals.Characters);    // Valdrenn, Sirenya, NoStats
    }
}
