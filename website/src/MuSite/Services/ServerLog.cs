using Dapper;
using MuSite.Data;
using Npgsql;

namespace MuSite.Services;

/// <summary>One line of the game server's log.</summary>
public sealed record LogEntry(
    Guid Id, DateTimeOffset At, string Level, string? Source, string? EventId,
    string Message, string? Exception);

/// <summary>How many lines a level has, within the current filter.</summary>
public sealed record LevelCount(string Level, int Lines);

/// <summary>How many lines a category has, within the current time window.</summary>
public sealed record SourceCount(string Source, int Lines);

/// <summary>
/// What /admin/logs is looking at. Hours of 0 means every row still in the table.
/// </summary>
public sealed record LogFilter(int Hours, string? Level, string? Source, string? Search)
{
    /// <summary>The parameters every query below takes, normalised for SQL.</summary>
    public object Parameters => new
    {
        hours = this.Hours,
        level = this.Level ?? string.Empty,
        source = this.Source ?? string.Empty,
        search = this.Search?.Trim() ?? string.Empty,
    };
}

/// <summary>
/// Reads the server log that Vector ships into openmu_web.server_log.
///
/// The site can SELECT and DELETE here and cannot INSERT - the shipper holds INSERT and nothing
/// else. So a compromised site can neither forge a log line nor read one it forged; see
/// db/web/002_server_log.sql. Pruning is LogRetentionService, not this class.
///
/// EVERY QUERY REPEATS THE SAME WHERE CLAUSE, deliberately. Sharing it through a const would hide
/// it from tools/verify-query-mapping.py, which only sees a complete `const string sql` literal -
/// and the risk that the count disagrees with the listing is covered by a test that runs both over
/// the same filters rather than by textual sharing, which is what actually broke the item pager.
/// </summary>
public sealed class ServerLog(SiteDataSources sources)
{
    /// <summary>
    /// True when server_log does not exist. The migrations are baked into the mu-site-migrate
    /// image, so an image built before 002_server_log.sql reports success having skipped it - the
    /// page says so rather than throwing, because that exact mistake is easy to make and the
    /// resulting 500 says nothing useful.
    /// </summary>
    public bool TableMissing { get; private set; }

    /// <summary>How many lines match, for the pager.</summary>
    public async Task<int> CountAsync(LogFilter filter, CancellationToken cancellationToken)
    {
        const string countSql =
            """
            SELECT count(*)::int
              FROM server_log
             WHERE (@hours <= 0 OR at >= now() - make_interval(hours => @hours))
               AND (@level  = '' OR level  = @level)
               AND (@source = '' OR source = @source)
               AND (@search = '' OR message ILIKE '%' || @search || '%'
                                 OR exception ILIKE '%' || @search || '%')
            """;

        return await this.ReadAsync(
            async connection => await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                countSql, filter.Parameters, cancellationToken: cancellationToken)).ConfigureAwait(false),
            0,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One page of lines, newest first.</summary>
    public async Task<IReadOnlyList<LogEntry>> PageAsync(
        LogFilter filter, int offset, int limit, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT id        AS Id,
                   at        AS At,
                   level     AS Level,
                   source    AS Source,
                   event_id  AS EventId,
                   message   AS Message,
                   exception AS Exception
              FROM server_log
             WHERE (@hours <= 0 OR at >= now() - make_interval(hours => @hours))
               AND (@level  = '' OR level  = @level)
               AND (@source = '' OR source = @source)
               AND (@search = '' OR message ILIKE '%' || @search || '%'
                                 OR exception ILIKE '%' || @search || '%')
             ORDER BY at DESC, id DESC
             LIMIT @limit OFFSET @offset
            """;

        return await this.ReadAsync(
            async connection =>
            {
                var rows = await connection.QueryAsync<LogEntry>(new CommandDefinition(
                    sql,
                    Merge(filter, offset, limit),
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                return (IReadOnlyList<LogEntry>)rows.AsList();
            },
            [],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The levels present, with counts, for the level filter. Deliberately ignores the level part
    /// of the filter: the chips have to show what you would get by switching to each one.
    /// </summary>
    public async Task<IReadOnlyList<LevelCount>> LevelsAsync(LogFilter filter, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT level          AS Level,
                   count(*)::int  AS Lines
              FROM server_log
             WHERE (@hours <= 0 OR at >= now() - make_interval(hours => @hours))
               AND (@source = '' OR source = @source)
               AND (@search = '' OR message ILIKE '%' || @search || '%'
                                 OR exception ILIKE '%' || @search || '%')
             GROUP BY level
             ORDER BY 2 DESC
            """;

        return await this.ReadAsync(
            async connection =>
            {
                var rows = await connection.QueryAsync<LevelCount>(new CommandDefinition(
                    sql, filter.Parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
                return (IReadOnlyList<LevelCount>)rows.AsList();
            },
            [],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The busiest categories in the window, for the category filter. Capped because a server can
    /// log from more classes than anyone wants in a dropdown.
    /// </summary>
    public async Task<IReadOnlyList<SourceCount>> SourcesAsync(
        LogFilter filter, int limit, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT source         AS Source,
                   count(*)::int  AS Lines
              FROM server_log
             WHERE source IS NOT NULL
               AND (@hours <= 0 OR at >= now() - make_interval(hours => @hours))
             GROUP BY source
             ORDER BY 2 DESC, 1 ASC
             LIMIT @limit
            """;

        return await this.ReadAsync(
            async connection =>
            {
                var rows = await connection.QueryAsync<SourceCount>(new CommandDefinition(
                    sql,
                    new { hours = filter.Hours, limit },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                return (IReadOnlyList<SourceCount>)rows.AsList();
            },
            [],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The offset/limit merged onto the filter's own parameters.</summary>
    private static object Merge(LogFilter filter, int offset, int limit) => new
    {
        hours = filter.Hours,
        level = filter.Level ?? string.Empty,
        source = filter.Source ?? string.Empty,
        search = filter.Search?.Trim() ?? string.Empty,
        offset,
        limit,
    };

    /// <summary>
    /// Runs a read, turning "the table is not there" into a flag rather than an exception.
    /// 42P01 is undefined_table; anything else is a real fault and still throws.
    /// </summary>
    private async Task<T> ReadAsync<T>(
        Func<NpgsqlConnection, Task<T>> read, T whenMissing, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await sources.Site
                .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var result = await read(connection).ConfigureAwait(false);
            this.TableMissing = false;
            return result;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            this.TableMissing = true;
            return whenMissing;
        }
    }
}
