using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MuSite.Auth;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Program.cs guards folders with policy NAMES:
///
///     options.Conventions.AuthorizeFolder("/Account", SitePolicies.Player);
///
/// ASP.NET resolves that string when the request arrives, not at startup. A name that was never
/// registered therefore throws
///
///     System.InvalidOperationException: The AuthorizationPolicy named: 'Site.Player' was not found.
///
/// on every request to the guarded folder, while every public page keeps serving normally. That is
/// exactly what shipped: /account and the whole of /admin were dead and the front page was fine, so
/// nothing short of opening a signed-in page could notice.
/// </summary>
public sealed class AuthorizationPolicyTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(SitePolicies.Configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task EveryDeclaredPolicyIsRegistered()
    {
        await using var provider = BuildProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var name in SitePolicies.All)
        {
            // GetPolicyAsync is what AuthorizationPolicy.CombineAsync calls in the middleware, so a
            // null here is the precise condition that produced the exception in production.
            Assert.NotNull(await policies.GetPolicyAsync(name));
        }
    }

    [Theory]
    [InlineData(SiteRole.None, true, false, false)]
    [InlineData(SiteRole.Admin, true, true, false)]
    [InlineData(SiteRole.Owner, true, true, true)]
    public async Task EachRoleReachesExactlyItsOwnAreas(SiteRole role, bool player, bool admin, bool owner)
    {
        await using var provider = BuildProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var principal = SitePrincipal.Build(Guid.NewGuid(), "someone", Guid.NewGuid(), role);

        Assert.Equal(player, (await authorization.AuthorizeAsync(principal, null, SitePolicies.Player)).Succeeded);
        Assert.Equal(admin, (await authorization.AuthorizeAsync(principal, null, SitePolicies.Admin)).Succeeded);
        Assert.Equal(owner, (await authorization.AuthorizeAsync(principal, null, SitePolicies.Owner)).Succeeded);
    }

    [Fact]
    public async Task AnAnonymousVisitorReachesNothingGuarded()
    {
        await using var provider = BuildProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        foreach (var name in SitePolicies.All)
        {
            Assert.False((await authorization.AuthorizeAsync(anonymous, null, name)).Succeeded);
        }
    }

    [Fact]
    public async Task APrincipalCarryingOnlyARoleClaimIsNotEnough()
    {
        // A forged or half-built identity with the right role string but no account id must not
        // reach /admin: every page there acts on an account, and would fail further in.
        await using var provider = BuildProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SitePolicies.RoleClaim, nameof(SiteRole.Owner))],
            "TestScheme"));

        Assert.False((await authorization.AuthorizeAsync(principal, null, SitePolicies.Admin)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(principal, null, SitePolicies.Player)).Succeeded);
    }
}
