using MuSite.Data;
using MuSite.Game;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The GM command catalogue and the builders over it.
///
/// Several of these guard the GENERATOR rather than the runtime code. GmCommands.Generated.cs is
/// produced by website/tools/generate-gm-commands.py from the OpenMU source, and the first two
/// versions of that generator each silently dropped arguments - one truncated a class body with a
/// regex and lost /item's `anc` and `ancBonuslvl`, the other collided every /set* command's nested
/// `Arguments` class onto one dictionary key. A generator that loses data without failing is worse
/// than no generator, so the shapes it must produce are asserted here.
/// </summary>
public sealed class GmCommandsTests
{
    [Fact]
    public void TheCatalogueIsPopulated()
    {
        // A regeneration that produced nothing, or a handful, must fail loudly rather than leave
        // the console quietly empty.
        Assert.True(GmCommands.All.Count >= 60, $"only {GmCommands.All.Count} commands in the catalogue");
        Assert.True(GmCommands.GameMasterCommands.Count >= 40);
        Assert.All(GmCommands.All, c => Assert.StartsWith("/", c.Command, StringComparison.Ordinal));
    }

    [Fact]
    public void ItemCarriesAllNineArgumentsIncludingTheAncientPair()
    {
        // The exact regression: [ValidValues] sits between [Argument] and the property for both
        // ancient arguments, and a parser requiring adjacency drops them.
        var item = GmCommands.Find("/item");

        Assert.NotNull(item);
        Assert.Equal(9, item!.Args.Count);
        Assert.Equal(GmStatus.GameMaster, item.Status);

        var ancient = item.Args.Single(a => a.Name == "Ancient");
        Assert.Equal("anc", ancient.ShortName);
        Assert.Equal(new[] { "0", "1", "2" }, ancient.ValidValues);

        var bonus = item.Args.Single(a => a.Name == "AncientBonusLevel");

        // Case-sensitive on purpose: ReadNamedArgumentsAsync compares with == against the short
        // name, so "ancbonuslvl" would be silently ignored by the server.
        Assert.Equal("ancBonuslvl", bonus.ShortName);
        Assert.Equal(new[] { "1", "2" }, bonus.ValidValues);
    }

    [Fact]
    public void ThePositionalOnlyFamilyStillReportsItsArguments()
    {
        // /setmoney's nested Arguments class carries no [Argument] attributes at all, so it cannot
        // be called by name - but it does take two positional arguments, and reporting none would
        // make the console claim the command takes no input.
        var setMoney = GmCommands.Find("/setmoney");

        Assert.NotNull(setMoney);
        Assert.Equal(2, setMoney!.Args.Count);
        Assert.False(setMoney.SupportsNamedArguments);
        Assert.Equal(new[] { "Amount", "CharacterName" }, setMoney.Args.Select(a => a.Name));
        Assert.True(setMoney.IsDisabledByDefault);
    }

    [Fact]
    public void TheDisabledByDefaultFamilyIsFlagged()
    {
        // These need enabling on the admin panel's Plugins page. Without the flag the console would
        // hand out commands that answer "unknown command" in game.
        var disabled = GmCommands.All.Where(c => c.IsDisabledByDefault).Select(c => c.Command).ToList();

        Assert.Contains("/setmoney", disabled);
        Assert.Contains("/setlevel", disabled);
        Assert.Contains("/clearinv", disabled);
        Assert.DoesNotContain("/item", disabled);
        Assert.DoesNotContain("/createmonster", disabled);
    }

    [Fact]
    public void AJewelIsTwoArgumentsAndNoMore()
    {
        // Jewel of Bless: group 14, number 13. Nothing else applies to a jewel, and emitting
        // `lvl=0 ex=0 opt=0 anc=0` would be noise to retype.
        var built = GmCommands.BuildItem(new ItemRequest(14, 13, 0, 0, false, false, 0, 0, 1));

        Assert.Equal("/item group=14 number=13", built);
    }

    [Fact]
    public void AFullyLoadedWeaponEmitsEveryRequestedOption()
    {
        var built = GmCommands.BuildItem(new ItemRequest(
            Group: 0, Number: 16, Level: 13, ExcellentNumber: 63,
            Skill: true, Luck: true, Opt: 3, Ancient: 2, AncientBonusLevel: 2));

        Assert.Equal(
            "/item group=0 number=16 lvl=13 ex=63 sk=true lu=true opt=3 anc=2 ancBonuslvl=2",
            built);
    }

    [Fact]
    public void TheAncientBonusIsOmittedWhenThereIsNoAncientSet()
    {
        // AncientBonusLevel defaults to 1 server-side and only applies when Ancient > 0.
        var built = GmCommands.BuildItem(new ItemRequest(0, 16, 9, 0, false, false, 0, 0, 2));

        Assert.DoesNotContain("ancBonuslvl", built, StringComparison.Ordinal);
        Assert.Equal("/item group=0 number=16 lvl=9", built);
    }

    [Fact]
    public void ArgumentsAreEmittedInTheServersDeclarationOrder()
    {
        // Named parsing does not care about order, but a GM reading the command does, and the
        // order is the documented usage order.
        var built = GmCommands.BuildItem(new ItemRequest(0, 16, 1, 2, true, true, 3, 1, 2));
        var order = new[] { "group=", "number=", "lvl=", "ex=", "sk=", "lu=", "opt=", "anc=", "ancBonuslvl=" };

        var positions = order.Select(token => built.IndexOf(token, StringComparison.Ordinal)).ToList();

        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p).ToList(), positions);
    }

    [Fact]
    public void CreateMonsterOmitsTheIntelligenceFlagUnlessAsked()
    {
        Assert.Equal("/createmonster number=78", GmCommands.BuildCreateMonster(78, intelligent: false));
        Assert.Equal(
            "/createmonster number=78 intelligence=true",
            GmCommands.BuildCreateMonster(78, intelligent: true));
    }

    [Fact]
    public void MovingAPlayerAlwaysCarriesTheMap()
    {
        // MoveChatCommandPlugIn only warps another character when MapIdOrName is non-empty, so a
        // built command without it would silently be treated as the sender moving themselves.
        var built = GmCommands.BuildMovePlayer("Granny", "Tarkan", null, null);

        Assert.Equal("/move target=Granny mapIdOrName=Tarkan", built);
        Assert.Contains("mapIdOrName=", built, StringComparison.Ordinal);
    }

    [Fact]
    public void MovingAPlayerToCoordinatesIncludesThem()
    {
        var built = GmCommands.BuildMovePlayer("Granny", "8", 120, 80);

        Assert.Equal("/move target=Granny mapIdOrName=8 x=120 y=80", built);
    }

    [Fact]
    public void BlankNamedValuesAreSkippedRatherThanEmittedEmpty()
    {
        var command = GmCommands.Find("/move")!;

        var built = GmCommands.BuildNamed(command, new Dictionary<string, string?>
        {
            ["Target"] = "Granny",
            ["MapIdOrName"] = "   ",
            ["X"] = null,
            ["Y"] = string.Empty,
        });

        Assert.Equal("/move target=Granny", built);
    }

    [Fact]
    public void PositionalBuildingStopsAtTheFirstBlank()
    {
        // The server assigns positional arguments by index, so a hole would shift every later
        // value onto the wrong property - /setmoney "" Granny would set the amount to "Granny".
        var command = GmCommands.Find("/setmoney")!;

        Assert.Equal("/setmoney 1000", GmCommands.BuildPositional(command, new string?[] { "1000", null }));
        Assert.Equal("/setmoney 1000 Granny", GmCommands.BuildPositional(command, new string?[] { "1000", "Granny" }));
        Assert.Equal("/setmoney", GmCommands.BuildPositional(command, new string?[] { null, "Granny" }));
    }

    [Fact]
    public void UsageMarksRequiredAndOptionalArguments()
    {
        Assert.Equal("/teleport <x> <y>", GmCommands.Find("/teleport")!.Usage);
        Assert.Equal(
            "/item <group> <number> [lvl] [ex] [sk] [lu] [opt] [anc] [ancBonuslvl]",
            GmCommands.Find("/item")!.Usage);
    }

    [Theory]
    [InlineData("/ITEM")]
    [InlineData("/Item")]
    public void LookupIsCaseInsensitiveEvenThoughArgumentNamesAreNot(string typed)
    {
        // Convenience for the console's own routing. It does NOT extend to argument short names,
        // which the server compares exactly.
        Assert.NotNull(GmCommands.Find(typed));
    }

    [Fact]
    public void AnUnknownCommandIsNotFound()
    {
        Assert.Null(GmCommands.Find("/definitelynotacommand"));
        Assert.Null(GmCommands.Find(null));
    }

    // ---------------------------------------------------------------------------------------------
    // Validation against the game's own item definitions.
    // ---------------------------------------------------------------------------------------------

    private static ItemRow Definition(
        string name = "Dragon Slayer", int maxLevel = 15, int maxSockets = 5, bool quest = false)
        => new(0, 16, name, maxLevel, maxSockets, 118, 130, 2, 4, 50, quest, true);

    [Fact]
    public void AnItemWithinItsLimitsHasNoProblems()
    {
        var problems = GmCommands.ValidateItem(Definition(), new ItemRequest(0, 16, 15, 63, true, true, 3, 2, 2));

        Assert.Empty(problems);
    }

    [Fact]
    public void ALevelAboveTheItemsMaximumIsRefused()
    {
        var problems = GmCommands.ValidateItem(Definition(maxLevel: 9), new ItemRequest(0, 16, 13, 0, false, false, 0, 0, 1));

        var problem = Assert.Single(problems);
        Assert.Contains("+9", problem, StringComparison.Ordinal);
        Assert.Contains("+13", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDefinitionIsTheOnlyProblemReported()
    {
        // Without a definition every other check would be guesswork, so it short-circuits.
        var problems = GmCommands.ValidateItem(null, new ItemRequest(99, 999, 99, 99, false, false, 9, 9, 9));

        var problem = Assert.Single(problems);
        Assert.Contains("group 99", problem, StringComparison.Ordinal);
        Assert.Contains("number 999", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void AnAncientOutsideItsValidValuesIsRefused(int ancient)
    {
        var problems = GmCommands.ValidateItem(Definition(), new ItemRequest(0, 16, 0, 0, false, false, 0, ancient, 1));

        Assert.Contains(problems, p => p.Contains("Ancient must be", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAncientBonusWithoutAnAncientSetIsRefused()
    {
        var problems = GmCommands.ValidateItem(Definition(), new ItemRequest(0, 16, 0, 0, false, false, 0, 0, 2));

        Assert.Contains(problems, p => p.Contains("only applies", StringComparison.Ordinal));
    }

    [Fact]
    public void ExcellentOptionsBeyondTheBitFieldAreRefused()
    {
        var problems = GmCommands.ValidateItem(Definition(), new ItemRequest(0, 16, 0, 64, false, false, 0, 0, 1));

        Assert.Contains(problems, p => p.Contains("bit field", StringComparison.Ordinal));
    }

    [Fact]
    public void AQuestItemIsFlaggedButNotBlocked()
    {
        var problems = GmCommands.ValidateItem(Definition(quest: true), new ItemRequest(0, 16, 0, 0, false, false, 0, 0, 1));

        Assert.Contains(problems, p => p.Contains("quest item", StringComparison.Ordinal));
    }
}
