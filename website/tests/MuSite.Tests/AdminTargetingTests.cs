using Microsoft.Extensions.Options;
using MuSite;
using MuSite.Auth;
using MuSite.Game;
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
    private const int Banned = 4;
    private const int TemporarilyBanned = 5;

    [Fact]
    public void AnAdminIsNotAllowedToActOnAnotherAdmin()
    {
        var resolver = Build(admins: ["boss", "helper", "other"], owner: "boss");

        // What the page computes: the target's role, then the actor's.
        Assert.Equal(SiteRole.Admin, resolver.ResolveProtection("other"));
        Assert.Equal(SiteRole.Admin, resolver.ResolveProtection("helper"));

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
        Assert.Equal(SiteRole.None, resolver.ResolveProtection("player"));
        Assert.False(IsProtected(target: SiteRole.None, actor: SiteRole.Admin));
    }

    [Theory]
    [InlineData(Normal)]
    [InlineData(GameMaster)]
    [InlineData(Banned)]
    [InlineData(TemporarilyBanned)]
    public void AnAdminStaysProtectedWhateverItsState(int state)
    {
        // A banned administrator has no role while banned - but another administrator must still not
        // be able to lift the owner's ban on them, or replace it with a shorter one.
        var resolver = Build(admins: ["boss", "helper"], owner: "boss");

        Assert.Equal(GameEnums.AccountState.IsGameMaster(state) ? SiteRole.Admin : SiteRole.None, resolver.Resolve("helper", state));
        Assert.Equal(SiteRole.Admin, resolver.ResolveProtection("helper"));
        Assert.True(IsProtected(target: resolver.ResolveProtection("helper"), actor: SiteRole.Admin));
        Assert.False(IsProtected(target: resolver.ResolveProtection("helper"), actor: SiteRole.Owner));
    }

    [Theory]
    [InlineData(Normal)]
    [InlineData(Banned)]
    [InlineData(TemporarilyBanned)]
    public void TheOwnerStaysProtectedWhateverItsState(int state)
    {
        // Not a GameMaster right now, or banned: Resolve gives the owner no role at all, and a
        // protection based on it would let any administrator act on the owner's account.
        var resolver = Build(admins: ["boss", "helper"], owner: "boss");

        Assert.Equal(SiteRole.None, resolver.Resolve("boss", state));
        Assert.Equal(SiteRole.Owner, resolver.ResolveProtection("boss"));
        Assert.True(IsProtected(target: resolver.ResolveProtection("boss"), actor: SiteRole.Admin));
    }

    [Fact]
    public void TheOwnerIsProtectedEvenWhenMissingFromTheAllowlist()
    {
        var resolver = Build(admins: ["helper"], owner: "boss");

        Assert.Equal(SiteRole.Owner, resolver.ResolveProtection("boss"));
    }

    [Fact]
    public void SeededAccountsAreNeverProtected()
    {
        // A seeded name on the allowlist by mistake must not shield an account whose password is public.
        var resolver = Build(admins: ["testgm", "boss"], owner: "boss");

        Assert.Equal(SiteRole.None, resolver.ResolveProtection("testgm"));
    }

    [Theory]
    [InlineData("Boss")]
    [InlineData("boss")]
    [InlineData("BOSS")]
    [InlineData("Helper")]
    [InlineData("hELPER")]
    public void AnAdminOrOwnerNameCannotBeRegistered(string loginName)
    {
        var options = new SiteOptions { Admins = ["Boss", "Helper", string.Empty], Owner = "Boss" };

        Assert.True(GameAccount.IsReservedName(options, loginName));
    }

    [Theory]
    [InlineData("player")]
    [InlineData("bossy")]
    [InlineData("")]
    public void OtherNamesAreNotReserved(string loginName)
    {
        var options = new SiteOptions { Admins = ["Boss", "Helper", string.Empty], Owner = "Boss" };

        Assert.False(GameAccount.IsReservedName(options, loginName));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, "spam")]
    [InlineData(3650, "")]
    public void AValidBanIsAccepted(int? days, string? reason)
    {
        Assert.Null(AdminActions.ValidateBan(days, reason));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    [InlineData(int.MaxValue)]
    public void ABanDurationOutOfRangeIsRefused(int days)
    {
        // 0 or less used to fall through to a PERMANENT ban.
        Assert.NotNull(AdminActions.ValidateBan(days, "reason"));
    }

    [Fact]
    public void ATooLongBanReasonIsRefused()
    {
        Assert.Null(AdminActions.ValidateBan(null, new string('x', 200)));
        Assert.NotNull(AdminActions.ValidateBan(null, new string('x', 201)));
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
        Assert.Equal(SiteRole.Owner, resolver.ResolveProtection("Boss"));
        Assert.Equal(SiteRole.None, resolver.ResolveProtection("boss"));
    }

    [Fact]
    public void ARevokedAdminLosesProtectionImmediately()
    {
        // Removing the name from MUSITE_ADMINS is the documented lever for a rogue administrator.
        var resolver = Build(admins: [], owner: string.Empty);

        Assert.Equal(SiteRole.None, resolver.Resolve("wasadmin", GameMaster));
        Assert.Equal(SiteRole.None, resolver.ResolveProtection("wasadmin"));
    }

    /// <summary>
    /// Mirrors AdminActions.ResolveTargetAsync and AccountModel.IsProtected, where the target's role
    /// is <see cref="RoleResolver.ResolveProtection"/> and the actor's is <see cref="RoleResolver.Resolve"/>.
    /// </summary>
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
