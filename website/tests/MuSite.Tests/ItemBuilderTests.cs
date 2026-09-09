using Microsoft.Extensions.Configuration;
using MuSite.Data;
using MuSite.Pages.Admin;
using Npgsql;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The /item builder page itself, over a real catalogue.
///
/// The queries are covered by GameCatalogTests and the wording by OptionTextTests; what is left is
/// the arithmetic the page does on the way in and out - turning ticked boxes back into the single
/// number `ex` wants, and deciding whether `opt` is a level or a bit field. That decision is the
/// one that cannot be got wrong quietly: /item accepts either and simply builds a different item.
///
/// Skipped when MUSITE_TEST_DB is unset.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ItemBuilderTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MUSITE_TEST_DB");

    private SiteDataSources _sources = null!;
    private GameCatalog _catalog = null!;

    private static bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

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

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:GameRead"] = ConnectionString,
            ["ConnectionStrings:GameAuth"] = ConnectionString,
            ["ConnectionStrings:GameReg"] = ConnectionString,
            ["ConnectionStrings:Site"] = ConnectionString,
        }).Build();

        this._sources = SiteDataSources.Create(configuration);
        this._catalog = new GameCatalog(this._sources);
    }

    public async Task DisposeAsync()
    {
        if (Enabled)
        {
            await this._sources.DisposeAsync();
        }
    }

    /// <summary>
    /// The Dragon Slayer, group 0 number 16, with whatever boxes were ticked.
    ///
    /// `filterGroup` is deliberately separate from the item being built and defaults to null: the
    /// two used to be the same parameter, which is why building a sword narrowed the list to swords.
    /// </summary>
    private async Task<ItemsModel> BuildAsync(
        int group = 0, int number = 16, int lvl = 0, int[]? exbit = null,
        bool sk = false, bool lu = false, int opt = 0, int[]? optbit = null,
        int anc = 0, int ancLvl = 0, int? filterGroup = null)
    {
        var page = new ItemsModel(this._catalog);
        await page.OnGetAsync(
            null, filterGroup, ItemsModel.ItemKey(group, number), 1,
            lvl, exbit, sk, lu, opt, optbit, anc, ancLvl, default);
        return page;
    }

    // ---------------------------------------------------------------------------------------------
    // The group picker. `group` used to be BOTH the filter and half of the selected item's
    // identity, so picking a sword narrowed the whole list to swords and the picker could never
    // sit on "All groups". The two are separate parameters now.
    // ---------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task BuildingAnItemDoesNotFilterTheListToItsGroup()
    {
        Skip.IfNot(Enabled);

        var page = await this.BuildAsync(group: 0, number: 16);

        Assert.Equal("Dragon Slayer", page.Selected!.Name);
        Assert.Null(page.Group);
        Assert.Equal(6, page.Total);
        Assert.Contains(page.Results, i => i.Group == 14);
    }

    [SkippableFact]
    public async Task TheGroupFilterStillFiltersWhenOneIsChosen()
    {
        Skip.IfNot(Enabled);

        var page = await this.BuildAsync(group: 0, number: 16, filterGroup: 14);

        Assert.Equal(14, page.Group);
        Assert.Equal(3, page.Total);
        Assert.All(page.Results, i => Assert.Equal(14, i.Group));

        // ... and the item being built is unaffected by it.
        Assert.Equal("Dragon Slayer", page.Selected!.Name);
        Assert.Equal("/item group=0 number=16", page.Command);
    }

    [SkippableFact]
    public async Task TheGroupPickerOnlyOffersGroupsThatHoldItems()
    {
        Skip.IfNot(Enabled);

        var page = await this.BuildAsync();

        // The fixture has groups 0, 13 and 14 only. A picker hardcoded to 0-15 would offer twelve
        // groups that come back empty.
        Assert.Equal([0, 13, 14], page.Groups.Select(g => g.Group));
        Assert.Equal([1, 2, 3], page.Groups.Select(g => g.Items));
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("0")]
    [InlineData("0:")]
    [InlineData("a:b")]
    [InlineData("0:16:32")]
    public async Task AMalformedItemKeySelectsNothingRatherThanThrowing(string key)
    {
        Skip.IfNot(Enabled);

        // The value is in the query string, so a stray edit must not 500 the page.
        var page = new ItemsModel(this._catalog);
        await page.OnGetAsync(null, null, key, 1, 0, null, false, false, 0, null, 0, 0, default);

        Assert.Null(page.Selected);
        Assert.Null(page.Command);
        Assert.NotEmpty(page.Results);
    }

    [SkippableFact]
    public async Task ABuildLinkCarriesTheSearchAndTheFilterButNotTheRowsGroup()
    {
        Skip.IfNot(Enabled);

        var page = new ItemsModel(this._catalog);
        await page.OnGetAsync("jewel", 14, null, 1, 0, null, false, false, 0, null, 0, 0, default);

        var row = page.Results.First(i => i.Group == 14);
        var link = page.BuildLink(row);

        Assert.Contains($"item=14:{row.Number}", link, StringComparison.Ordinal);
        Assert.Contains("q=jewel", link, StringComparison.Ordinal);
        Assert.Contains("group=14", link, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PagingReportsTheWholeCatalogueNotJustThePageOnScreen()
    {
        Skip.IfNot(Enabled);

        var page = new ItemsModel(this._catalog);
        await page.OnGetAsync(null, null, null, 1, 0, null, false, false, 0, null, 0, 0, default);

        Assert.Equal(6, page.Total);
        Assert.Equal(6, page.Results.Count);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(1, page.PageCount);
        Assert.Equal(1, page.FirstRow);
        Assert.Equal(6, page.LastRow);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(9999)]
    public async Task APageNumberOutsideTheRangeIsClampedRatherThanShowingNothing(int requested)
    {
        Skip.IfNot(Enabled);

        // ?page=0 and ?page=9999 are one edit of the address bar away, and an out-of-range OFFSET
        // returns an empty table that reads as "no items match".
        var page = new ItemsModel(this._catalog);
        await page.OnGetAsync(null, null, null, requested, 0, null, false, false, 0, null, 0, 0, default);

        Assert.InRange(page.PageNumber, 1, page.PageCount);
        Assert.NotEmpty(page.Results);
    }

    [SkippableFact]
    public async Task PagingKeepsTheSearchAndTheItemBeingBuilt()
    {
        Skip.IfNot(Enabled);

        // Paging the list must not throw away a half-built command, or the builder closes under you.
        var page = new ItemsModel(this._catalog);
        await page.OnGetAsync("dragon", 0, ItemsModel.ItemKey(0, 16), 1, 0, [4], false, false, 0, null, 0, 0, default);

        var link = page.PageLink(2);

        Assert.Contains("page=2", link, StringComparison.Ordinal);
        Assert.Contains("q=dragon", link, StringComparison.Ordinal);
        Assert.Contains("group=0", link, StringComparison.Ordinal);
        Assert.Contains("item=0:16", link, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TickedExcellentBoxesAreAddedUpIntoOneNumber()
    {
        Skip.IfNot(Enabled);

        // Options 3, 4 and 6 are bits 4, 8 and 32. A GM should never have to work out that this is
        // 44 - and 44 is exactly what /item has to receive.
        var page = await this.BuildAsync(exbit: [4, 8, 32]);

        Assert.Empty(page.Problems);
        Assert.Equal(44, page.Requested!.ExcellentNumber);
        Assert.Equal("/item group=0 number=16 ex=44", page.Command);
    }

    [SkippableFact]
    public async Task AnExcellentBoxTickedTwiceStillCountsOnce()
    {
        Skip.IfNot(Enabled);

        // A repeated query parameter must not double the bit into a different option's value.
        var page = await this.BuildAsync(exbit: [8, 8, 4]);

        Assert.Equal(12, page.Requested!.ExcellentNumber);
    }

    [SkippableFact]
    public async Task TheOptionIsALevelForAnOrdinaryItem()
    {
        Skip.IfNot(Enabled);

        var page = await this.BuildAsync(opt: 3);

        Assert.False(page.OptIsBitField);
        Assert.Equal([1, 2, 3, 4], page.OptionLevels.Select(o => o.Level));
        Assert.Equal("/item group=0 number=16 opt=3", page.Command);
    }

    [SkippableFact]
    public async Task AnOptionLevelTheItemDoesNotHaveIsRefused()
    {
        Skip.IfNot(Enabled);

        // Only reachable by editing the query string - the select offers 1 to 4 - but /item would
        // accept it and produce an option with no configured value.
        var page = await this.BuildAsync(opt: 9);

        Assert.Null(page.Command);
        Assert.Contains(page.Problems, p => p.Contains("level 4", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task TheDinorantsOptionIsABitFieldInstead()
    {
        Skip.IfNot(Enabled);

        // Skill number 49. ItemChatCommandPlugIn.AddOption switches on it, so the page must too.
        var page = await this.BuildAsync(group: 13, number: 3, optbit: [1, 4]);

        Assert.True(page.OptIsBitField);
        Assert.Equal(3, page.DinorantOptions.Count);
        Assert.Equal(5, page.Requested!.Opt);
        Assert.Equal("/item group=13 number=3 opt=5", page.Command);
    }

    [SkippableFact]
    public async Task ADinorantBitTheItemHasNoOptionForIsRefused()
    {
        Skip.IfNot(Enabled);

        var page = await this.BuildAsync(group: 13, number: 3, optbit: [8]);

        Assert.Null(page.Command);
        Assert.Contains(page.Problems, p => p.Contains("add up to 7", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task AnAncientSetIsOfferedByNameAndBuiltByNumber()
    {
        Skip.IfNot(Enabled);

        var page = await this.BuildAsync(anc: 2, ancLvl: 2);

        Assert.Equal(["Hyon Dragon", "Vicious Dragon"], page.AncientSets.Select(s => s.SetName));
        Assert.Equal("/item group=0 number=16 anc=2 ancBonuslvl=2", page.Command);
    }

    [SkippableFact]
    public async Task AnAncientOutsideTheCommandsOwnValidValuesIsRefused()
    {
        Skip.IfNot(Enabled);

        // The fixture's unlinked set is discriminator 3, which ItemChatCommandArgs rejects anyway
        // through [ValidValues("0","1","2")] - so this is the check that fires, and it is enough.
        // The page never offers it either: ItemAncientSetsAsync excludes unlinked sets.
        var page = await this.BuildAsync(anc: 3);

        Assert.Null(page.Command);
        Assert.DoesNotContain(page.AncientSets, s => s.Discriminator == 3);
        Assert.Contains(page.Problems, p => p.Contains("Ancient must be", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task AnAncientOnAnItemThatIsInNoSetIsRefused()
    {
        Skip.IfNot(Enabled);

        // The Dinorant belongs to no ancient set, so /item would apply nothing and say nothing.
        var page = await this.BuildAsync(group: 13, number: 3, anc: 1);

        Assert.Null(page.Command);
        Assert.Contains(page.Problems, p => p.Contains("not part of any ancient set", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task AnItemWithNoOptionsBuildsThePlainCommand()
    {
        Skip.IfNot(Enabled);

        var page = await this.BuildAsync(group: 14, number: 13);

        Assert.Empty(page.ExcellentOptions);
        Assert.Empty(page.OptionLevels);
        Assert.Empty(page.AncientSets);
        Assert.False(page.OptIsBitField);
        Assert.Equal("/item group=14 number=13", page.Command);
    }

    [SkippableFact]
    public async Task AnUnfilledFormDoesNotReadAsAnAncientBonusOfZero()
    {
        Skip.IfNot(Enabled);

        // AncientBonusLevel defaults to 1 server-side. A form that posts nothing must match that,
        // or every plain item trips "only applies when Ancient is 1 or 2".
        var page = await this.BuildAsync();

        Assert.Equal(1, page.Requested!.AncientBonusLevel);
        Assert.Empty(page.Problems);
        Assert.Equal("/item group=0 number=16", page.Command);
    }
}
