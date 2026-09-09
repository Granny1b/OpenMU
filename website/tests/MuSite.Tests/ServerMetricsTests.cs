using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using MuSite.Data;
using MuSite.Services;
using Npgsql;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The /admin/metrics queries, against a real PostgreSQL.
///
/// The arithmetic is what these cover, not that the SQL parses - the mapping verifier already runs
/// every one of these against the live schema. Counters are the risk: cpu_seconds_total and the
/// byte totals only climb, so every reading has to be differenced against the one before it, and
/// the two ways that goes wrong both draw a convincing lie.
///
///   * A REBOOT resets the counter. The delta goes negative, and a chart that trusts it shows an
///     enormous dip in processor use at the exact moment the machine was least available.
///   * A COLLECTION GAP leaves two readings far apart. The delta is real but the elapsed time is
///     too, and averaging one over the other smears a busy minute across an hour of downtime.
///
/// Skipped when MUSITE_TEST_SITE_DB is unset.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ServerMetricsTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MUSITE_TEST_SITE_DB");

    private SiteDataSources _sources = null!;
    private NpgsqlDataSource _dataSource = null!;
    private ServerMetrics _metrics = null!;

    /// <summary>
    /// One instant that every inserted row is measured back from.
    /// </summary>
    /// <remarks>
    /// Calling now() per INSERT would give the two cores of a single scrape timestamps microseconds
    /// apart, and CpuBusyAsync groups by the exact timestamp to find the cores of one scrape. That
    /// grouping is right - Vector stamps every metric in a scrape with the same instant - so it is
    /// the fixture that has to be realistic, not the query that has to be loosened.
    /// </remarks>
    private DateTimeOffset _base;

    private static bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        this._dataSource = NpgsqlDataSource.Create(ConnectionString!);
        this._base = DateTimeOffset.UtcNow;

        await this.ExecuteAsync("DELETE FROM host_metric");
        await this.ExecuteAsync("DELETE FROM server_sample");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:GameRead"] = ConnectionString,
            ["ConnectionStrings:GameAuth"] = ConnectionString,
            ["ConnectionStrings:GameReg"] = ConnectionString,
            ["ConnectionStrings:Site"] = ConnectionString,
        }).Build();

        this._sources = SiteDataSources.Create(configuration);
        this._metrics = NoCache(this._sources);
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
    public async Task ProcessorBusyIsTheShareOfCoreSecondsThatWereNotIdle()
    {
        Skip.IfNot(Enabled);

        // Two cores, thirty seconds apart. Together they could have been idle for 60 core-seconds
        // and were idle for 45, so the machine was busy for a quarter of its capacity.
        await this.CpuAsync(60, "0", 1000);
        await this.CpuAsync(60, "1", 2000);
        await this.CpuAsync(30, "0", 1022.5);
        await this.CpuAsync(30, "1", 2022.5);

        var series = await this._metrics.CpuBusyAsync(1, 30, default);

        Assert.Single(series);
        Assert.Equal(25, series[0].Value, 1);
    }

    [SkippableFact]
    public async Task ARebootIsDroppedRatherThanDrawnAsAHugeDip()
    {
        Skip.IfNot(Enabled);

        // The counter goes backwards, which cannot happen except by a reset.
        await this.CpuAsync(60, "0", 5000);
        await this.CpuAsync(30, "0", 12);

        var series = await this._metrics.CpuBusyAsync(1, 30, default);

        Assert.Empty(series);
    }

    [SkippableFact]
    public async Task AGapInCollectionIsDroppedRatherThanSmearedAcrossIt()
    {
        Skip.IfNot(Enabled);

        // Fifty minutes apart: the shipper was down. The delta is real, but attributing it evenly
        // to the whole gap would report a near-idle machine for an hour nobody measured.
        await this.CpuAsync(60 * 60, "0", 1000);
        await this.CpuAsync(10 * 60, "0", 1100);

        var series = await this._metrics.CpuBusyAsync(24, 60, default);

        Assert.Empty(series);
    }

    [SkippableFact]
    public async Task AThroughputRateIsDifferencedPerDeviceAndThenAdded()
    {
        Skip.IfNot(Enabled);

        // Two interfaces, each climbing by 3000 bytes over 30 seconds: 100 B/s each, 200 together.
        // Adding the counters BEFORE differencing would be right here too - the trap is a device
        // that appears mid-window, which the next test covers.
        await this.MetricAsync("network_receive_bytes_total", 60, "eth0", 10_000);
        await this.MetricAsync("network_receive_bytes_total", 60, "eth1", 50_000);
        await this.MetricAsync("network_receive_bytes_total", 30, "eth0", 13_000);
        await this.MetricAsync("network_receive_bytes_total", 30, "eth1", 53_000);

        var series = await this._metrics.RateAsync("network_receive_bytes_total", 1, 30, default);

        Assert.Single(series);
        Assert.Equal(200, series[0].Value, 1);
    }

    [SkippableFact]
    public async Task ARateIsAveragedOverTheBucketAndNotAddedUpAcrossIt()
    {
        Skip.IfNot(Enabled);

        // Four scrapes, each 30 seconds apart at a steady 100 B/s, asked for as ONE bucket. The
        // answer is 100 B/s, not 300. Adding the per-second rates of every scrape in a bucket
        // multiplies the reading by however many scrapes the bucket happens to hold, which on the
        // dashboard's 12 minute buckets was a factor of twenty-four.
        await this.MetricAsync("network_receive_bytes_total", 120, "eth0", 0);
        await this.MetricAsync("network_receive_bytes_total", 90, "eth0", 3_000);
        await this.MetricAsync("network_receive_bytes_total", 60, "eth0", 6_000);
        await this.MetricAsync("network_receive_bytes_total", 30, "eth0", 9_000);

        var series = await this._metrics.RateAsync("network_receive_bytes_total", 1, 3600, default);

        Assert.Single(series);
        Assert.Equal(100, series[0].Value, 1);
    }

    [SkippableFact]
    public async Task ADeviceAppearingMidWindowDoesNotRegisterAsAnEnormousSpike()
    {
        Skip.IfNot(Enabled);

        // eth1 shows up already carrying 900 000 bytes of history - a container starting, say.
        // Summing the counters first would treat that entire history as traffic in one bucket.
        await this.MetricAsync("network_receive_bytes_total", 60, "eth0", 10_000);
        await this.MetricAsync("network_receive_bytes_total", 30, "eth0", 13_000);
        await this.MetricAsync("network_receive_bytes_total", 30, "eth1", 900_000);

        var series = await this._metrics.RateAsync("network_receive_bytes_total", 1, 30, default);

        Assert.Single(series);
        Assert.Equal(100, series[0].Value, 1);
    }

    [SkippableFact]
    public async Task TheSameDiskMountedTwiceIsNotCountedTwice()
    {
        Skip.IfNot(Enabled);

        // Vector keys filesystems by DEVICE precisely so that the log volume and vector's own data
        // directory - both on the host disk - collapse into one series instead of drawing the disk
        // as two identical lines. Averaging identical values gives that value back.
        await this.MetricAsync("filesystem_used_bytes", 30, "/dev/vda", 500);
        await this.MetricAsync("filesystem_used_bytes", 30, "/dev/vda", 500);

        var series = await this._metrics.GaugeForScopeAsync("filesystem_used_bytes", "/dev/vda", 1, 30, default);

        Assert.Single(series);
        Assert.Equal(500, series[0].Value, 1);
    }

    [SkippableFact]
    public async Task APerDeviceGaugeDoesNotAverageOneDiskIntoAnother()
    {
        Skip.IfNot(Enabled);

        // A full disk and an empty one do not make two half-full disks.
        await this.MetricAsync("filesystem_used_bytes", 30, "/dev/vda", 1000);
        await this.MetricAsync("filesystem_used_bytes", 30, "/dev/vdb", 0);

        var scoped = await this._metrics.GaugeForScopeAsync("filesystem_used_bytes", "/dev/vda", 1, 30, default);
        var averaged = await this._metrics.GaugeAsync("filesystem_used_bytes", 1, 30, default);

        Assert.Equal(1000, scoped[0].Value, 1);
        Assert.Equal(500, averaged[0].Value, 1);
    }

    [SkippableFact]
    public async Task ASampleWithNoPlayerCountIsAGapAndNotAZero()
    {
        Skip.IfNot(Enabled);

        // Recording "unknown" as zero would draw a server that emptied out every time the API key
        // was wrong, which is the opposite of what happened.
        await this._metrics.WriteSampleAsync(new ServerSample(true, 12, 3, 1), default);
        await this._metrics.WriteSampleAsync(new ServerSample(true, null, 3, 1), default);

        var series = await this._metrics.PlayersAsync(1, 3600, default);

        Assert.Single(series);
        Assert.Equal(12, series[0].Value, 1);
    }

    [SkippableFact]
    public async Task AvailabilityIsTheShareOfProbesThatWereAnswered()
    {
        Skip.IfNot(Enabled);

        await this._metrics.WriteSampleAsync(new ServerSample(true, null, null, null), default);
        await this._metrics.WriteSampleAsync(new ServerSample(true, null, null, null), default);
        await this._metrics.WriteSampleAsync(new ServerSample(true, null, null, null), default);
        await this._metrics.WriteSampleAsync(new ServerSample(false, null, null, null), default);

        var series = await this._metrics.AvailabilityAsync(1, 3600, default);

        Assert.Single(series);
        Assert.Equal(75, series[0].Value, 1);
    }

    [SkippableFact]
    public async Task TheWindowExcludesOlderMeasurements()
    {
        Skip.IfNot(Enabled);

        await this.MetricAsync("load1", 30, string.Empty, 1);
        await this.MetricAsync("load1", 3 * 3600, string.Empty, 9);

        var recent = await this._metrics.GaugeAsync("load1", 1, 60, default);
        var wider = await this._metrics.GaugeAsync("load1", 6, 60, default);

        Assert.Single(recent);
        Assert.Equal(2, wider.Count);
    }

    [SkippableFact]
    public async Task BucketsAveragePointsTogetherRatherThanReturningEveryRow()
    {
        Skip.IfNot(Enabled);

        // Four readings inside one hour, asked for as one hour-wide bucket.
        await this.MetricAsync("load1", 100, string.Empty, 1);
        await this.MetricAsync("load1", 200, string.Empty, 2);
        await this.MetricAsync("load1", 300, string.Empty, 3);
        await this.MetricAsync("load1", 400, string.Empty, 4);

        var series = await this._metrics.GaugeAsync("load1", 1, 3600, default);

        Assert.Single(series);
        Assert.Equal(2.5, series[0].Value, 2);
    }

    [SkippableFact]
    public async Task TheNewestValuePerDeviceIsWhatLatestReturns()
    {
        Skip.IfNot(Enabled);

        await this.MetricAsync("filesystem_total_bytes", 300, "/dev/vda", 100);
        await this.MetricAsync("filesystem_total_bytes", 30, "/dev/vda", 270);
        await this.MetricAsync("filesystem_total_bytes", 30, "/dev/vdb", 50);

        var latest = await this._metrics.LatestByScopeAsync("filesystem_total_bytes", default);

        Assert.Equal(2, latest.Count);
        Assert.Equal(270, latest.Single(row => row.Scope == "/dev/vda").Value, 1);
    }

    [SkippableFact]
    public async Task AMissingTableIsReportedRatherThanThrown()
    {
        Skip.IfNot(Enabled);

        // Points at the GAME database, which has no host_metric - the same shape as a site deployed
        // with a migrate image built before 003 existed.
        var game = Environment.GetEnvironmentVariable("MUSITE_TEST_DB");
        Skip.If(string.IsNullOrWhiteSpace(game));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:GameRead"] = game,
            ["ConnectionStrings:GameAuth"] = game,
            ["ConnectionStrings:GameReg"] = game,
            ["ConnectionStrings:Site"] = game,
        }).Build();

        await using var sources = SiteDataSources.Create(configuration);
        var metrics = NoCache(sources);

        var series = await metrics.GaugeAsync("load1", 1, 60, default);

        Assert.Empty(series);
        Assert.True(metrics.TablesMissing);
    }

    [SkippableFact]
    public async Task ASeriesIsServedFromMemoryUntilTheNextScrapeIsDue()
    {
        Skip.IfNot(Enabled);

        // The dashboard runs eleven aggregates per load, about 150 ms of database work over a seven
        // day window. Without this, every reload and every second admin pays it again - and there
        // is nothing to pay it FOR, because no new measurement exists between two scrapes.
        var cached = new ServerMetrics(this._sources, new MemoryCache(new MemoryCacheOptions()));

        await this.MetricAsync("load1", 30, string.Empty, 1);
        var first = await cached.GaugeAsync("load1", 1, 3600, default);

        await this.MetricAsync("load1", 20, string.Empty, 99);
        var second = await cached.GaugeAsync("load1", 1, 3600, default);

        Assert.Equal(first[0].Value, second[0].Value, 3);

        // A different window is a different key, so it is read rather than wrongly reused.
        var wider = await cached.GaugeAsync("load1", 6, 3600, default);
        Assert.Equal(50, wider[0].Value, 1);
    }

    /// <summary>
    /// A ServerMetrics with caching off.
    /// </summary>
    /// <remarks>
    /// These tests insert rows and then immediately read them back, which is precisely the pattern
    /// the 25 second cache is designed to serve from memory. Left on, every test after the first
    /// read of a series would be asserting against the previous test's answer.
    /// </remarks>
    private static ServerMetrics NoCache(SiteDataSources sources) =>
        new(sources, new MemoryCache(new MemoryCacheOptions())) { CacheFor = TimeSpan.Zero };

    private Task CpuAsync(int secondsAgo, string core, double idleSeconds) =>
        this.MetricAsync("cpu_seconds_total", secondsAgo, core, idleSeconds);

    private async Task MetricAsync(string name, int secondsAgo, string scope, double value)
    {
        await using var command = this._dataSource.CreateCommand(
            "INSERT INTO host_metric (at, name, scope, value) VALUES ($1, $2, $3, $4)");
        command.Parameters.AddWithValue(this._base.AddSeconds(-secondsAgo));
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(scope);
        command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = this._dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
