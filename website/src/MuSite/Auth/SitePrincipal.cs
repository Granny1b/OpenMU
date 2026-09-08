using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MuSite.Auth;

/// <summary>
/// Builds and reads the signed-in identity.
///
/// One place, because sign-in writes these claims and per-request revalidation rewrites them. If a
/// claim were added at sign-in but not here, it would silently disappear the first time a role
/// change caused the principal to be rebuilt.
/// </summary>
public static class SitePrincipal
{
    /// <summary>Builds the cookie identity for a signed-in account.</summary>
    public static ClaimsPrincipal Build(Guid accountId, string loginName, Guid sessionId, SiteRole role)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, loginName),
                new Claim(SitePolicies.AccountIdClaim, accountId.ToString()),
                new Claim(SitePolicies.SessionIdClaim, sessionId.ToString()),
                new Claim(SitePolicies.RoleClaim, role.ToString()),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return new ClaimsPrincipal(identity);
    }

    /// <summary>The signed-in account id, or null when not signed in.</summary>
    public static Guid? AccountId(this ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(SitePolicies.AccountIdClaim)?.Value, out var id) ? id : null;

    /// <summary>The current session id, or null when not signed in.</summary>
    public static Guid? SessionId(this ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(SitePolicies.SessionIdClaim)?.Value, out var id) ? id : null;

    /// <summary>The login name, in its stored capitalisation.</summary>
    public static string LoginName(this ClaimsPrincipal? principal)
        => principal?.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;

    /// <summary>The resolved website role.</summary>
    public static SiteRole Role(this ClaimsPrincipal? principal)
        => Enum.TryParse<SiteRole>(principal?.FindFirst(SitePolicies.RoleClaim)?.Value, out var role)
            ? role
            : SiteRole.None;
}
