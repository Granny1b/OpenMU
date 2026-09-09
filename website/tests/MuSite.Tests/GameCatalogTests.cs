using Microsoft.Extensions.Configuration;
using MuSite.Data;
using Npgsql;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The GM console's catalogue queries, against a real PostgreSQL.
///
/// These are strings over the `config` schema, so the compiler can check none of it. The fixture
/// carries the hazards each query has to survive - a duplicate attribute row, a monster with no
/// attributes at all, a point spawn and a box spawn - and the column TYPES are the real ones: every
/// C# `byte` in OpenMU is PostgreSQL `smallint`, so a record field declared `byte` would be
/// rejected by Dapper against the real database. A fixture using `integer` would hide that.
///
/// Skipped when MUSITE_TEST_DB is unset.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class GameCatalogTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MUSITE_TEST_DB");

    private GameCatalog _catalog = null!;
    private SiteDataSources _sources = null!;

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

    [SkippableFact]
    public async Task EveryMonsterQueryMaterialises()
    {
        Skip.IfNot(Enabled);

        var monsters = await this._catalog.MonstersAsync(null, 100, default);

        Assert.NotEmpty(monsters);
        Assert.All(monsters, m => Assert.False(string.IsNullOrWhiteSpace(m.Designation)));
    }

    [SkippableFact]
    public async Task ADuplicateAttributeRowDoesNotSplitAMonsterInTwo()
    {
        Skip.IfNot(Enabled);

        // The fixture gives Golden Tantallos two Level rows, 104 and 106. A GROUP BY that lost the
        // aggregate would list it twice; MAX must win and the row count must stay one.
        var monsters = await this._catalog.MonstersAsync("Tantallos", 100, default);

        var monster = Assert.Single(monsters);
        Assert.Equal(106, monster.Level);
        Assert.Equal(35000, monster.Health);
    }

    [SkippableFact]
    public async Task AMonsterWithNoAttributesStillListsRatherThanVanishing()
    {
        Skip.IfNot(Enabled);

        // A plain JOIN instead of a LEFT JOIN would drop it entirely, and a GM searching for it
        // would conclude it does not exist.
        var monsters = await this._catalog.MonstersAsync("Statueless", 100, default);

        var monster = Assert.Single(monsters);
        Assert.Equal(0, monster.Level);
        Assert.Equal(0, monster.Health);
        Assert.Equal(0, monster.SpawnAreas);
    }

    [SkippableFact]
    public async Task AMonsterIsFoundByItsNumberAsWellAsItsName()
    {
        Skip.IfNot(Enabled);

        // A GM reading a command usually has the number, not the name.
        var byNumber = await this._catalog.MonstersAsync("78", 100, default);

        var monster = Assert.Single(byNumber);
        Assert.Equal("Golden Tantallos", monster.Designation);
    }

    [SkippableFact]
    public async Task SpawnAreasResolveToMapNamesAndCoordinates()
    {
        Skip.IfNot(Enabled);

        var spawns = await this._catalog.SpawnsAsync(78, default);

        Assert.Equal(2, spawns.Count);

        var box = spawns.Single(s => s.MapNumber == 8);
        Assert.Equal("Tarkan", box.MapName);
        Assert.Equal(120, box.X1);
        Assert.Equal(100, box.Y2);
        Assert.Equal(3, box.Quantity);

        var point = spawns.Single(s => s.MapNumber == 10);
        Assert.Equal(point.X1, point.X2);
        Assert.Equal(point.Y1, point.Y2);
    }

    [SkippableFact]
    public async Task AMonsterThatIsNeverPlacedHasNoSpawns()
    {
        Skip.IfNot(Enabled);

        Assert.Empty(await this._catalog.SpawnsAsync(253, default));
    }

    [SkippableFact]
    public async Task ItemsAreFoundByNameAndFilteredByGroup()
    {
        Skip.IfNot(Enabled);

        var jewels = await this._catalog.ItemsAsync("jewel", null, 100, default);

        Assert.Equal(3, jewels.Count);
        Assert.All(jewels, j => Assert.Equal(14, j.Group));

        var group0 = await this._catalog.ItemsAsync(null, 0, 100, default);

        var weapon = Assert.Single(group0);
        Assert.Equal("Dragon Slayer", weapon.Name);
        Assert.Equal(15, weapon.MaximumItemLevel);
        Assert.Equal(5, weapon.MaximumSockets);
        Assert.Equal(130, weapon.MaximumDropLevel);
    }

    [SkippableFact]
    public async Task ANullableDropLevelComesBackAsNull()
    {
        Skip.IfNot(Enabled);

        // MaximumDropLevel is nullable in the real schema; a jewel has none.
        var jewel = await this._catalog.ItemAsync(14, 13, default);

        Assert.NotNull(jewel);
        Assert.Equal("Jewel of Bless", jewel!.Name);
        Assert.Null(jewel.MaximumDropLevel);
        Assert.False(jewel.IsQuestItem);
    }

    [SkippableFact]
    public async Task AQuestItemIsReportedAsOne()
    {
        Skip.IfNot(Enabled);

        var quest = await this._catalog.ItemAsync(13, 20, default);

        Assert.NotNull(quest);
        Assert.True(quest!.IsQuestItem);
        Assert.False(quest.DropsFromMonsters);
    }

    [SkippableFact]
    public async Task AnUnknownItemIsNullRatherThanThrowing()
    {
        Skip.IfNot(Enabled);

        Assert.Null(await this._catalog.ItemAsync(99, 999, default));
    }

    // ---------------------------------------------------------------------------------------------
    // Item options. These are what let the /item builder say "+7 Attack Speed Any" instead of
    // asking a GM what bit 4 of ex means, so getting them wrong is worse than not having them.
    // ---------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task AnItemsExcellentOptionsCarryTheBitThatSelectsThem()
    {
        Skip.IfNot(Enabled);

        var options = await this._catalog.ItemExcellentOptionsAsync(0, 16, default);

        // 1 << (Number - 1), which is what AddExcellentOptions masks `ex` against.
        Assert.Equal([3, 4, 5, 6], options.Select(o => o.Number));
        Assert.Equal([4, 8, 16, 32], options.Select(o => o.BitValue));

        var attackSpeed = options.Single(o => o.Number == 3);
        Assert.Equal("Attack Speed Any", attackSpeed.Attribute);
        Assert.Equal(7m, attackSpeed.Value);
        Assert.Equal(0, attackSpeed.AggregateType);
        Assert.Null(attackSpeed.ScalesWith);
    }

    [SkippableFact]
    public async Task AnExcellentOptionWithNoConstantReportsWhatItScalesWith()
    {
        Skip.IfNot(Enabled);

        // Option 5 of every excellent set is a relationship, not a constant: its stored value is 0
        // and a page printing only that would say "+0 Physical Base Dmg", which is a lie.
        var options = await this._catalog.ItemExcellentOptionsAsync(0, 16, default);

        var scaling = options.Single(o => o.Number == 5);
        Assert.Equal(0m, scaling.Value);
        Assert.Equal("Total Level", scaling.ScalesWith);
        Assert.Equal(0.05m, scaling.ScalesWithFactor);
    }

    [SkippableFact]
    public async Task OptionsThatAreNeitherExcellentNorOrdinaryStayOutOfBothLists()
    {
        Skip.IfNot(Enabled);

        // The Dragon Slayer also carries a Luck option. It is applied by `lu`, not by `ex` or
        // `opt`, so it must not appear in either list or the bits would be off by one.
        var excellent = await this._catalog.ItemExcellentOptionsAsync(0, 16, default);
        var levels = await this._catalog.ItemOptionLevelsAsync(0, 16, default);

        Assert.DoesNotContain(excellent, o => o.Attribute == "Critical Damage Chance");
        Assert.DoesNotContain(levels, o => o.Attribute == "Critical Damage Chance");
    }

    [SkippableFact]
    public async Task AnOrdinaryOptionComesBackAsItsLevels()
    {
        Skip.IfNot(Enabled);

        // Level 1 is the option's own boost; 2 upwards are ItemOptionOfLevel rows. Both branches of
        // the UNION have to be there, or the select would offer +4 and nothing else.
        var levels = await this._catalog.ItemOptionLevelsAsync(0, 16, default);

        Assert.Equal([1, 2, 3, 4], levels.Select(l => l.Level));
        Assert.All(levels, l => Assert.Equal("Physical Base Dmg", l.Attribute));
        Assert.Equal([4m, 8m, 12m, 16m], levels.Select(l => l.Value));
    }

    [SkippableFact]
    public async Task TheDinorantsThreeOptionsAreAllLevelOne()
    {
        Skip.IfNot(Enabled);

        // Its `opt` is a bit field over these three, not a level - so all three are level 1 and the
        // page has to tell them apart by attribute, exactly as AddOption does.
        var levels = await this._catalog.ItemOptionLevelsAsync(13, 3, default);

        // The ids are spelled out rather than read from the site's own constants: comparing the
        // code to itself would prove nothing. These are Stats.DamageReceiveDecrement,
        // Stats.MaximumAbility and Stats.AttackSpeedAny, which is what
        // ItemChatCommandPlugIn.AddOption matches bits 1, 2 and 4 against.
        Assert.Equal(3, levels.Count);
        Assert.All(levels, l => Assert.Equal(1, l.Level));
        Assert.Contains(levels, l => l.AttributeId == new Guid("9D9761EF-EF47-4E5C-8106-EBC555786F20"));
        Assert.Contains(levels, l => l.AttributeId == new Guid("466BBBBA-C1D8-45DC-8832-2EAA1130ACFD"));
        Assert.Contains(levels, l => l.AttributeId == new Guid("DA08473F-DF5B-444D-8651-9EDB65797922"));
    }

    [SkippableFact]
    public async Task AnItemsAncientSetsAreNamedWithTheirBonusAtBothLevels()
    {
        Skip.IfNot(Enabled);

        var sets = await this._catalog.ItemAncientSetsAsync(0, 16, default);

        Assert.Equal([1, 2], sets.Select(s => s.Discriminator));
        Assert.Equal(["Hyon Dragon", "Vicious Dragon"], sets.Select(s => s.SetName));

        var hyon = sets[0];
        Assert.Equal("Total Strength", hyon.BonusAttribute);
        Assert.Equal(5m, hyon.BonusAtLevel1);
        Assert.Equal(10m, hyon.BonusAtLevel2);
        Assert.Equal(2, hyon.MinimumItemCount);
        Assert.Equal(2, hyon.ItemsInSet);
    }

    [SkippableFact]
    public async Task AnAncientSetTheItemIsNotLinkedToIsNotOffered()
    {
        Skip.IfNot(Enabled);

        // The fixture gives the Dragon Slayer an ItemOfItemSet row for discriminator 3 whose group
        // is NOT among its PossibleItemSetGroups. AddAncientBonusOption would ignore anc=3, so
        // offering it would build a command that silently does nothing.
        var sets = await this._catalog.ItemAncientSetsAsync(0, 16, default);

        Assert.DoesNotContain(sets, s => s.Discriminator == 3);
        Assert.DoesNotContain(sets, s => s.SetName == "Sylph Wind Set");
    }

    [SkippableFact]
    public async Task AnItemWithNoOptionsReturnsEmptyRatherThanThrowing()
    {
        Skip.IfNot(Enabled);

        Assert.Empty(await this._catalog.ItemExcellentOptionsAsync(14, 13, default));
        Assert.Empty(await this._catalog.ItemOptionLevelsAsync(14, 13, default));
        Assert.Empty(await this._catalog.ItemAncientSetsAsync(14, 13, default));
    }

    [SkippableFact]
    public async Task AnItemsSkillNumberIsWhatMarksTheDinorant()
    {
        Skip.IfNot(Enabled);

        var dinorant = await this._catalog.ItemAsync(13, 3, default);
        var jewel = await this._catalog.ItemAsync(14, 13, default);

        // 49 is the skill number ItemChatCommandPlugIn.cs:90 tests for.
        Assert.Equal(49, dinorant!.SkillNumber);
        Assert.Null(jewel!.SkillNumber);
    }
}
