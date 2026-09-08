using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Data;
using MuSite.Game;
using MuSite.Services;

namespace MuSite.Pages;

public sealed class CharacterModel(RankingCache cache) : PageModel
{
    public CharacterProfile? Profile { get; private set; }

    public string Age { get; private set; } = "just now";

    /// <summary>The stats worth drawing. Leadership only appears for classes that actually use it.</summary>
    public IReadOnlyList<(string Label, int Value)> Stats { get; private set; } = [];

    public int TotalPoints => this.Stats.Sum(s => s.Value);

    /// <summary>The guild rank to show, or null for an ordinary member (and for no guild at all).</summary>
    public string? GuildRank => this.Profile?.GuildPosition is { } position
        && position != GameEnums.GuildPosition.NormalMember
        && position != GameEnums.GuildPosition.Undefined
            ? GameEnums.GuildPosition.Describe(position)
            : null;

    public string Badge => this.Profile is null ? string.Empty : RankingsModel.BadgeFor(
        new RankingRow(this.Profile.Name, this.Profile.Class, this.Profile.Level, this.Profile.MasterLevel,
            this.Profile.Resets, this.Profile.Pk, this.Profile.HeroState, this.Profile.CharStatus, this.Profile.Guild));

    public async Task<IActionResult> OnGetAsync(string name, CancellationToken cancellationToken)
    {
        var result = await cache.GetCharacterAsync(name, cancellationToken).ConfigureAwait(false);
        if (result.Value is null)
        {
            // A character on a banned, bot or template account is deliberately indistinguishable from
            // one that does not exist: the query excludes them, so this is a plain 404.
            return this.NotFound();
        }

        this.Profile = result.Value;
        this.Age = result.Age;

        var stats = new List<(string, int)>
        {
            ("Strength", this.Profile.Strength),
            ("Agility", this.Profile.Agility),
            ("Vitality", this.Profile.Vitality),
            ("Energy", this.Profile.Energy),
        };

        if (this.Profile.Leadership > 0)
        {
            stats.Add(("Command", this.Profile.Leadership));
        }

        this.Stats = stats;
        return this.Page();
    }
}
