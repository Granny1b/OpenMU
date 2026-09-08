using Microsoft.AspNetCore.Authorization;

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

    /// <summary>Every policy name this class declares. AuthorizationPolicyTests asserts each one
    /// is registered by <see cref="Configure"/> - a name declared here but never registered throws
    /// "The AuthorizationPolicy named: 'X' was not found" on first request, and only on the pages
    /// that use it.</summary>
    public static IReadOnlyList<string> All => [Player, Admin, Owner];

    /// <summary>
    /// Registers the policies. Called as <c>services.AddAuthorization(SitePolicies.Configure)</c>.
    ///
    /// These MUST exist: options.Conventions.AuthorizeFolder in Program.cs names them by string, and
    /// ASP.NET resolves that name at request time, not at startup. Without the registration the
    /// whole of /account and /admin throws InvalidOperationException on every request while every
    /// public page keeps working - so nothing in a smoke test of the front page notices.
    /// </summary>
    public static void Configure(AuthorizationOptions options)
    {
        // Any signed-in account, including SiteRole.None - an ordinary player owns /account.
        // The account id claim is required too: a principal without one cannot be acted on by any
        // page in that folder, and would fail further in with a less obvious error.
        options.AddPolicy(Player, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(AccountIdClaim));

        // Admin or Owner. The claim carries SiteRole.ToString(), written in SitePrincipal.Build and
        // rewritten on every request by OnValidatePrincipal, so a demotion takes effect at once.
        options.AddPolicy(Admin, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(AccountIdClaim)
            .RequireClaim(RoleClaim, nameof(SiteRole.Admin), nameof(SiteRole.Owner)));

        options.AddPolicy(Owner, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(AccountIdClaim)
            .RequireClaim(RoleClaim, nameof(SiteRole.Owner)));
    }
}
