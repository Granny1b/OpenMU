namespace MuSite;

/// <summary>Configuration, bound from MUSITE_* environment variables.</summary>
public sealed class SiteOptions
{
    /// <summary>Server name shown in the header and page titles.</summary>
    public string ServerName { get; set; } = "OpenMU Server";

    /// <summary>
    /// Login names allowed into /admin, on top of the GameMaster state requirement.
    /// EMPTY MEANS ZERO ADMINS - the area 404s for everyone. See <see cref="Auth.RoleResolver"/>.
    /// </summary>
    public string[] Admins { get; set; } = [];

    /// <summary>The single login name that additionally gets <see cref="Auth.SiteRole.Owner"/>.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// Maximum password length accepted at registration. 20 for Season 6
    /// (LoginLongPassword.Password is a 20-byte slice), 10 for 0.75/0.95d clients.
    /// A password longer than the client's field is truncated by the client before it reaches the
    /// server, so BCrypt.Verify then fails forever with no diagnostic. Lowering this later strands
    /// every account registered in the meantime.
    /// </summary>
    public int MaxPassword { get; set; } = 20;

    /// <summary>Minimum password length accepted at registration.</summary>
    public int MinPassword { get; set; } = 8;

    /// <summary>
    /// How many days of the game server's log to keep in server_log. 0 keeps everything, which on
    /// a busy server eventually fills the disk - see <see cref="Services.LogRetentionService"/>.
    /// </summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>
    /// host:port pairs the status probe opens a TCP connection to. Configurable because the deploy
    /// variants publish different ports: all-in-one publishes 44405 and 44406, Traefik only 44405.
    /// </summary>
    public string[] ProbeEndpoints { get; set; } = [];

    /// <summary>CIDRs of the reverse proxy, for X-Forwarded-For to be honoured.</summary>
    public string[] TrustedNetworks { get; set; } = [];

    /// <summary>Where the client download lives, shown on /downloads.</summary>
    public string ClientUrl { get; set; } = string.Empty;

    /// <summary>Connect server host players type into the launcher.</summary>
    public string ConnectHost { get; set; } = string.Empty;

    /// <summary>Optional Discord invite, shown in the footer.</summary>
    public string DiscordUrl { get; set; } = string.Empty;

    /// <summary>Registrations per day before the site closes registration by itself.</summary>
    public int RegistrationDailyCap { get; set; } = 200;
}
