using Dapper;

namespace MuSite.Data;

/// <summary>Which ranking board is being shown.</summary>
public enum RankingBoard
{
    /// <summary>Highest character level.</summary>
    Level,

    /// <summary>Most resets.</summary>
    Resets,

    /// <summary>Highest master level.</summary>
    Master,

    /// <summary>Most player kills.</summary>
    Pk,
}

/// <summary>One row of a ranking board.</summary>
public sealed record RankingRow(
    string Name, string? Class, int Level, int MasterLevel, int Resets,
    int Pk, int HeroState, int CharStatus, string? Guild);

/// <summary>Everything a character profile page shows.</summary>
public sealed record CharacterProfile(
    string Name, string? Class, int Level, int MasterLevel, int Resets, int Pk,
    int HeroState, int CharStatus, DateTime Created, string? Map,
    string? Guild, int? GuildPosition,
    int Strength, int Agility, int Vitality, int Energy, int Leadership);

/// <summary>A guild in the list.</summary>
public sealed record GuildRow(string Name, int Score, int Members);

/// <summary>A guild's roster entry.</summary>
public sealed record GuildMemberRow(string Name, string? Class, int Level, int Resets, int Position);

/// <summary>A guild profile with its roster.</summary>
public sealed record GuildProfile(string Name, int Score, string? Notice, IReadOnlyList<GuildMemberRow> Members);

/// <summary>The guild row on its own, before the roster is read.</summary>
public sealed record GuildHeader(string Name, int Score, string? Notice);

/// <summary>Account and character totals for the home page.</summary>
public sealed record SiteTotals(int Accounts, int Characters);

/// <summary>
/// Every read the public pages make, in one place, as parameterised SQL over the mu_web_read pool.
///
/// Three things every query here must do, each verified against a live PostgreSQL with the hazard
/// present:
///
///   1. DEDUPLICATE data."StatAttribute". There is no unique index on (CharacterId, DefinitionId) -
///      only separate indexes on each - so a character can carry two Level rows. Without the
///      MAX(...) FILTER aggregation that character appears twice in a ranking, at two levels.
///   2. EXCLUDE bot, template and banned accounts. Bots are ordinary accounts with IsBot set, and
///      a banned account's characters keep their levels; without the filter a banned cheater stays
///      at the top of the board.
///   3. TOLERATE missing rows. A non-master character has no Master Level row at all, and a brand
///      new one may be missing several - LEFT JOIN plus COALESCE, never an inner join on the stat.
/// </summary>
public sealed class PublicQueries(SiteDataSources sources)
{
    /// <summary>
    /// Deduplicated stats per character, restricted to the three ids the boards need so the
    /// ("DefinitionId", "Value") index is usable.
    /// </summary>
    private const string BoardStatsCte =
        """
        WITH stats AS (
            SELECT sa."CharacterId" AS cid,
                   MAX(sa."Value") FILTER (WHERE sa."DefinitionId" = @levelId)  AS lvl,
                   MAX(sa."Value") FILTER (WHERE sa."DefinitionId" = @masterId) AS mlvl,
                   MAX(sa."Value") FILTER (WHERE sa."DefinitionId" = @resetId)  AS resets
              FROM data."StatAttribute" sa
             WHERE sa."DefinitionId" IN (@levelId, @masterId, @resetId)
               AND sa."CharacterId" IS NOT NULL
             GROUP BY sa."CharacterId"
        )
        """;

    /// <summary>
    /// The visible-character projection. The JOIN to data."Account" is deliberately INNER: it both
    /// applies the exclusions and drops characters whose "AccountId" is null.
    /// </summary>
    private const string BoardSelect =
        """
        SELECT c."Name"                                    AS Name,
               cl."Name"                                   AS Class,
               COALESCE(s.lvl, 0)::int                     AS Level,
               COALESCE(s.mlvl, 0)::int                    AS MasterLevel,
               COALESCE(s.resets, 0)::int                  AS Resets,
               c."PlayerKillCount"                         AS Pk,
               c."State"                                   AS HeroState,
               c."CharacterStatus"                         AS CharStatus,
               g."Name"                                    AS Guild
          FROM data."Character" c
          JOIN data."Account" a                ON a."Id" = c."AccountId"
          LEFT JOIN stats s                    ON s.cid = c."Id"
          LEFT JOIN config."CharacterClass" cl ON cl."Id" = c."CharacterClassId"
          LEFT JOIN guild."GuildMember" gm     ON gm."Id" = c."Id"
          LEFT JOIN guild."Guild" g            ON g."Id" = gm."GuildId"
         WHERE a."IsBot" = false
           AND a."IsTemplate" = false
           AND a."State" NOT IN (4, 5)
        """;

    /// <summary>
    /// Ordering per board. Chosen from this fixed table by a validated enum - never interpolated
    /// from the route value, because ORDER BY cannot be a parameter.
    ///
    /// Each ends with "Experience" DESC then "Name" ASC. Without a total order, two characters on
    /// the same level swap places between requests and paging silently repeats or skips rows.
    /// </summary>
    private static readonly Dictionary<RankingBoard, string> BoardOrder = new()
    {
        [RankingBoard.Level] = """ AND COALESCE(s.lvl, 0) > 0 ORDER BY COALESCE(s.lvl, 0) DESC, c."Experience" DESC, c."Name" ASC """,
        [RankingBoard.Resets] = """ AND COALESCE(s.resets, 0) > 0 ORDER BY COALESCE(s.resets, 0) DESC, COALESCE(s.lvl, 0) DESC, c."Name" ASC """,
        [RankingBoard.Master] = """ AND COALESCE(s.mlvl, 0) > 0 ORDER BY COALESCE(s.mlvl, 0) DESC, c."MasterExperience" DESC, c."Name" ASC """,
        [RankingBoard.Pk] = """ AND c."PlayerKillCount" > 0 ORDER BY c."PlayerKillCount" DESC, COALESCE(s.lvl, 0) DESC, c."Name" ASC """,
    };

    /// <summary>The three stat ids every board query binds.</summary>
    private static DynamicParameters StatParameters()
    {
        var parameters = new DynamicParameters();
        parameters.Add("levelId", StatIds.Level);
        parameters.Add("masterId", StatIds.MasterLevel);
        parameters.Add("resetId", StatIds.Resets);
        return parameters;
    }

    /// <summary>Reads one page of a ranking board.</summary>
    public async Task<IReadOnlyList<RankingRow>> GetRankingAsync(RankingBoard board, int offset, int limit, string? search, CancellationToken cancellationToken)
    {
        var filter = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : """ AND lower(c."Name") LIKE lower(@search) || '%' """;

        var sql = BoardStatsCte + BoardSelect + filter + BoardOrder[board] + " LIMIT @limit OFFSET @offset";

        var parameters = StatParameters();
        parameters.Add("limit", limit);
        parameters.Add("offset", offset);
        if (filter.Length > 0)
        {
            parameters.Add("search", search);
        }

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RankingRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>Counts the rows a board would return, for the pager.</summary>
    public async Task<int> CountRankingAsync(RankingBoard board, string? search, CancellationToken cancellationToken)
    {
        var filter = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : """ AND lower(c."Name") LIKE lower(@search) || '%' """;

        // Same predicates as the board, minus the ORDER BY - take the qualifier the board appends
        // and cut it at ORDER BY so the two can never drift apart.
        var qualifier = BoardOrder[board];
        var orderIndex = qualifier.IndexOf("ORDER BY", StringComparison.Ordinal);
        var predicate = orderIndex < 0 ? qualifier : qualifier[..orderIndex];

        var sql = BoardStatsCte + """
            SELECT count(*)
              FROM data."Character" c
              JOIN data."Account" a ON a."Id" = c."AccountId"
              LEFT JOIN stats s     ON s.cid = c."Id"
             WHERE a."IsBot" = false
               AND a."IsTemplate" = false
               AND a."State" NOT IN (4, 5)
            """ + filter + predicate;

        var parameters = StatParameters();
        if (filter.Length > 0)
        {
            parameters.Add("search", search);
        }

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one character profile by name. The lookup is case-insensitive so a typed or copied URL
    /// works, but data."Character"."Name" carries a CASE-SENSITIVE unique index, so 'Bob' and 'bob'
    /// can both exist: the exact-case match is preferred, and only one row is ever returned.
    /// </summary>
    public async Task<CharacterProfile?> GetCharacterAsync(string name, CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH stats AS (
                SELECT sa."CharacterId" AS cid, sa."DefinitionId" AS def, MAX(sa."Value") AS val
                  FROM data."StatAttribute" sa
                 WHERE sa."CharacterId" IS NOT NULL
                 GROUP BY sa."CharacterId", sa."DefinitionId"
            )
            -- COLUMN ORDER IS LOAD-BEARING. Dapper's DefaultTypeMap.FindConstructor walks the
            -- constructor parameters and the reader's columns TOGETHER by index, comparing
            -- ctorParameters[i].Name to names[i]. A column list that carries every name but in a
            -- different order matches nothing, and Dapper throws "a parameterless default
            -- constructor or one matching signature (...) is required" at the first row - not at
            -- compile time, and not on an empty result. This list is in CharacterProfile's
            -- declaration order (Name, Class, Level, MasterLevel, Resets, Pk, ...); keep it that
            -- way, and change both together or neither.
            SELECT c."Name"                    AS Name,
                   cl."Name"                   AS Class,
                   COALESCE(MAX(s.val) FILTER (WHERE s.def = @levelId), 0)::int      AS Level,
                   COALESCE(MAX(s.val) FILTER (WHERE s.def = @masterId), 0)::int     AS MasterLevel,
                   COALESCE(MAX(s.val) FILTER (WHERE s.def = @resetId), 0)::int      AS Resets,
                   c."PlayerKillCount"         AS Pk,
                   c."State"                   AS HeroState,
                   c."CharacterStatus"         AS CharStatus,
                   c."CreateDate"              AS Created,
                   m."Name"                    AS Map,
                   g."Name"                    AS Guild,
                   gm."Status"::int            AS GuildPosition,
                   COALESCE(MAX(s.val) FILTER (WHERE s.def = @strId), 0)::int        AS Strength,
                   COALESCE(MAX(s.val) FILTER (WHERE s.def = @agiId), 0)::int        AS Agility,
                   COALESCE(MAX(s.val) FILTER (WHERE s.def = @vitId), 0)::int        AS Vitality,
                   COALESCE(MAX(s.val) FILTER (WHERE s.def = @eneId), 0)::int        AS Energy,
                   COALESCE(MAX(s.val) FILTER (WHERE s.def = @cmdId), 0)::int        AS Leadership
              FROM data."Character" c
              JOIN data."Account" a                ON a."Id" = c."AccountId"
              LEFT JOIN stats s                    ON s.cid = c."Id"
              LEFT JOIN config."CharacterClass" cl ON cl."Id" = c."CharacterClassId"
              LEFT JOIN config."GameMapDefinition" m ON m."Id" = c."CurrentMapId"
              LEFT JOIN guild."GuildMember" gm     ON gm."Id" = c."Id"
              LEFT JOIN guild."Guild" g            ON g."Id" = gm."GuildId"
             WHERE lower(c."Name") = lower(@name)
               AND a."IsBot" = false
               AND a."IsTemplate" = false
               AND a."State" NOT IN (4, 5)
             GROUP BY c."Name", cl."Name", c."PlayerKillCount", c."State", c."CharacterStatus",
                      c."CreateDate", m."Name", g."Name", gm."Status"
             ORDER BY (c."Name" = @name) DESC
             LIMIT 1
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<CharacterProfile>(new CommandDefinition(sql, new
        {
            name,
            levelId = StatIds.Level,
            masterId = StatIds.MasterLevel,
            resetId = StatIds.Resets,
            strId = StatIds.BaseStrength,
            agiId = StatIds.BaseAgility,
            vitId = StatIds.BaseVitality,
            eneId = StatIds.BaseEnergy,
            cmdId = StatIds.BaseLeadership,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Lists guilds by score. count(gm."Id"), not count(*), so an empty guild reads 0.</summary>
    public async Task<IReadOnlyList<GuildRow>> GetGuildsAsync(int offset, int limit, string? search, CancellationToken cancellationToken)
    {
        var filter = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : """ WHERE lower(g."Name") LIKE lower(@search) || '%' """;

        // Counts only members the roster page will actually show. Counting raw GuildMember rows
        // instead would list a guild as having members whose profile page then shows nobody, because
        // the roster excludes banned, bot and template accounts.
        var sql = """
            SELECT g."Name" AS Name, g."Score" AS Score, count(a."Id")::int AS Members
              FROM guild."Guild" g
              LEFT JOIN guild."GuildMember" gm ON gm."GuildId" = g."Id"
              LEFT JOIN data."Character" c ON c."Id" = gm."Id"
              LEFT JOIN data."Account" a ON a."Id" = c."AccountId"
                                        AND a."IsBot" = false
                                        AND a."IsTemplate" = false
                                        AND a."State" NOT IN (4, 5)
            """ + filter + """
             GROUP BY g."Id", g."Name", g."Score"
             ORDER BY g."Score" DESC, g."Name" ASC
             LIMIT @limit OFFSET @offset
            """;

        var parameters = new DynamicParameters();
        parameters.Add("limit", limit);
        parameters.Add("offset", offset);
        if (filter.Length > 0)
        {
            parameters.Add("search", search);
        }

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<GuildRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>Reads a guild and its roster. Members on excluded accounts are left out of the roster.</summary>
    public async Task<GuildProfile?> GetGuildAsync(string name, CancellationToken cancellationToken)
    {
        const string guildSql =
            """
            SELECT g."Name" AS Name, g."Score" AS Score, g."Notice" AS Notice
              FROM guild."Guild" g
             WHERE lower(g."Name") = lower(@name)
             ORDER BY (g."Name" = @name) DESC
             LIMIT 1
            """;

        const string rosterSql =
            """
            WITH stats AS (
                SELECT sa."CharacterId" AS cid,
                       MAX(sa."Value") FILTER (WHERE sa."DefinitionId" = @levelId) AS lvl,
                       MAX(sa."Value") FILTER (WHERE sa."DefinitionId" = @resetId) AS resets
                  FROM data."StatAttribute" sa
                 WHERE sa."DefinitionId" IN (@levelId, @resetId) AND sa."CharacterId" IS NOT NULL
                 GROUP BY sa."CharacterId"
            )
            SELECT c."Name"                  AS Name,
                   cl."Name"                 AS Class,
                   COALESCE(s.lvl, 0)::int   AS Level,
                   COALESCE(s.resets, 0)::int AS Resets,
                   gm."Status"::int          AS Position
              FROM guild."GuildMember" gm
              JOIN guild."Guild" g                 ON g."Id" = gm."GuildId"
              JOIN data."Character" c              ON c."Id" = gm."Id"
              JOIN data."Account" a                ON a."Id" = c."AccountId"
              LEFT JOIN stats s                    ON s.cid = c."Id"
              LEFT JOIN config."CharacterClass" cl ON cl."Id" = c."CharacterClassId"
             WHERE lower(g."Name") = lower(@name)
               AND a."IsBot" = false
               AND a."IsTemplate" = false
               AND a."State" NOT IN (4, 5)
             ORDER BY CASE gm."Status" WHEN 2 THEN 0 WHEN 4 THEN 1 WHEN 3 THEN 2 ELSE 3 END,
                      COALESCE(s.lvl, 0) DESC, c."Name" ASC
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var guild = await connection.QuerySingleOrDefaultAsync<GuildHeader>(
            new CommandDefinition(guildSql, new { name }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (guild is null)
        {
            return null;
        }

        var roster = await connection.QueryAsync<GuildMemberRow>(new CommandDefinition(
            rosterSql,
            new { name, levelId = StatIds.Level, resetId = StatIds.Resets },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new GuildProfile(guild.Name, guild.Score, guild.Notice, roster.AsList());
    }

    /// <summary>Account and character totals for the home page.</summary>
    public async Task<SiteTotals> GetTotalsAsync(CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT (SELECT count(*) FROM data."Account"
                     WHERE "IsBot" = false AND "IsTemplate" = false AND "State" NOT IN (4, 5))::int AS accounts,
                   (SELECT count(*) FROM data."Character" c
                      JOIN data."Account" a ON a."Id" = c."AccountId"
                     WHERE a."IsBot" = false AND a."IsTemplate" = false AND a."State" NOT IN (4, 5))::int AS characters
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleAsync<SiteTotals>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
