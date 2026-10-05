// <copyright file="ForwardedHttpsCookieBuilder.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Auth;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

/// <summary>
/// A cookie builder which marks the cookie as secure when the browser reached a TLS terminating
/// reverse proxy over https, even though the proxy forwarded the request over plain http.
/// </summary>
/// <remarks>
/// With <see cref="CookieSecurePolicy.SameAsRequest"/> alone, the authentication cookie would never
/// be secure behind such a proxy, so the browser would also send it over an unencrypted connection
/// to the same host. <see cref="CookieSecurePolicy.Always"/> isn't an option, because the panel is
/// also used over plain http, e.g. through an SSH tunnel, where some browsers reject secure cookies.
/// The header doesn't need to come from a trusted proxy: it can only make the cookie stricter.
/// </remarks>
internal sealed class ForwardedHttpsCookieBuilder : RequestPathBaseCookieBuilder
{
    private const string ForwardedProtoHeaderName = "X-Forwarded-Proto";

    /// <inheritdoc />
    public override CookieOptions Build(HttpContext context, DateTimeOffset expiresFrom)
    {
        var options = base.Build(context, expiresFrom);
        if (!options.Secure && this.SecurePolicy != CookieSecurePolicy.None && IsForwardedHttps(context.Request))
        {
            options.Secure = true;
        }

        return options;
    }

    private static bool IsForwardedHttps(HttpRequest request)
    {
        foreach (var value in request.Headers[ForwardedProtoHeaderName])
        {
            // The left-most entry is the protocol which the client used.
            var protocol = value?.Split(',', 2)[0].Trim();
            if (string.Equals(protocol, "https", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
