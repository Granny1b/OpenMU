using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MuSite.Auth;

/// <summary>Reads the caller's address for audit rows and session records.</summary>
public static class ClientIpExtensions
{
    /// <summary>
    /// The client address, or null when it cannot be determined.
    ///
    /// This is only the real client address if the proxy forwards it AND
    /// ForwardedHeadersOptions.ForwardedHeaders is set explicitly - see Program.cs and the
    /// proxy_set_header block in deploy/all-in-one/nginx/*.conf. Otherwise every audit row records
    /// the proxy container's address, which is worse than useless: it looks like evidence.
    /// </summary>
    public static string? ClientIp(this PageModel page)
        => page.HttpContext.Connection.RemoteIpAddress?.ToString();
}
