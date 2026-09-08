using Npgsql;

namespace MuSite.Data;

/// <summary>
/// The four connection pools, one per database role. Splitting them is the point: the pool that
/// serves anonymous pages holds credentials that cannot read a password hash or an email address,
/// so a mistake on a ranking page cannot leak either. See website/db/01b-grants.sql.
/// </summary>
public sealed class SiteDataSources : IAsyncDisposable
{
    private SiteDataSources(NpgsqlDataSource gameRead, NpgsqlDataSource gameAuth, NpgsqlDataSource gameReg, NpgsqlDataSource site)
    {
        this.GameRead = gameRead;
        this.GameAuth = gameAuth;
        this.GameReg = gameReg;
        this.Site = site;
    }

    /// <summary>
    /// mu_web_read. Every anonymous page, plus the account-state re-read during session
    /// revalidation - which needs only "State", so it must not consume the small auth pool.
    /// </summary>
    public NpgsqlDataSource GameRead { get; }

    /// <summary>
    /// mu_web_auth. THE ONLY pool that can read data."Account"."PasswordHash". Three call sites:
    /// login, change-password verification, and the password write.
    /// </summary>
    public NpgsqlDataSource GameAuth { get; }

    /// <summary>mu_web_reg. Registration, ban, unban, admin account views. Cannot read a hash.</summary>
    public NpgsqlDataSource GameReg { get; }

    /// <summary>mu_web_app against openmu_web. Not the owner of that database, deliberately.</summary>
    public NpgsqlDataSource Site { get; }

    /// <summary>
    /// Builds the four pools. Throws at startup - not on the first request - when a connection
    /// string is missing, so a misconfigured container fails visibly instead of 500ing later.
    /// </summary>
    public static SiteDataSources Create(IConfiguration configuration)
    {
        return new SiteDataSources(
            Build(configuration, "GameRead", maxPool: 10),
            Build(configuration, "GameAuth", maxPool: 4),
            Build(configuration, "GameReg", maxPool: 4),
            Build(configuration, "Site", maxPool: 6));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.GameRead.DisposeAsync().ConfigureAwait(false);
        await this.GameAuth.DisposeAsync().ConfigureAwait(false);
        await this.GameReg.DisposeAsync().ConfigureAwait(false);
        await this.Site.DisposeAsync().ConfigureAwait(false);
    }

    private static NpgsqlDataSource Build(IConfiguration configuration, string name, int maxPool)
    {
        var connectionString = configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{name} is not configured. Set MUSITE_DB_{name.ToUpperInvariant()}_PW and the "
                + "matching connection string - see website/.env.example.");

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = maxPool,
            ConnectionIdleLifetime = 30,

            // Two processes share the openmu database. Small pools keep `-reinit` possible:
            // DROP DATABASE fails while any other session is connected.
            MinPoolSize = 0,
            ApplicationName = $"mu-site/{name}",
        };

        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }
}
