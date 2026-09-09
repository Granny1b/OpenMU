using Dapper;
using Microsoft.Extensions.Caching.Memory;
using MuSite.Data;
using Npgsql;

namespace MuSite.Services;

/// <summary>One point of a single time series.</summary>
public sealed record Point(DateTimeOffset At, double Value);

/// <summary>One point of a series that is split by device, core or level.</summary>
public sealed record ScopePoint(DateTimeOffset At, string Scope, double Value);

/// <summary>The newest value of a series, per device.</summary>
public sealed record ScopeValue(string Scope, double Value);

/// <summary>What the site last observed about the game server.</summary>
/// <param name="IsUp">Whether the connect server accepted a connection.</param>
/// <param name="Players">Exact players online, or null when no API key is configured.</param>
/// <param name="LoadPercent">Highest load percentage across game servers, or null.</param>
/// <param name="Servers">How many game servers the connect server listed, or null.</param>
public sealed record ServerSample(bool IsUp, int? Players, int? LoadPercent, int? Servers);

/// <summary>
/// Reads the two metric tables behind /admin/metrics, and writes the game server samples.
///
/// COUNTERS ARE STORED RAW AND DIFFERENCED HERE
///
/// cpu_seconds_total and the network and disk byte totals are counters that only ever climb.
/// Vector has no rate transform, and storing a rate would bake in the sampling interval, so the raw
/// counter is stored and the difference is taken in SQL. Two guards matter and are in every one of
/// those queries:
///
///   * a NEGATIVE delta means the counter reset - a reboot - and is dropped rather than drawn as a
///     huge dip;
///   * a delta over a long gap means the collector was down, and is dropped rather than smeared
///     across the gap as if it had been busy the whole time.
///
/// EVERY QUERY REPEATS ITS OWN SQL for the same reason as ServerLog: tools/verify-query-mapping.py
/// checks a complete `const string sql` literal against the record it materialises, and text shared
/// through a helper would be invisible to it.
/// </summary>
public sealed class ServerMetrics(SiteDataSources sources, IMemoryCache cache)
{
    /// <summary>
    /// How long a loaded series is reused before it is read again.
    ///
    /// Slightly under the 30 second collection interval, which makes this close to free rather than
    /// a trade: there IS no newer data between two scrapes, so a shorter window would re-run the
    /// same aggregate over the same rows for the same answer. What it removes is the cost of a
    /// second admin opening the page, or of anyone reloading it - measured at about 150 ms of
    /// database work per uncached load over a 7 day window, and this makes that at most once per
    /// scrape however many people are looking.
    ///
    /// The measurements are server-wide, so one cache serves everyone. Nothing here is per-user,
    /// which is also why the RESPONSE is not cached: doing that on a page behind admin
    /// authentication risks handing one person's view to another.
    /// </summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Deltas spanning more than this many seconds are discarded as a collection gap.
    /// </summary>
    /// <remarks>
    /// Vector scrapes every 30 seconds, so a healthy delta is 30. Six times that is generous enough
    /// to survive a slow scrape or a restart of the shipper, and short enough that an hour of
    /// downtime does not turn into one enormous, meaningless bar.
    /// </remarks>
    private const int MaxGapSeconds = 180;

    /// <summary>True when the metric tables are not there yet, so the page can say so.</summary>
    public bool TablesMissing { get; private set; }

    /// <summary>
    /// Records what the probe just saw.
    /// </summary>
    /// <param name="sample">The observation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteSampleAsync(ServerSample sample, CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO server_sample (at, is_up, players, load_percent, servers)
            VALUES (now(), @isUp, @players, @loadPercent, @servers)
            """;

        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                isUp = sample.IsUp,
                players = sample.Players,
                loadPercent = sample.LoadPercent,
                servers = sample.Servers,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Averages a gauge into time buckets - memory, load, filesystem size.
    /// </summary>
    /// <param name="name">The metric name, as host_metrics emits it.</param>
    /// <param name="hours">How far back to look.</param>
    /// <param name="bucketSeconds">Width of one point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One point per bucket that holds data, oldest first.</returns>
    public Task<IReadOnlyList<Point>> GaugeAsync(
        string name, int hours, int bucketSeconds, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT to_timestamp(floor(extract(epoch FROM at) / @bucket) * @bucket) AS at,
                   avg(value)                                                      AS value
            FROM host_metric
            WHERE name = @name
              AND at >= now() - make_interval(hours => @hours)
            GROUP BY 1
            ORDER BY 1
            """;

        return this.ReadAsync(
            $"gauge:{name}:{hours}:{bucketSeconds}",
            async connection => (IReadOnlyList<Point>)(await connection.QueryAsync<Point>(
                new CommandDefinition(
                    sql,
                    new { name, hours, bucket = bucketSeconds },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList(),
            [],
            cancellationToken);
    }

    /// <summary>
    /// Turns a counter into a per-second rate, bucketed - network and disk throughput.
    /// </summary>
    /// <param name="name">The counter's metric name.</param>
    /// <param name="hours">How far back to look.</param>
    /// <param name="bucketSeconds">Width of one point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Bytes per second per bucket, summed across devices.</returns>
    public Task<IReadOnlyList<Point>> RateAsync(
        string name, int hours, int bucketSeconds, CancellationToken cancellationToken)
    {
        // The counter is per device, so the delta must be taken PER DEVICE and only then summed.
        // Summing first would produce a spike every time an interface appears or disappears.
        const string sql = """
            WITH stepped AS (
                SELECT at,
                       scope,
                       value - lag(value) OVER (PARTITION BY scope ORDER BY at)          AS delta,
                       extract(epoch FROM at - lag(at) OVER (PARTITION BY scope ORDER BY at)) AS seconds
                FROM host_metric
                WHERE name = @name
                  AND at >= now() - make_interval(hours => @hours)
            ),
            good AS (
                SELECT at, delta / seconds AS per_second
                FROM stepped
                WHERE seconds > 0 AND seconds <= @maxGap AND delta >= 0
            ),
            -- Add the devices together FIRST, per scrape, and only then average over the bucket.
            -- Summing straight into the bucket adds up every scrape it contains as well as every
            -- device, so a twelve minute bucket over a thirty second interval reported twenty-four
            -- times the real rate - and the last, partly-filled bucket reported less than the rest,
            -- which is what made it visible as a cliff at the right hand edge of the chart.
            moment AS (
                SELECT at, sum(per_second) AS total
                FROM good
                GROUP BY at
            )
            SELECT to_timestamp(floor(extract(epoch FROM at) / @bucket) * @bucket) AS at,
                   avg(total)                                                      AS value
            FROM moment
            GROUP BY 1
            ORDER BY 1
            """;

        return this.ReadAsync(
            $"rate:{name}:{hours}:{bucketSeconds}",
            async connection => (IReadOnlyList<Point>)(await connection.QueryAsync<Point>(
                new CommandDefinition(
                    sql,
                    new { name, hours, bucket = bucketSeconds, maxGap = MaxGapSeconds },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList(),
            [],
            cancellationToken);
    }

    /// <summary>
    /// How busy the processors were, as a percentage.
    /// </summary>
    /// <remarks>
    /// Derived from the idle counter, which is the only cpu mode collected: busy is the share of
    /// elapsed core-seconds that were NOT idle. Idle is summed across cores first, and the core
    /// count is taken from the same scrape rather than from logical_cpus, so a machine that is
    /// resized mid-window still produces correct percentages on both sides of the change.
    /// </remarks>
    /// <param name="hours">How far back to look.</param>
    /// <param name="bucketSeconds">Width of one point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Busy percentage per bucket, 0-100.</returns>
    public Task<IReadOnlyList<Point>> CpuBusyAsync(int hours, int bucketSeconds, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH scrape AS (
                SELECT at, sum(value) AS idle, count(*) AS cores
                FROM host_metric
                WHERE name = 'cpu_seconds_total'
                  AND at >= now() - make_interval(hours => @hours)
                GROUP BY at
            ),
            stepped AS (
                SELECT at,
                       idle - lag(idle) OVER (ORDER BY at)                         AS idle_delta,
                       extract(epoch FROM at - lag(at) OVER (ORDER BY at))         AS seconds,
                       cores
                FROM scrape
            )
            SELECT to_timestamp(floor(extract(epoch FROM at) / @bucket) * @bucket)          AS at,
                   avg(greatest(0, least(100, 100 * (1 - (idle_delta / (seconds * cores)))))) AS value
            FROM stepped
            WHERE seconds > 0 AND seconds <= @maxGap AND idle_delta >= 0 AND cores > 0
            GROUP BY 1
            ORDER BY 1
            """;

        return this.ReadAsync(
            $"cpu:{hours}:{bucketSeconds}",
            async connection => (IReadOnlyList<Point>)(await connection.QueryAsync<Point>(
                new CommandDefinition(
                    sql,
                    new { hours, bucket = bucketSeconds, maxGap = MaxGapSeconds },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList(),
            [],
            cancellationToken);
    }

    /// <summary>
    /// Averages a gauge into buckets for ONE device.
    /// </summary>
    /// <remarks>
    /// <see cref="GaugeAsync"/> averages across every scope, which is right for a metric collected
    /// once per host and wrong for one collected per device: the mean of a full disk and an empty
    /// one is a half-full disk that does not exist. Anything charted per device comes through here.
    /// </remarks>
    /// <param name="name">The metric name.</param>
    /// <param name="scope">The device, mount or core to restrict to.</param>
    /// <param name="hours">How far back to look.</param>
    /// <param name="bucketSeconds">Width of one point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One point per bucket, oldest first.</returns>
    public Task<IReadOnlyList<Point>> GaugeForScopeAsync(
        string name, string scope, int hours, int bucketSeconds, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT to_timestamp(floor(extract(epoch FROM at) / @bucket) * @bucket) AS at,
                   avg(value)                                                      AS value
            FROM host_metric
            WHERE name = @name
              AND scope = @scope
              AND at >= now() - make_interval(hours => @hours)
            GROUP BY 1
            ORDER BY 1
            """;

        return this.ReadAsync(
            $"gauge:{name}:{scope}:{hours}:{bucketSeconds}",
            async connection => (IReadOnlyList<Point>)(await connection.QueryAsync<Point>(
                new CommandDefinition(
                    sql,
                    new { name, scope, hours, bucket = bucketSeconds },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList(),
            [],
            cancellationToken);
    }

    /// <summary>
    /// The newest value of a metric for every device it is collected per.
    /// </summary>
    /// <param name="name">The metric name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One row per scope, largest first.</returns>
    public Task<IReadOnlyList<ScopeValue>> LatestByScopeAsync(string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT ON (scope) scope, value
            FROM host_metric
            WHERE name = @name
              AND at >= now() - interval '1 hour'
            ORDER BY scope, at DESC
            """;

        return this.ReadAsync(
            $"latest:{name}",
            async connection => (IReadOnlyList<ScopeValue>)(await connection.QueryAsync<ScopeValue>(
                new CommandDefinition(
                    sql,
                    new { name },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList(),
            [],
            cancellationToken);
    }

    /// <summary>
    /// Players online over time, from the site's own samples.
    /// </summary>
    /// <remarks>
    /// Samples whose player count is null - taken while no API key was configured, or while the
    /// status call was failing - are excluded rather than read as zero. A gap in the line is honest
    /// about not knowing; a zero would claim the server had emptied.
    /// </remarks>
    /// <param name="hours">How far back to look.</param>
    /// <param name="bucketSeconds">Width of one point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Average players per bucket.</returns>
    public Task<IReadOnlyList<Point>> PlayersAsync(int hours, int bucketSeconds, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT to_timestamp(floor(extract(epoch FROM at) / @bucket) * @bucket) AS at,
                   avg(players)::double precision                                  AS value
            FROM server_sample
            WHERE at >= now() - make_interval(hours => @hours)
              AND players IS NOT NULL
            GROUP BY 1
            ORDER BY 1
            """;

        return this.ReadAsync(
            $"players:{hours}:{bucketSeconds}",
            async connection => (IReadOnlyList<Point>)(await connection.QueryAsync<Point>(
                new CommandDefinition(
                    sql,
                    new { hours, bucket = bucketSeconds },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList(),
            [],
            cancellationToken);
    }

    /// <summary>
    /// The share of samples in each bucket where the server answered, as a percentage.
    /// </summary>
    /// <param name="hours">How far back to look.</param>
    /// <param name="bucketSeconds">Width of one point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Availability percentage per bucket, 0-100.</returns>
    public Task<IReadOnlyList<Point>> AvailabilityAsync(int hours, int bucketSeconds, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT to_timestamp(floor(extract(epoch FROM at) / @bucket) * @bucket)      AS at,
                   (100.0 * count(*) FILTER (WHERE is_up) / count(*))::double precision AS value
            FROM server_sample
            WHERE at >= now() - make_interval(hours => @hours)
            GROUP BY 1
            ORDER BY 1
            """;

        return this.ReadAsync(
            $"availability:{hours}:{bucketSeconds}",
            async connection => (IReadOnlyList<Point>)(await connection.QueryAsync<Point>(
                new CommandDefinition(
                    sql,
                    new { hours, bucket = bucketSeconds },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList(),
            [],
            cancellationToken);
    }

    /// <summary>
    /// How many log lines each level produced over time.
    /// </summary>
    /// <param name="hours">How far back to look.</param>
    /// <param name="bucketSeconds">Width of one point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Lines per bucket per level.</returns>
    public Task<IReadOnlyList<ScopePoint>> LogRateAsync(int hours, int bucketSeconds, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT to_timestamp(floor(extract(epoch FROM at) / @bucket) * @bucket) AS at,
                   level                                                           AS scope,
                   count(*)::double precision                                      AS value
            FROM server_log
            WHERE at >= now() - make_interval(hours => @hours)
            GROUP BY 1, 2
            ORDER BY 1, 2
            """;

        return this.ReadAsync(
            $"lograte:{hours}:{bucketSeconds}",
            async connection => (IReadOnlyList<ScopePoint>)(await connection.QueryAsync<ScopePoint>(
                new CommandDefinition(
                    sql,
                    new { hours, bucket = bucketSeconds },
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList(),
            [],
            cancellationToken);
    }

    /// <summary>
    /// Runs a read, reporting a missing table rather than throwing.
    /// </summary>
    /// <remarks>
    /// Migrations are baked into the mu-site-migrate image at build time, so a site deployed with a
    /// migrate image built before 003 exists will find these tables absent and would otherwise show
    /// a 500 with nothing to act on. ONLY "undefined table" is caught: a permission problem must
    /// still surface as an error, not be disguised as "collection has not started".
    /// </remarks>
    /// <typeparam name="T">What the read returns.</typeparam>
    /// <param name="key">Cache key identifying this exact series and window.</param>
    /// <param name="read">The read.</param>
    /// <param name="whenMissing">What to return when the table is not there.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The read's result, or <paramref name="whenMissing"/>.</returns>
    private async Task<T> ReadAsync<T>(
        string key,
        Func<NpgsqlConnection, Task<T>> read,
        T whenMissing,
        CancellationToken cancellationToken)
    {
        if (this.CacheFor > TimeSpan.Zero && cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var result = await read(connection).ConfigureAwait(false);
            this.TablesMissing = false;

            if (this.CacheFor > TimeSpan.Zero)
            {
                cache.Set(key, result, this.CacheFor);
            }

            return result;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            this.TablesMissing = true;
            return whenMissing;
        }
    }
}
