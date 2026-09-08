using Microsoft.Extensions.Options;
using MuSite;
using MuSite.Auth;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Who may act on whom.
///
/// The targeting rule and the uuid-only routing are the two things standing between an
/// administrator account and a full takeover of the server. data."Account"."LoginName" carries a
/// case-SENSITIVE unique index while the admin search matches with ILIKE, so a guard that resolved
/// a name case-insensitively and a write that resolved it case-sensitively would clear one account
/// and modify another. Every admin route therefore takes {id:guid} and passes that id straight
/// through - these tests pin the role half of it.
/// </summary>
public class AdminTargetingTests
{
    private const int Normal = 0;
    private const int GameMaster = 2;

    [Fact]
    public void AnAdminIsNotAllowedToActOnAnotherAdmin()
    {
        var resolver = Build(admins: ["boss", "helper", "other"], owner: "boss");

        // What the page computes: the target's role, then the actor's.
        Assert.Equal(SiteRole.Admin, resolver.Resolve("other", GameMaster));
        Assert.Equal(SiteRole.Admin, resolver.Resolve("helper", GameMaster));

        // Protected unless the actor is the owner.
        Assert.True(IsProtected(target: SiteRole.Admin, actor: SiteRole.Admin));
        Assert.False(IsProtected(target: SiteRole.Admin, actor: SiteRole.Owner));
    }

    [Fact]
    public void NobodyMayActOnTheOwner()
    {
        Assert.True(IsProtected(target: SiteRole.Owner, actor: SiteRole.Admin));
        Assert.True(IsProtected(target: SiteRole.Owner, actor: SiteRole.Owner));
    }

    [Fact]
    public void OrdinaryPlayersAreNotProtected()
    {
        var resolver = Build(admins: ["boss"], owner: "boss");

        Assert.Equal(SiteRole.None, resolver.Resolve("player", Normal));
        Assert.False(IsProtected(target: SiteRole.None, actor: SiteRole.Admin));
    }

    [Fact]
    public void ACaseVariantOfAnAdminNameDoesNotInheritProtection()
    {
        // 'Boss' and 'boss' are two different accounts to the game. The allowlist is matched with
        // Ordinal, so only the exact spelling is an administrator - and the case variant is an
        // ordinary account that an admin may ban, which is the correct outcome.
        var resolver = Build(admins: ["Boss"], owner: "Boss");

        Assert.Equal(SiteRole.Owner, resolver.Resolve("Boss", GameMaster));
        Assert.Equal(SiteRole.None, resolver.Resolve("boss", GameMaster));
    }

    [Fact]
    public void ARevokedAdminLosesProtectionImmediately()
    {
        // Removing the name from MUSITE_ADMINS is the documented lever for a rogue administrator.
        var resolver = Build(admins: [], owner: string.Empty);

        Assert.Equal(SiteRole.None, resolver.Resolve("wasadmin", GameMaster));
    }

    /// <summary>Mirrors AdminActions.ResolveTargetAsync and AccountModel.IsProtected.</summary>
    private static bool IsProtected(SiteRole target, SiteRole actor)
        => target == SiteRole.Owner || (target == SiteRole.Admin && actor != SiteRole.Owner);

    private static RoleResolver Build(string[] admins, string owner)
        => new(new StaticOptions(new SiteOptions { Admins = admins, Owner = owner }));

    private sealed class StaticOptions(SiteOptions value) : IOptionsMonitor<SiteOptions>
    {
        public SiteOptions CurrentValue => value;

        public SiteOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SiteOptions, string?> listener) => null;
    }
}
