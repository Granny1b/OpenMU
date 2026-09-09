using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Data;
using MuSite.Game;

namespace MuSite.Pages.Admin;

public sealed class MonstersModel(GameCatalog catalog) : PageModel
{
    /// <summary>The search text, or null when nothing was searched.</summary>
    public string? Query { get; private set; }

    /// <summary>The matching monsters.</summary>
    public IReadOnlyList<MonsterRow> Results { get; private set; } = [];

    /// <summary>The monster whose spawn areas are expanded, if any.</summary>
    public MonsterRow? Selected { get; private set; }

    /// <summary>Where the selected monster spawns naturally.</summary>
    public IReadOnlyList<SpawnRow> Spawns { get; private set; } = [];

    /// <summary>The /createmonster command for the selected monster.</summary>
    public string? SpawnCommand { get; private set; }

    /// <summary>Whether /createmonster needs enabling on the panel's Plugins page first.</summary>
    public bool CommandDisabledByDefault
        => GmCommands.Find("/createmonster")?.IsDisabledByDefault ?? false;

    public async Task OnGetAsync(
        [FromQuery] string? q, [FromQuery] int? number, CancellationToken cancellationToken)
    {
        this.Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        // An empty search lists the highest-level monsters, which is the useful default: those are
        // the ones a GM looks up. It is not an unbounded scan - MonstersAsync caps the row count.
        this.Results = await catalog.MonstersAsync(this.Query, 200, cancellationToken).ConfigureAwait(false);

        if (number is not { } monsterNumber)
        {
            return;
        }

        this.Selected = this.Results.FirstOrDefault(m => m.Number == monsterNumber);
        this.Spawns = await catalog.SpawnsAsync(monsterNumber, cancellationToken).ConfigureAwait(false);
        this.SpawnCommand = GmCommands.BuildCreateMonster(monsterNumber, intelligent: false);
    }
}
