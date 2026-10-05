// <copyright file="AuthCookieTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.AdminAuth;

using Microsoft.AspNetCore.Http;
using MUnique.OpenMU.Web.AdminPanel.Auth;

/// <summary>
/// Tests for the secure flag of the authentication cookie of the admin panel.
/// </summary>
[TestFixture]
public class AuthCookieTests
{
    /// <summary>
    /// Tests that the cookie is secure when a TLS terminating proxy forwarded an https request.
    /// </summary>
    /// <param name="forwardedProto">The value of the X-Forwarded-Proto header.</param>
    [TestCase("https")]
    [TestCase("HTTPS")]
    [TestCase("https, http")]
    public void CookieIsSecureBehindATlsTerminatingProxy(string forwardedProto)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;

        Assert.That(CreateBuilder().Build(context).Secure, Is.True);
    }

    /// <summary>
    /// Tests that the cookie is secure for a direct https request.
    /// </summary>
    [Test]
    public void CookieIsSecureForHttps()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";

        Assert.That(CreateBuilder().Build(context).Secure, Is.True);
    }

    /// <summary>
    /// Tests that the cookie still works over plain http, e.g. through an SSH tunnel to http://localhost.
    /// </summary>
    /// <param name="forwardedProto">The value of the X-Forwarded-Proto header, if any.</param>
    [TestCase(null)]
    [TestCase("http")]
    public void CookieIsNotSecureForPlainHttp(string? forwardedProto)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        if (forwardedProto is not null)
        {
            context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        }

        Assert.That(CreateBuilder().Build(context).Secure, Is.False);
    }

    private static ForwardedHttpsCookieBuilder CreateBuilder() => new()
    {
        Name = AdminAuthenticationDefaults.CookieName,
        SecurePolicy = CookieSecurePolicy.SameAsRequest,
    };
}
