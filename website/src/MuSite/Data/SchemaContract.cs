using System.Collections.Concurrent;
using Npgsql;

namespace MuSite.Data;

/// <summary>
/// The tripwire for OpenMU schema drift and for lost grants.
///
/// This site hardcodes table names, column names and eight config."AttributeDefinition" primary
/// keys copied out of Stats.cs. Nothing in the C# compiler and nothing in OpenMU's CI will tell us
/// when an upstream migration renames a column - the failure would otherwise surface as a 500 on a
/// player-facing page, or worse, as a ranking that silently lists everybody at level 0.
///
/// It probes ONCE PER ROLE, because the two failure modes are different and both are real:
///   - a renamed column breaks every role at once (upstream drift);
///   - a `-reinit` or panel Setup -> Install drops the database and takes every GRANT with it, so
///     the columns are fine and only the site's roles lose access.
/// </summary>
public sealed class SchemaContract(SiteDataSources sources, ILogger<SchemaContract> logger)
{
    private static readonly (string Schema, string Table, string[] Columns)[] GameTables =
    [
        ("data",   "Account",             ["Id", "State", "IsBot", "IsTemplate"]),
        ("data",   "Character",           ["Id", "Name", "AccountId", "CharacterClassId", "CharacterStatus", "State", "Experience", "MasterExperience", "PlayerKillCount", "CreateDate"]),
        ("data",   "StatAttribute",       ["CharacterId", "DefinitionId", "Value"]),
        ("config", "CharacterClass",      ["Id", "Number", "Name"]),
        ("config", "AttributeDefinition", ["Id", "Designation"]),
        ("guild",  "Guild",               ["Id", "Name", "Score", "Notice"]),
        ("guild",  "GuildMember",         ["Id", "GuildId", "Status"]),
    ];

    private readonly ConcurrentDictionary<string, string[]> _failures = new();

    /// <summary>True when the last check passed for every role.</summary>
    public bool IsHealthy => this._failures.IsEmpty;

    /// <summary>The current failures, keyed by role name. Surfaced on /healthz/schema.</summary>
    public IReadOnlyDictionary<string, string[]> Failures => this._failures;

    /// <summary>
    /// Runs the contract for every role. Returns true when all of them pass.
    /// </summary>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        await this.CheckRoleAsync("mu_web_read", sources.GameRead, checkAttributeIds: true, cancellationToken).ConfigureAwait(false);
        await this.CheckRoleAsync("mu_web_auth", sources.GameAuth, checkAttributeIds: false, cancellationToken).ConfigureAwait(false);
        await this.CheckRoleAsync("mu_web_reg", sources.GameReg, checkAttributeIds: false, cancellationToken).ConfigureAwait(false);
        await this.CheckSiteAsync(cancellationToken).ConfigureAwait(false);

        if (this._failures.IsEmpty)
        {
            logger.LogDebug("Schema contract holds for every role.");
            return true;
        }

        foreach (var (role, problems) in this._failures)
        {
            logger.LogError("Schema contract FAILED for {Role}: {Problems}", role, string.Join("; ", problems));
        }

        return false;
    }

    private static async Task<bool> CanReadAsync(NpgsqlDataSource source, string schema, string table, string column, CancellationToken cancellationToken)
    {
        // has_column_privilege answers the question the site actually cares about - "can THIS role
        // read THIS column" - which information_schema.columns alone does not, because a column can
        // exist while the grant on it is gone.
        await using var command = source.CreateCommand(
            """
            SELECT EXISTS (
                     SELECT 1 FROM information_schema.columns
                      WHERE table_schema = @schema AND table_name = @table AND column_name = @column)
                   AND has_column_privilege(format('%I.%I', @schema, @table), @column, 'SELECT')
            """);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is true;
    }

    private async Task CheckRoleAsync(string role, NpgsqlDataSource source, bool checkAttributeIds, CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        try
        {
            foreach (var (schema, table, columns) in GameTables)
            {
                foreach (var column in columns)
                {
                    // Every role is granted a subset. A column this role was never meant to read is
                    // not a failure - only a column it needs and cannot reach, which each query file
                    // asserts by using it. Here we check reachability of the shared core.
                    if (!await CanReadAsync(source, schema, table, column, cancellationToken).ConfigureAwait(false)
                        && IsRequiredFor(role, schema, table, column))
                    {
                        problems.Add($"{schema}.\"{table}\".\"{column}\" is missing or not readable");
                    }
                }
            }

            if (checkAttributeIds)
            {
                await using var command = source.CreateCommand(
                    """SELECT "Id" FROM config."AttributeDefinition" WHERE "Id" = ANY(@ids)""");
                command.Parameters.AddWithValue("ids", StatIds.All.ToArray());

                var found = new HashSet<Guid>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    found.Add(reader.GetGuid(0));
                }

                foreach (var missing in StatIds.All.Where(id => !found.Contains(id)))
                {
                    problems.Add($"config.\"AttributeDefinition\" has no row {missing} - see Data/StatIds.cs");
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"cannot query as this role: {ex.Message}");
        }

        this.Record(role, problems);
    }

    private async Task CheckSiteAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        try
        {
            await using var command = sources.Site.CreateCommand(
                """
                SELECT count(*) FROM information_schema.tables
                 WHERE table_schema = 'public'
                   AND table_name IN ('web_session','web_ban','news','audit_log','site_setting','schema_version')
                """);

            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (count < 6)
            {
                problems.Add($"openmu_web has {count} of 6 expected tables - run the mu-site-migrate container");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"cannot query openmu_web: {ex.Message}");
        }

        this.Record("mu_web_app", problems);
    }

    private static bool IsRequiredFor(string role, string schema, string table, string column) => role switch
    {
        // The anonymous pages need everything in the core list.
        "mu_web_read" => true,

        // Auth only ever touches data."Account", and only the four columns it is granted.
        "mu_web_auth" => schema == "data" && table == "Account" && column is "Id" or "State",

        // Registration and the admin views read accounts, characters and guild membership.
        "mu_web_reg" => table is "Account" or "Character" or "StatAttribute" or "CharacterClass" or "GuildMember",

        _ => false,
    };

    private void Record(string role, List<string> problems)
    {
        if (problems.Count == 0)
        {
            this._failures.TryRemove(role, out _);
        }
        else
        {
            this._failures[role] = problems.ToArray();
        }
    }
}

/// <summary>Re-runs the schema contract every 60 seconds, so a lost grant is noticed without a restart.</summary>
public sealed class SchemaContractService(SchemaContract contract, ILogger<SchemaContractService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        do
        {
            try
            {
                await contract.CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Schema contract check threw.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
