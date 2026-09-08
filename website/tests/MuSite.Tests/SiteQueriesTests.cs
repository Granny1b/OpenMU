using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MuSite.Data;
using MuSite.Game;
using MuSite.Services;
using Npgsql;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Reads from the SITE database (openmu_web) against a real PostgreSQL.
///
/// This suite exists because nothing covered that database. PublicQueriesTests and
/// RegistrationInvariantTests both target the GAME database, so every record materialised out of
/// openmu_web - NewsItem, AuditEntry, BanRecord - was unverified, and all three were broken the
/// same way: they declare DateTimeOffset properties, Npgsql 6 reports timestamptz columns as
/// System.DateTime, and Dapper rejects a constructor whose parameter types do not match what the
/// reader reports. The home page threw on every request as soon as one announcement existed.
///
/// Skipped when MUSITE_TEST_SITE_DB is unset, so `dotnet test` works without a database.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SiteQueriesTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MUSITE_TEST_SITE_DB");

    private SiteDataSources _sources = null!;
    private NpgsqlDataSource _dataSource = null!;

    private static bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        // The handler the application registers in Program.cs. Dapper's registry is static, so
        // registering it here reproduces the running configuration - and a test that passed without
        // this line would be testing something the site does not do.
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());

        this._dataSource = NpgsqlDataSource.Create(ConnectionString!);

        // The schema is applied by db/web/001_init.sql before the tests run; only the rows are ours.
        await using (var command = this._dataSource.CreateCommand(
            "DELETE FROM news; DELETE FROM audit_log; DELETE FROM web_ban;"))
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
    }

    public async Task DisposeAsync()
    {
        if (Enabled)
        {
            await this._sources.DisposeAsync();
            await this._dataSource.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task APublishedAnnouncementMaterialises()
    {
        Skip.IfNot(Enabled);

        var news = new NewsStore(this._sources);
        await news.CreateAsync("Golden Invasion", "**tonight** at eight", "Granny", publish: true, pin: false, default);

        var published = await news.GetPublishedAsync(10, default);

        var item = Assert.Single(published);
        Assert.Equal("Golden Invasion", item.Title);
        Assert.Contains("<strong>tonight</strong>", item.BodyHtml, StringComparison.Ordinal);
        Assert.NotNull(item.PublishedAt);

        // The offset is the point: timestamptz is an instant, and the handler must present it as UTC
        // rather than reinterpreting it in the machine's local zone.
        Assert.Equal(TimeSpan.Zero, item.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, item.PublishedAt!.Value.Offset);
    }

    [SkippableFact]
    public async Task AnUnpublishedAnnouncementHasNoPublishedAt()
    {
        Skip.IfNot(Enabled);

        // The nullable half of the mapping: published_at is NULL here, and Dapper resolves
        // DateTimeOffset? through the same handler.
        var news = new NewsStore(this._sources);
        var id = await news.CreateAsync("Draft", "not yet", "Granny", publish: false, pin: false, default);

        var item = await news.GetByIdAsync(id, default);

        Assert.NotNull(item);
        Assert.False(item!.IsPublished);
        Assert.Null(item.PublishedAt);
        Assert.Empty(await news.GetPublishedAsync(10, default));
    }

    [SkippableFact]
    public async Task AnAuditEntryMaterialises()
    {
        Skip.IfNot(Enabled);

        var audit = new AuditLog(this._sources, NullLogger<AuditLog>.Instance);
        await audit.WriteAsync("account.ban", "Granny", null, "someone", "127.0.0.1", "7 days", default);

        var recent = await audit.RecentAsync(10, default);

        var entry = Assert.Single(recent);
        Assert.Equal("account.ban", entry.Action);
        Assert.Equal("Granny", entry.Actor);
        Assert.Equal(TimeSpan.Zero, entry.At.Offset);
    }

    [SkippableFact]
    public async Task ABanRecordMaterialises()
    {
        Skip.IfNot(Enabled);

        // AdminActions.GetBanHistoryAsync is the caller, but constructing it needs six collaborators
        // (RoleResolver, SessionStore, SessionState, GameAccount, AuditLog, a logger) and a game
        // database besides. What is under test is the CLR mapping of BanRecord's three timestamptz
        // columns, so the row is read directly - one carrying a NULL expires_at and lifted_at, and
        // one carrying values, because those are the nullable and non-nullable paths.
        var accountId = Guid.NewGuid();
        await using (var command = this._dataSource.CreateCommand(
            """
            INSERT INTO web_ban (id, account_id, login_name, prior_state, reason, actor, expires_at, lifted_at)
            VALUES (gen_random_uuid(), @accountId, 'someone', 0, 'testing', 'Granny', NULL, NULL),
                   (gen_random_uuid(), @accountId, 'someone', 0, 'testing', 'Granny',
                    now() + interval '7 days', now());
            """))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            await command.ExecuteNonQueryAsync();
        }

        await using var connection = await this._sources.Site.OpenConnectionAsync(default);
        var rows = (await connection.QueryAsync<BanRecord>(
            """
            SELECT id AS Id, created_at AS CreatedAt, expires_at AS ExpiresAt, lifted_at AS LiftedAt,
                   prior_state AS PriorState, reason AS Reason, actor AS Actor
              FROM web_ban WHERE account_id = @accountId ORDER BY expires_at NULLS FIRST
            """,
            new { accountId })).AsList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(TimeSpan.Zero, row.CreatedAt.Offset));
        Assert.Null(rows[0].ExpiresAt);
        Assert.Null(rows[0].LiftedAt);
        Assert.NotNull(rows[1].ExpiresAt);
        Assert.NotNull(rows[1].LiftedAt);
    }
}
