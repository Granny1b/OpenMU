// <copyright file="LoginReturnUrlTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.AdminAuth;

using MUnique.OpenMU.Web.AdminPanel.Pages;

/// <summary>
/// Tests for the return url of the login page, which must never lead out of the admin panel.
/// </summary>
[TestFixture]
public class LoginReturnUrlTests
{
    /// <summary>
    /// Tests that paths within the admin panel are accepted.
    /// </summary>
    /// <param name="url">The url.</param>
    [TestCase("/")]
    [TestCase("/servers")]
    [TestCase("/edit-config/MUnique.OpenMU.DataModel.Configuration.GameServerDefinition/5b2e3d6e-04c2-4d2c-a0e3-3d6c1c9e0b11")]
    [TestCase("/network-analyzer?filter=a:b")]
    public void LocalPathIsAccepted(string url)
    {
        Assert.That(Login.IsLocalUrl(url), Is.True);
    }

    /// <summary>
    /// Tests that anything which could lead to another site or run a script is refused.
    /// </summary>
    /// <param name="url">The url.</param>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("servers")]
    [TestCase("javascript:alert(1)")]
    [TestCase("JavaScript:alert(document.cookie)")]
    [TestCase("data:text/html,<script>alert(1)</script>")]
    [TestCase("https://evil.example")]
    [TestCase("//evil.example")]
    [TestCase("/\\evil.example")]
    [TestCase("\\\\evil.example")]
    [TestCase("/\t/evil.example")]
    [TestCase("/\r\n/evil.example")]
    [TestCase("/ /evil.example")]
    [TestCase("/foo\\..\\evil")]
    public void ForeignOrScriptUrlIsRefused(string? url)
    {
        Assert.That(Login.IsLocalUrl(url), Is.False);
    }
}
