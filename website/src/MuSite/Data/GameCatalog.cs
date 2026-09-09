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
///
/// <remarks>
/// SkillNumber is here for one reason: skill 49 is the Dinorant, and it is the single item for
/// which ItemChatCommandPlugIn reads `opt` as a three-bit field instead of an option level. A page
/// that labelled a Dinorant's opt box "level" would be confidently wrong.
/// </remarks>
public sealed record ItemRow(
    int Group, int Number, string Name, int MaximumItemLevel, int MaximumSockets,
    int DropLevel, int? MaximumDropLevel, int Width, int Height, int Durability,
    bool IsQuestItem, bool DropsFromMonsters, int? SkillNumber);

/// <summary>
/// One excellent option an item can carry, and the bit of `ex` that selects it.
///
/// Number is the option's own number within its definition, 1-6; BitValue is 1 &lt;&lt; (Number - 1),
/// which is what ItemChatCommandPlugIn.AddExcellentOptions masks `ex` against. Value/AggregateType
/// describe the effect (AddRaw 0, Multiplicate 1, AddFinal 2, Maximum 3); ScalesWith is set instead
/// for the one option per set whose bonus is a relationship to another stat rather than a constant.
/// </summary>
public sealed record ExcellentOptionRow(
    int Number, int BitValue, string? Attribute, decimal Value, int AggregateType,
    string? ScalesWith, decimal? ScalesWithFactor);

/// <summary>
/// One level of one ordinary (Option-type) option an item can carry.
///
/// For everything but the Dinorant, `opt` IS this level - ItemChatCommandPlugIn.AddOption takes the
/// item's first Option-type option and applies it at Level = opt. Level 1 comes from the option's
/// own boost; the rest are rows in config."ItemOptionOfLevel".
/// </summary>
public sealed record OptionLevelRow(
    Guid AttributeId, string Attribute, int Level, decimal Value, int AggregateType);

/// <summary>
/// One ancient set an item belongs to, and therefore what `anc=N` means for that item.
///
/// Discriminator is the number to pass: for a Dragon set item, 1 is Hyon and 2 is Vicious. The
/// bonus option is applied at `ancBonuslvl`, which is why both level values are here.
/// </summary>
public sealed record AncientSetRow(
    int Discriminator, string SetName, int SetLevel, int MinimumItemCount,
    string? BonusAttribute, decimal? BonusAtLevel1, decimal? BonusAtLevel2, int ItemsInSet);

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
    public async Task<int> MonsterCountAsync(string? search, CancellationToken cancellationToken)
    {
        const string countSql =
            """
            SELECT count(*)::int
              FROM config."MonsterDefinition" m
             WHERE (@search = '' OR m."Designation" ILIKE '%' || @search || '%'
                    OR CAST(m."Number" AS text) = @search)
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            countSql, new { search = search?.Trim() ?? string.Empty }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MonsterRow>> MonstersAsync(
        string? search, int offset, int limit, CancellationToken cancellationToken)
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
             ORDER BY 3 DESC, m."Designation" ASC, m."Number" ASC
             LIMIT @limit OFFSET @offset
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MonsterRow>(new CommandDefinition(sql, new
        {
            search = search?.Trim() ?? string.Empty,
            offset,
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

    /// <summary>How many item definitions a search matches, for paging.</summary>
    public async Task<int> ItemCountAsync(string? search, int? group, CancellationToken cancellationToken)
    {
        // `countSql`, not `sql`: verify-query-mapping.py pairs each `const string sql` with the
        // next Query*Async<T> by position, and a scalar has no record to pair with.
        const string countSql =
            """
            SELECT count(*)::int
              FROM config."ItemDefinition" d
             WHERE (@search = '' OR d."Name" ILIKE '%' || @search || '%')
               AND (@group IS NULL OR d."Group" = @group)
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, new
        {
            search = search?.Trim() ?? string.Empty,
            group,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>One page of item definitions, filtered by name and group.</summary>
    public async Task<IReadOnlyList<ItemRow>> ItemsAsync(
        string? search, int? group, int offset, int limit, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT d."Group"::int       AS "Group",
                   d."Number"::int      AS Number,
                   d."Name"             AS Name,
                   d."MaximumItemLevel"::int AS MaximumItemLevel,
                   d."MaximumSockets"   AS MaximumSockets,
                   d."DropLevel"::int   AS DropLevel,
                   d."MaximumDropLevel"::int AS MaximumDropLevel,
                   d."Width"::int       AS Width,
                   d."Height"::int      AS Height,
                   d."Durability"::int  AS Durability,
                   d."IsQuestItem"      AS IsQuestItem,
                   d."DropsFromMonsters" AS DropsFromMonsters,
                   (SELECT s."Number"::int FROM config."Skill" s
                     WHERE s."Id" = d."SkillId")     AS SkillNumber
              FROM config."ItemDefinition" d
             WHERE (@search = '' OR d."Name" ILIKE '%' || @search || '%')
               AND (@group IS NULL OR d."Group" = @group)
             ORDER BY d."Group", d."Number"
             LIMIT @limit OFFSET @offset
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ItemRow>(new CommandDefinition(sql, new
        {
            search = search?.Trim() ?? string.Empty,
            group,
            offset,
            limit,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>One item definition by its group and number, for validating a built command.</summary>
    public async Task<ItemRow?> ItemAsync(int group, int number, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT d."Group"::int       AS "Group",
                   d."Number"::int      AS Number,
                   d."Name"             AS Name,
                   d."MaximumItemLevel"::int AS MaximumItemLevel,
                   d."MaximumSockets"   AS MaximumSockets,
                   d."DropLevel"::int   AS DropLevel,
                   d."MaximumDropLevel"::int AS MaximumDropLevel,
                   d."Width"::int       AS Width,
                   d."Height"::int      AS Height,
                   d."Durability"::int  AS Durability,
                   d."IsQuestItem"      AS IsQuestItem,
                   d."DropsFromMonsters" AS DropsFromMonsters,
                   (SELECT s."Number"::int FROM config."Skill" s
                     WHERE s."Id" = d."SkillId")     AS SkillNumber
              FROM config."ItemDefinition" d
             WHERE d."Group" = @group AND d."Number" = @number
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ItemRow>(
            new CommandDefinition(sql, new { group, number }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// The excellent options one item can carry, in the order their bits are counted.
    ///
    /// This is what makes the `ex` box mean something. ItemChatCommandPlugIn.AddExcellentOptions
    /// takes the item's OWN excellent options and keeps the ones whose bit is set:
    ///
    ///     .Where(o =&gt; ((1 &lt;&lt; (o.Number - 1)) &amp; arguments.ExcellentNumber) &gt; 0)
    ///
    /// so `ex` is not a value from 0 to 63 that a GM should be asked to work out - it is a sum of
    /// the bits below, and a weapon's option 1 is a different thing from an armour's option 1.
    /// </summary>
    public async Task<IReadOnlyList<ExcellentOptionRow>> ItemExcellentOptionsAsync(
        int group, int number, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT o."Number"::int                       AS Number,
                   (1 << (o."Number" - 1))::int          AS BitValue,
                   a."Designation"                       AS Attribute,
                   COALESCE(v."Value", 0)::real::numeric AS Value,
                   COALESCE(v."AggregateType", 0)::int   AS AggregateType,
                   (SELECT ra."Designation"
                      FROM config."AttributeRelationship" r
                      JOIN config."AttributeDefinition" ra ON ra."Id" = r."InputAttributeId"
                     WHERE r."PowerUpDefinitionValueId" = v."Id"
                     LIMIT 1)                            AS ScalesWith,
                   (SELECT r."InputOperand"::real::numeric
                      FROM config."AttributeRelationship" r
                     WHERE r."PowerUpDefinitionValueId" = v."Id"
                     LIMIT 1)                            AS ScalesWithFactor
              FROM config."ItemDefinition" d
              JOIN config."ItemDefinitionItemOptionDefinition" j ON j."ItemDefinitionId" = d."Id"
              JOIN config."ItemOptionDefinition" od  ON od."Id" = j."ItemOptionDefinitionId"
              JOIN config."IncreasableItemOption" o  ON o."ItemOptionDefinitionId" = od."Id"
              LEFT JOIN config."PowerUpDefinition" p      ON p."Id" = o."PowerUpDefinitionId"
              LEFT JOIN config."PowerUpDefinitionValue" v ON v."Id" = p."BoostId"
              LEFT JOIN config."AttributeDefinition" a    ON a."Id" = p."TargetAttributeId"
             WHERE d."Group" = @group AND d."Number" = @number
               AND o."OptionTypeId" = @excellentType
               AND o."Number" BETWEEN 1 AND 8
             ORDER BY o."Number"
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ExcellentOptionRow>(new CommandDefinition(sql, new
        {
            group,
            number,
            excellentType = StatIds.ExcellentOptionType,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>
    /// Every level of every ordinary option one item can carry, lowest first.
    ///
    /// Level 1 is the option's own boost; levels above it are rows in config."ItemOptionOfLevel",
    /// seeded as level * baseValue - which is where +4/+8/+12/+16 comes from
    /// (GameConfigurationInitializerBase.CreateOptionDefinition). Reading them rather than assuming
    /// the four means the page stays right on a server that configured more.
    /// </summary>
    public async Task<IReadOnlyList<OptionLevelRow>> ItemOptionLevelsAsync(
        int group, int number, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT a."Id"                   AS AttributeId,
                   a."Designation"          AS Attribute,
                   1::int                   AS Level,
                   v."Value"::real::numeric AS Value,
                   v."AggregateType"::int   AS AggregateType
              FROM config."ItemDefinition" d
              JOIN config."ItemDefinitionItemOptionDefinition" j ON j."ItemDefinitionId" = d."Id"
              JOIN config."ItemOptionDefinition" od     ON od."Id" = j."ItemOptionDefinitionId"
              JOIN config."IncreasableItemOption" o     ON o."ItemOptionDefinitionId" = od."Id"
              JOIN config."PowerUpDefinition" p         ON p."Id" = o."PowerUpDefinitionId"
              JOIN config."PowerUpDefinitionValue" v    ON v."Id" = p."BoostId"
              JOIN config."AttributeDefinition" a       ON a."Id" = p."TargetAttributeId"
             WHERE d."Group" = @group AND d."Number" = @number
               AND o."OptionTypeId" = @optionType
            UNION ALL
            SELECT a."Id",
                   a."Designation",
                   l."Level"::int,
                   v."Value"::real::numeric,
                   v."AggregateType"::int
              FROM config."ItemDefinition" d
              JOIN config."ItemDefinitionItemOptionDefinition" j ON j."ItemDefinitionId" = d."Id"
              JOIN config."ItemOptionDefinition" od     ON od."Id" = j."ItemOptionDefinitionId"
              JOIN config."IncreasableItemOption" o     ON o."ItemOptionDefinitionId" = od."Id"
              JOIN config."ItemOptionOfLevel" l         ON l."IncreasableItemOptionId" = o."Id"
              JOIN config."PowerUpDefinition" p         ON p."Id" = l."PowerUpDefinitionId"
              JOIN config."PowerUpDefinitionValue" v    ON v."Id" = p."BoostId"
              JOIN config."AttributeDefinition" a       ON a."Id" = p."TargetAttributeId"
             WHERE d."Group" = @group AND d."Number" = @number
               AND o."OptionTypeId" = @optionType
             ORDER BY 2, 3
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<OptionLevelRow>(new CommandDefinition(sql, new
        {
            group,
            number,
            optionType = StatIds.NormalOptionType,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>
    /// The ancient sets one item belongs to - which is what `anc` actually selects.
    ///
    /// AddAncientBonusOption looks for a set group among the item's PossibleItemSetGroups holding
    /// an entry for this item with AncientSetDiscriminator == anc, so the EXISTS below is not
    /// decoration: a set the item is not linked to is one the command would silently ignore.
    /// </summary>
    public async Task<IReadOnlyList<AncientSetRow>> ItemAncientSetsAsync(
        int group, int number, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT ios."AncientSetDiscriminator"::int AS Discriminator,
                   g."Name"                           AS SetName,
                   g."SetLevel"::int                  AS SetLevel,
                   g."MinimumItemCount"::int          AS MinimumItemCount,
                   ba."Designation"                   AS BonusAttribute,
                   (SELECT bv."Value"::real::numeric
                      FROM config."ItemOptionOfLevel" l
                      JOIN config."PowerUpDefinition" lp      ON lp."Id" = l."PowerUpDefinitionId"
                      JOIN config."PowerUpDefinitionValue" bv ON bv."Id" = lp."BoostId"
                     WHERE l."IncreasableItemOptionId" = ios."BonusOptionId" AND l."Level" = 1
                     LIMIT 1)                         AS BonusAtLevel1,
                   (SELECT bv."Value"::real::numeric
                      FROM config."ItemOptionOfLevel" l
                      JOIN config."PowerUpDefinition" lp      ON lp."Id" = l."PowerUpDefinitionId"
                      JOIN config."PowerUpDefinitionValue" bv ON bv."Id" = lp."BoostId"
                     WHERE l."IncreasableItemOptionId" = ios."BonusOptionId" AND l."Level" = 2
                     LIMIT 1)                         AS BonusAtLevel2,
                   (SELECT count(*)::int FROM config."ItemOfItemSet" m
                     WHERE m."ItemSetGroupId" = g."Id"
                       AND m."AncientSetDiscriminator" = ios."AncientSetDiscriminator")
                                                      AS ItemsInSet
              FROM config."ItemDefinition" d
              JOIN config."ItemOfItemSet" ios ON ios."ItemDefinitionId" = d."Id"
              JOIN config."ItemSetGroup" g    ON g."Id" = ios."ItemSetGroupId"
              LEFT JOIN config."IncreasableItemOption" bo ON bo."Id" = ios."BonusOptionId"
              LEFT JOIN config."PowerUpDefinition" bp     ON bp."Id" = bo."PowerUpDefinitionId"
              LEFT JOIN config."AttributeDefinition" ba   ON ba."Id" = bp."TargetAttributeId"
             WHERE d."Group" = @group AND d."Number" = @number
               AND ios."AncientSetDiscriminator" > 0
               AND EXISTS (SELECT 1 FROM config."ItemDefinitionItemSetGroup" dg
                            WHERE dg."ItemDefinitionId" = d."Id"
                              AND dg."ItemSetGroupId" = g."Id")
             ORDER BY ios."AncientSetDiscriminator"
            """;

        await using var connection = await sources.GameRead.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AncientSetRow>(new CommandDefinition(sql, new
        {
            group,
            number,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }
}
