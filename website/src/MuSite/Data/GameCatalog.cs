using Dapper;

namespace MuSite.Data;

// -------------------------------------------------------------------------------------------------
// EVERY integer below is `int`, and every integer column is cast to ::int in the SQL. That is not
// laziness about widths - it is the one rule that avoids a whole bug class.
//
// EF stores every C# `byte` property as PostgreSQL `smallint` (there is no one-byte integer), and
// Npgsql reports smallint as System.Int16. Dapper matches a record's constructor against the types
// the reader REPORTS, so a field declared `byte` - which is what the OpenMU entity declares, and
// therefore what looks correct - makes Dapper reject the constructor outright at the first row:
//
//   A parameterless default constructor or one matching signature (...) is required for ...
//
// That exact failure took out the character page and the whole news feed once already. Casting in
// the query and declaring `int` makes the mapping independent of the column's width, now and after
// any future migration widens one.
// -------------------------------------------------------------------------------------------------

/// <summary>A monster, as the GM console lists it.</summary>
public sealed record MonsterRow(
    int Number, string Designation, int Level, int Health,
    int MinDamage, int MaxDamage, int Defense, int ObjectKind, int SpawnAreas);

/// <summary>One place a monster spawns naturally.</summary>
public sealed record SpawnRow(
    string MapName, int MapNumber, int X1, int Y1, int X2, int Y2,
    int Quantity, int SpawnTrigger);

/// <summary>An item definition, as the /item builder needs it.</summary>
public sealed record ItemRow(
    int Group, int Number, string Name, int MaximumItemLevel, int MaximumSockets,
    int DropLevel, int? MaximumDropLevel, int Width, int Height, int Durability,
    bool IsQuestItem, bool DropsFromMonsters);

/// <summary>A game map, for /move.</summary>
public sealed record MapRow(int Number, string Name, double ExpMultiplier, int SpawnAreas);

/// <summary>
/// Read-only reference data out of the `config` schema, for the GM console.
///
/// Nothing here touches player state. That is not a stylistic choice - the site CANNOT safely write
/// game state at all, and the console exists because of it:
///
///   1. A signed-in player's account, characters and inventory live in an EF context created in
///      GameLogic/Player.cs:98 and disposed at :1330 - it spans the whole session. A row written
///      behind its back is untracked, and collides with whatever that session saves.
///   2. LoginServer keeps connected accounts in a plain in-memory Dictionary
///      (src/LoginServer/LoginServer.cs:15). Online state is never persisted, so the site cannot
///      even tell whether a player is online in order to refuse the write.
///   3. There is no API to ask the server to do it. Web/Shared/Services/ChatCommandController only
///      LISTS commands, by reflecting over assemblies loaded in the admin panel's own process.
///
/// So the console reads the catalogue and composes exact commands for a GM to run in-game. Every
/// query below uses the mu_web_read pool: config reference data needs nothing more, and that pool
/// cannot read a login name, an email or a password hash.
/// </summary>
public sealed class GameCatalog(SiteDataSources sources)
{
    /// <summary>
    /// Monsters, filtered by name or number.
    ///
    /// Level, health, damage and defense are rows in config."MonsterAttribute", one per
    /// (monster, attribute) - so they arrive through a pivot rather than as columns. MAX() is used
    /// rather than a bare aggregate for the same reason the ranking boards do: a duplicate row for
    /// one attribute must not turn one monster into two.
    /// </summary>
    public async Task<IReadOnlyList<MonsterRow>> MonstersAsync(
        string? search, int limit, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT m."Number"::int                                                       AS Number,
                   m."Designation"                                                       AS Designation,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @levelId), 0)::int   AS Level,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @healthId), 0)::int  AS Health,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @minDmgId), 0)::int  AS MinDamage,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @maxDmgId), 0)::int  AS MaxDamage,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @defenseId), 0)::int AS Defense,
                   m."ObjectKind"                                                        AS ObjectKind,
                   (SELECT count(*)::int FROM config."MonsterSpawnArea" s
                     WHERE s."MonsterDefinitionId" = m."Id")                             AS SpawnAreas
              FROM config."MonsterDefinition" m
              LEFT JOIN config."MonsterAttribute" a ON a."MonsterDefinitionId" = m."Id"
             WHERE (@search = '' OR m."Designation" ILIKE '%' || @search || '%'
                    OR CAST(m."Number" AS text) = @search)
             GROUP BY m."Id", m."Number", m."Designation", m."ObjectKind"
             ORDER BY 3 DESC, m."Designation" ASC
             LIMIT @limit
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MonsterRow>(new CommandDefinition(sql, new
        {
            search = search?.Trim() ?? string.Empty,
            limit,
            levelId = StatIds.Level,
            healthId = StatIds.MaximumHealth,
            minDmgId = StatIds.MinimumPhysBaseDmg,
            maxDmgId = StatIds.MaximumPhysBaseDmg,
            defenseId = StatIds.DefenseBase,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>Every place one monster number spawns, by map.</summary>
    public async Task<IReadOnlyList<SpawnRow>> SpawnsAsync(int monsterNumber, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT g."Name"     AS MapName,
                   g."Number"::int   AS MapNumber,
                   s."X1"::int       AS X1,
                   s."Y1"::int       AS Y1,
                   s."X2"::int       AS X2,
                   s."Y2"::int       AS Y2,
                   s."Quantity"::int AS Quantity,
                   s."SpawnTrigger" AS SpawnTrigger
              FROM config."MonsterSpawnArea" s
              JOIN config."MonsterDefinition" m ON m."Id" = s."MonsterDefinitionId"
              JOIN config."GameMapDefinition" g ON g."Id" = s."GameMapId"
             WHERE m."Number" = @monsterNumber
             ORDER BY g."Number", s."X1", s."Y1"
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SpawnRow>(
            new CommandDefinition(sql, new { monsterNumber }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>Item definitions, filtered by name, or by "group:number".</summary>
    public async Task<IReadOnlyList<ItemRow>> ItemsAsync(
        string? search, int? group, int limit, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT "Group"            AS "Group",
                   "Number"           AS Number,
                   "Name"             AS Name,
                   "MaximumItemLevel" AS MaximumItemLevel,
                   "MaximumSockets"   AS MaximumSockets,
                   "DropLevel"        AS DropLevel,
                   "MaximumDropLevel" AS MaximumDropLevel,
                   "Width"            AS Width,
                   "Height"           AS Height,
                   "Durability"       AS Durability,
                   "IsQuestItem"      AS IsQuestItem,
                   "DropsFromMonsters" AS DropsFromMonsters
              FROM config."ItemDefinition"
             WHERE (@search = '' OR "Name" ILIKE '%' || @search || '%')
               AND (@group IS NULL OR "Group" = @group)
             ORDER BY "Group", "Number"
             LIMIT @limit
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ItemRow>(new CommandDefinition(sql, new
        {
            search = search?.Trim() ?? string.Empty,
            group,
            limit,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>One item definition by its group and number, for validating a built command.</summary>
    public async Task<ItemRow?> ItemAsync(int group, int number, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT "Group"            AS "Group",
                   "Number"           AS Number,
                   "Name"             AS Name,
                   "MaximumItemLevel" AS MaximumItemLevel,
                   "MaximumSockets"   AS MaximumSockets,
                   "DropLevel"        AS DropLevel,
                   "MaximumDropLevel" AS MaximumDropLevel,
                   "Width"            AS Width,
                   "Height"           AS Height,
                   "Durability"       AS Durability,
                   "IsQuestItem"      AS IsQuestItem,
                   "DropsFromMonsters" AS DropsFromMonsters
              FROM config."ItemDefinition"
             WHERE "Group" = @group AND "Number" = @number
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ItemRow>(
            new CommandDefinition(sql, new { group, number }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Maps, with how many spawn areas each carries.</summary>
    public async Task<IReadOnlyList<MapRow>> MapsAsync(CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT g."Number"::int   AS Number,
                   g."Name"          AS Name,
                   g."ExpMultiplier" AS ExpMultiplier,
                   (SELECT count(*)::int FROM config."MonsterSpawnArea" s
                     WHERE s."GameMapId" = g."Id") AS SpawnAreas
              FROM config."GameMapDefinition" g
             ORDER BY g."Number"
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MapRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>What spawns on one map, highest level first.</summary>
    public async Task<IReadOnlyList<MonsterRow>> MonstersOnMapAsync(int mapNumber, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT m."Number"::int                                                       AS Number,
                   m."Designation"                                                       AS Designation,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @levelId), 0)::int   AS Level,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @healthId), 0)::int  AS Health,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @minDmgId), 0)::int  AS MinDamage,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @maxDmgId), 0)::int  AS MaxDamage,
                   COALESCE(MAX(a."Value") FILTER (WHERE a."AttributeDefinitionId" = @defenseId), 0)::int AS Defense,
                   m."ObjectKind"                                                        AS ObjectKind,
                   count(DISTINCT s."Id")::int                                           AS SpawnAreas
              FROM config."MonsterSpawnArea" s
              JOIN config."GameMapDefinition" g ON g."Id" = s."GameMapId"
              JOIN config."MonsterDefinition" m ON m."Id" = s."MonsterDefinitionId"
              LEFT JOIN config."MonsterAttribute" a ON a."MonsterDefinitionId" = m."Id"
             WHERE g."Number" = @mapNumber
             GROUP BY m."Id", m."Number", m."Designation", m."ObjectKind"
             ORDER BY 3 DESC, m."Designation" ASC
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MonsterRow>(new CommandDefinition(sql, new
        {
            mapNumber,
            levelId = StatIds.Level,
            healthId = StatIds.MaximumHealth,
            minDmgId = StatIds.MinimumPhysBaseDmg,
            maxDmgId = StatIds.MaximumPhysBaseDmg,
            defenseId = StatIds.DefenseBase,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }
}
