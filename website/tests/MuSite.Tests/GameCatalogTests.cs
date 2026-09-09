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

    [SkippableFact]
    public async Task MapsCarryTheirNumberAndSpawnCount()
    {
        Skip.IfNot(Enabled);

        var maps = await this._catalog.MapsAsync(default);

        var tarkan = maps.Single(m => m.Number == 8);
        Assert.Equal("Tarkan", tarkan.Name);
        Assert.Equal(2, tarkan.SpawnAreas);

        var icarus = maps.Single(m => m.Number == 10);
        Assert.Equal(1.5, icarus.ExpMultiplier);
        Assert.Equal(1, icarus.SpawnAreas);
    }

    [SkippableFact]
    public async Task WhatSpawnsOnAMapIsCountedPerMapNotOverall()
    {
        Skip.IfNot(Enabled);

        // Golden Tantallos has two spawn areas in total but only ONE of them is on Tarkan. The map
        // view must count the areas on THIS map, or every boss would look twice as common as it is.
        var onTarkan = await this._catalog.MonstersOnMapAsync(8, default);

        Assert.Equal(2, onTarkan.Count);

        var golden = onTarkan.Single(m => m.Number == 78);
        Assert.Equal(1, golden.SpawnAreas);
        Assert.Equal(106, golden.Level);

        // Ordered by level, highest first: a GM scanning a map wants the dangerous ones on top.
        Assert.Equal(onTarkan.OrderByDescending(m => m.Level).Select(m => m.Number), onTarkan.Select(m => m.Number));
    }

    [SkippableFact]
    public async Task AMapWithNothingOnItReturnsNothing()
    {
        Skip.IfNot(Enabled);

        Assert.Empty(await this._catalog.MonstersOnMapAsync(999, default));
    }
}
