namespace MuSite.Auth;

/// <summary>Authorization policy names, and the claim the resolved role is carried in.</summary>
public static class SitePolicies
{
    /// <summary>The claim type holding the <see cref="SiteRole"/> name.</summary>
    public const string RoleClaim = "musite:role";

    /// <summary>The claim type holding the account's uuid.</summary>
    public const string AccountIdClaim = "musite:account";

    /// <summary>The claim type holding the server-side session id.</summary>
    public const string SessionIdClaim = "musite:sid";

    /// <summary>Signed in as any player.</summary>
    public const string Player = "Site.Player";

    /// <summary>Admin or Owner. Guards the whole /admin area.</summary>
    public const string Admin = "Site.Admin";

    /// <summary>Owner only. Guards password reset.</summary>
    public const string Owner = "Site.Owner";
}
