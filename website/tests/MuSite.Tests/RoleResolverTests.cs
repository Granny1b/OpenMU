using Microsoft.Extensions.Options;
using MuSite;
using MuSite.Auth;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The admin gating rule is the highest-consequence branch in the site: it decides who can ban
/// players and reset passwords. Every clause gets a test.
/// </summary>
public class RoleResolverTests
{
    private const int Normal = 0;
    private const int GameMaster = 2;
    private const int GameMasterInvisible = 3;
    private const int Banned = 4;
    private const int TemporarilyBanned = 5;

    [Fact]
    public void EmptyAllowlistMeansZeroAdmins()
    {
        var resolver = Build(admins: [], owner: string.Empty);

        Assert.Equal(SiteRole.None, resolver.Resolve("anybody", GameMaster));
        Assert.Equal(SiteRole.None, resolver.Resolve("anybody", GameMasterInvisible));
    }

    [Fact]
    public void GameMasterStateAloneIsNotEnough()
    {
        // The whole point of the allowlist: an OpenMU panel Viewer can set State = 2 on any account
        // (Accounts.razor carries no Administrator policy), so state alone would be an escalation.
        var resolver = Build(admins: ["realadmin"], owner: "realadmin");

        Assert.Equal(SiteRole.None, resolver.Resolve("sneaky", GameMaster));
        Assert.Equal(SiteRole.Owner, resolver.Resolve("realadmin", GameMaster));
    }

    [Fact]
    public void AllowlistAloneIsNotEnough()
    {
        var resolver = Build(admins: ["listed"], owner: string.Empty);

        Assert.Equal(SiteRole.None, resolver.Resolve("listed", Normal));
        Assert.Equal(SiteRole.Admin, resolver.Resolve("listed", GameMaster));
    }

    [Theory]
    [InlineData(Banned)]
    [InlineData(TemporarilyBanned)]
    public void BannedIsNeverAdmin(int state)
    {
        var resolver = Build(admins: ["listed"], owner: "listed");

        Assert.Equal(SiteRole.None, resolver.Resolve("listed", state));
    }

    [Fact]
    public void EverySeededAccountIsDeniedEvenWhenListed()
    {
        // testgm, testgm2 and testunlock ship with State = GameMaster and password == login name.
        var seeds = new[]
        {
            "test0", "test1", "test2", "test3", "test4", "test5", "test6", "test7", "test8", "test9",
            "test300", "test400", "ancient", "socket", "quest1", "quest2", "quest3",
            "testgm", "testgm2", "testunlock",
        };

        var resolver = Build(admins: seeds, owner: "testgm");

        foreach (var seed in seeds)
        {
            Assert.Equal(SiteRole.None, resolver.Resolve(seed, GameMaster));
        }
    }

    [Fact]
    public void FutureTestAccountsAreDeniedByThePattern()
    {
        var resolver = Build(admins: ["test42"], owner: "test42");

        Assert.Equal(SiteRole.None, resolver.Resolve("test42", GameMaster));
    }

    [Fact]
    public void AllowlistMatchIsCaseSensitive()
    {
        // data."Account"."LoginName" has a case-sensitive unique index, so 'Owner' and 'owner' can be
        // two different accounts. A loose match would hand the entry to whichever registered first.
        var resolver = Build(admins: ["Owner"], owner: "Owner");

        Assert.Equal(SiteRole.Owner, resolver.Resolve("Owner", GameMaster));
        Assert.Equal(SiteRole.None, resolver.Resolve("owner", GameMaster));
        Assert.Equal(SiteRole.None, resolver.Resolve("OWNER", GameMaster));
    }

    [Fact]
    public void BlankAllowlistEntriesNeverMatch()
    {
        // The compose file sets MUSITE_ADMINS__0 and __1 unconditionally, so an unconfigured slot
        // arrives as an empty string.
        var resolver = Build(admins: ["", "  ", "boss"], owner: "boss");

        Assert.Equal(SiteRole.Owner, resolver.Resolve("boss", GameMaster));
        Assert.Equal(SiteRole.None, resolver.Resolve("", GameMaster));
        Assert.Equal(SiteRole.None, resolver.Resolve("  ", GameMaster));
    }

    [Fact]
    public void AdminIsNotOwner()
    {
        var resolver = Build(admins: ["boss", "helper"], owner: "boss");

        Assert.Equal(SiteRole.Owner, resolver.Resolve("boss", GameMaster));
        Assert.Equal(SiteRole.Admin, resolver.Resolve("helper", GameMaster));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingLoginNameIsNone(string? loginName)
    {
        var resolver = Build(admins: ["boss"], owner: "boss");

        Assert.Equal(SiteRole.None, resolver.Resolve(loginName, GameMaster));
    }

    private static RoleResolver Build(string[] admins, string owner)
        => new(new StaticOptions(new SiteOptions { Admins = admins, Owner = owner }));

    private sealed class StaticOptions(SiteOptions value) : IOptionsMonitor<SiteOptions>
    {
        public SiteOptions CurrentValue => value;

        public SiteOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SiteOptions, string?> listener) => null;
    }
}
