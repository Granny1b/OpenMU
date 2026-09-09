using Dapper;
using Microsoft.Extensions.Configuration;
using MuSite.Data;
using MuSite.Services;
using Npgsql;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The /admin/logs queries, against a real PostgreSQL.
///
/// The rows here stand in for what Vector inserts. What matters is not that a SELECT runs, but that
/// the count and the listing agree on the same filter - a pager whose count comes from a different
/// WHERE clause than its rows shows pages that come back empty, which is exactly how the item
/// listing broke - and that the time window, the level and the search actually narrow.
///
/// Skipped when MUSITE_TEST_SITE_DB is unset.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ServerLogTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MUSITE_TEST_SITE_DB");

    private SiteDataSources _sources = null!;
    private NpgsqlDataSource _dataSource = null!;
    private ServerLog _log = null!;

    private static bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        this._dataSource = NpgsqlDataSource.Create(ConnectionString!);

        await using (var command = this._dataSource.CreateCommand("DELETE FROM server_log"))
        {
            await command.ExecuteNonQueryAsync();
        }

        // Deliberately spread across time and levels, with one multi-line exception and one line
        // whose text only appears inside that exception.
        await using (var command = this._dataSource.CreateCommand(
            """
            INSERT INTO server_log (id, at, level, source, event_id, message, exception) VALUES
              (gen_random_uuid(), now() - interval '2 minutes',  'Information', 'MUnique.OpenMU.GameServer.GameServer', NULL, 'Server listener started.', NULL),
              (gen_random_uuid(), now() - interval '3 minutes',  'Warning',     'MUnique.OpenMU.GameLogic.Player',      NULL, 'Trader invoked CancelTrade', NULL),
              (gen_random_uuid(), now() - interval '4 minutes',  'Error',       'MUnique.OpenMU.Persistence.Repo',      '{ Id = 7 }', 'Could not load account',
               E'Npgsql.PostgresException: 42501: permission denied for table Account\n   at Npgsql.ReadMessage()\n   at Repo.GetByIdAsync(Guid id)'),
              (gen_random_uuid(), now() - interval '5 hours',    'Information', 'MUnique.OpenMU.GameLogic.Player',      NULL, 'Guild created', NULL),
              (gen_random_uuid(), now() - interval '3 days',     'Error',       'MUnique.OpenMU.GameServer.GameServer', NULL, 'Old failure well outside the hour', NULL),
              (gen_random_uuid(), now() - interval '40 days',    'Fatal',       'MUnique.OpenMU.GameServer.GameServer', NULL, 'Ancient fatal beyond retention', NULL)
            """))
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
        this._log = new ServerLog(this._sources);
    }

    public async Task DisposeAsync()
    {
        if (Enabled)
        {
            await this._sources.DisposeAsync();
            await this._dataSource.DisposeAsync();
        }
    }

    private static LogFilter All => new(0, null, null, null);

    [SkippableFact]
    public async Task ALineMaterialisesWithEveryColumn()
    {
        Skip.IfNot(Enabled);

        var page = await this._log.PageAsync(new LogFilter(0, "Error", null, null), 0, 10, default);

        var failure = page.First(e => e.Message == "Could not load account");
        Assert.Equal("Error", failure.Level);
        Assert.Equal("MUnique.OpenMU.Persistence.Repo", failure.Source);
        Assert.Equal("{ Id = 7 }", failure.EventId);
        Assert.NotEqual(Guid.Empty, failure.Id);
        Assert.NotEqual(default, failure.At);

        // The stack trace must arrive as ONE value with its newlines, not split across rows.
        Assert.NotNull(failure.Exception);
        Assert.Contains("permission denied", failure.Exception!, StringComparison.Ordinal);
        Assert.Equal(3, failure.Exception!.Split('\n').Length);
    }

    [SkippableFact]
    public async Task ALineWithNoSourceComesBackAsNull()
    {
        Skip.IfNot(Enabled);

        await using var command = this._dataSource.CreateCommand(
            "INSERT INTO server_log (id,at,level,message) VALUES (gen_random_uuid(), now(), 'Information', 'Begin starting')");
        await command.ExecuteNonQueryAsync();

        var page = await this._log.PageAsync(new LogFilter(1, null, null, "Begin starting"), 0, 10, default);

        var entry = Assert.Single(page);
        Assert.Null(entry.Source);
        Assert.Null(entry.EventId);
        Assert.Null(entry.Exception);
    }

    [SkippableTheory]
    [InlineData(1, null, null, null)]
    [InlineData(24, null, null, null)]
    [InlineData(0, null, null, null)]
    [InlineData(0, "Error", null, null)]
    [InlineData(0, null, "MUnique.OpenMU.GameLogic.Player", null)]
    [InlineData(0, null, null, "permission denied")]
    [InlineData(0, "Error", "MUnique.OpenMU.GameServer.GameServer", null)]
    [InlineData(0, null, null, "nothing matches this")]
    public async Task TheCountAgreesWithTheListingForEveryFilter(int hours, string? level, string? source, string? search)
    {
        Skip.IfNot(Enabled);

        // A count from a different WHERE clause than the rows gives a pager with empty pages.
        var filter = new LogFilter(hours, level, source, search);

        var counted = await this._log.CountAsync(filter, default);
        var listed = await this._log.PageAsync(filter, 0, 1000, default);

        Assert.Equal(listed.Count, counted);
    }

    [SkippableFact]
    public async Task TheWindowExcludesOlderLines()
    {
        Skip.IfNot(Enabled);

        var lastHour = await this._log.CountAsync(new LogFilter(1, null, null, null), default);
        var lastDay = await this._log.CountAsync(new LogFilter(24, null, null, null), default);
        var everything = await this._log.CountAsync(All, default);

        Assert.Equal(3, lastHour);      // the three within minutes
        Assert.Equal(4, lastDay);       // plus the one five hours old
        Assert.Equal(6, everything);    // plus the three-day and forty-day rows

        // 0 means everything, NOT "now minus zero hours" - which would return nothing at all.
        Assert.True(everything > lastDay);
    }

    [SkippableFact]
    public async Task TheSearchCoversTheStackTraceAndNotOnlyTheMessage()
    {
        Skip.IfNot(Enabled);

        // "permission denied" appears ONLY inside the exception. Searching the message alone is the
        // easy mistake, and it hides the text an operator is most likely to paste in.
        var found = await this._log.PageAsync(new LogFilter(0, null, null, "permission denied"), 0, 10, default);

        var entry = Assert.Single(found);
        Assert.Equal("Could not load account", entry.Message);
        Assert.DoesNotContain("permission denied", entry.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PagesDoNotSkipOrRepeatALine()
    {
        Skip.IfNot(Enabled);

        var whole = await this._log.PageAsync(All, 0, 100, default);
        var first = await this._log.PageAsync(All, 0, 4, default);
        var second = await this._log.PageAsync(All, 4, 4, default);

        Assert.Equal(4, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(whole.Select(e => e.Id), first.Concat(second).Select(e => e.Id));
        Assert.Equal(6, whole.Select(e => e.Id).Distinct().Count());

        // Newest first, so an operator sees what just happened without paging.
        Assert.Equal(whole.OrderByDescending(e => e.At).Select(e => e.Id), whole.Select(e => e.Id));
    }

    [SkippableFact]
    public async Task AnOffsetPastTheEndIsEmptyRatherThanWrapping()
    {
        Skip.IfNot(Enabled);

        Assert.Empty(await this._log.PageAsync(All, 500, 50, default));
    }

    [SkippableFact]
    public async Task TheLevelChipsIgnoreTheLevelFilterButRespectTheRest()
    {
        Skip.IfNot(Enabled);

        // The chips have to show what you would get by switching to each level, so selecting Error
        // must not reduce them to Error alone - otherwise there is no way back.
        var chips = await this._log.LevelsAsync(new LogFilter(0, "Error", null, null), default);

        Assert.Contains(chips, c => c.Level == "Information");
        Assert.Contains(chips, c => c.Level == "Warning");
        Assert.Equal(2, chips.Single(c => c.Level == "Error").Lines);

        // ... but they DO respect the source filter, or the numbers would not match the table.
        var scoped = await this._log.LevelsAsync(
            new LogFilter(0, null, "MUnique.OpenMU.GameLogic.Player", null), default);

        Assert.Equal(2, scoped.Sum(c => c.Lines));
        Assert.DoesNotContain(scoped, c => c.Level == "Fatal");
    }

    [SkippableFact]
    public async Task TheCategoryListSkipsLinesWithNoSource()
    {
        Skip.IfNot(Enabled);

        var sources = await this._log.SourcesAsync(All, 40, default);

        Assert.All(sources, s => Assert.False(string.IsNullOrEmpty(s.Source)));
        Assert.Equal(3, sources.Count);
        Assert.Equal("MUnique.OpenMU.GameServer.GameServer", sources[0].Source);   // busiest first
        Assert.Equal(3, sources[0].Lines);
    }

    [SkippableFact]
    public async Task AMissingTableIsReportedRatherThanThrown()
    {
        Skip.IfNot(Enabled);

        // The migrations are baked into the mu-site-migrate image, so an image built before
        // 002_server_log.sql reports success having skipped it. The page has to say that instead of
        // returning a 500 that explains nothing. Pointed at a database with no server_log.
        var elsewhere = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:GameRead"] = ConnectionString,
            ["ConnectionStrings:GameAuth"] = ConnectionString,
            ["ConnectionStrings:GameReg"] = ConnectionString,
            ["ConnectionStrings:Site"] = ConnectionString!.Replace("Database=openmu_web", "Database=openmu"),
        }).Build();

        await using var sources = SiteDataSources.Create(elsewhere);
        var log = new ServerLog(sources);

        Assert.Equal(0, await log.CountAsync(All, default));
        Assert.Empty(await log.PageAsync(All, 0, 10, default));
        Assert.Empty(await log.LevelsAsync(All, default));
        Assert.True(log.TableMissing);
    }
}
