using MuSite.Data;
using MuSite.Game;
using MuSite.Pages;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Badges are rendered with Html.Raw, so they must never carry anything that came from the database.
/// These also pin the two enums they read, both of which are easy to get wrong: CharacterStatus is
/// NOT contiguous (GameMaster is 32), and HeroState's PK stages are 5 and 6, not 1 and 2.
/// </summary>
public class BadgeTests
{
    [Fact]
    public void GameMasterWinsOverHeroState()
    {
        var row = Row(heroState: GameEnums.HeroState.Hero, charStatus: GameEnums.CharacterStatus.GameMaster);

        Assert.Contains("mu-badge--gm", RankingsModel.BadgeFor(row), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GameEnums.HeroState.Hero, "mu-badge--hero")]
    [InlineData(GameEnums.HeroState.LightHero, "mu-badge--hero")]
    [InlineData(GameEnums.HeroState.PlayerKiller1stStage, "mu-badge--pk")]
    [InlineData(GameEnums.HeroState.PlayerKiller2ndStage, "mu-badge--pk")]
    public void HeroStatesMapToTheirBadge(int heroState, string expected)
        => Assert.Contains(expected, RankingsModel.BadgeFor(Row(heroState: heroState)), StringComparison.Ordinal);

    [Theory]
    [InlineData(GameEnums.HeroState.New)]
    [InlineData(GameEnums.HeroState.Normal)]
    [InlineData(GameEnums.HeroState.PlayerKillWarning)]
    public void OrdinaryStatesGetNoBadge(int heroState)
        => Assert.Equal(string.Empty, RankingsModel.BadgeFor(Row(heroState: heroState)));

    [Fact]
    public void BadgeMarkupNeverCarriesCharacterData()
    {
        // The name is attacker-controlled; the badge is written with Html.Raw.
        var row = Row(name: "<script>alert(1)</script>", guild: "<img onerror=x>");

        var badge = RankingsModel.BadgeFor(row);

        Assert.DoesNotContain("script", badge, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", badge, StringComparison.OrdinalIgnoreCase);
    }

    private static RankingRow Row(
        string name = "Someone",
        int heroState = GameEnums.HeroState.Normal,
        int charStatus = GameEnums.CharacterStatus.Normal,
        string? guild = null)
        => new(name, "Blade Master", 400, 220, 37, 12, heroState, charStatus, guild);
}
