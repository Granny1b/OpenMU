using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Data;
using MuSite.Game;

namespace MuSite.Pages.Admin;

public sealed class MonstersModel(GameCatalog catalog) : PageModel
{
    /// <summary>Rows per page.</summary>
    private const int PageSize = 50;

    /// <summary>The search text, or null when nothing was searched.</summary>
    public string? Query { get; private set; }

    /// <summary>The matching monsters on this page.</summary>
    public IReadOnlyList<MonsterRow> Results { get; private set; } = [];

    /// <summary>How many monsters match in total, across every page.</summary>
    public int Total { get; private set; }

    /// <summary>1-based page number. NOT called "Page": that would hide PageModel.Page().</summary>
    public int PageNumber { get; private set; } = 1;

    /// <summary>How many pages the current search fills.</summary>
    public int PageCount => Math.Max(1, (int)Math.Ceiling(this.Total / (double)PageSize));

    /// <summary>The first row of the current page.</summary>
    public int FirstRow => this.Total == 0 ? 0 : ((this.PageNumber - 1) * PageSize) + 1;

    /// <summary>The last row of the current page.</summary>
    public int LastRow => Math.Min(this.PageNumber * PageSize, this.Total);

    /// <summary>A link to another page of the same search, keeping any expanded monster.</summary>
    public string PageLink(int page)
    {
        var link = $"/admin/monsters?page={page}";

        if (!string.IsNullOrEmpty(this.Query))
        {
            link += $"&q={Uri.EscapeDataString(this.Query)}";
        }

        if (this.SelectedNumber is { } number)
        {
            link += $"&number={number}";
        }

        return link;
    }

    /// <summary>The monster number the spawn panel is showing, if any.</summary>
    public int? SelectedNumber { get; private set; }

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
        [FromQuery] string? q, [FromQuery] int? number, [FromQuery] int page,
        CancellationToken cancellationToken)
    {
        this.Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        this.Total = await catalog.MonsterCountAsync(this.Query, cancellationToken).ConfigureAwait(false);
        this.PageNumber = Math.Clamp(page < 1 ? 1 : page, 1, this.PageCount);

        // An empty search lists the highest-level monsters first, which is the useful default:
        // those are the ones a GM looks up.
        this.Results = await catalog
            .MonstersAsync(this.Query, (this.PageNumber - 1) * PageSize, PageSize, cancellationToken)
            .ConfigureAwait(false);

        if (number is not { } monsterNumber)
        {
            return;
        }

        this.SelectedNumber = monsterNumber;

        // Before paging this could read the monster off the listing. It cannot now: the one you
        // asked about is usually not among the 50 rows on screen, and the panel would be headed
        // "Monster - spawn" with no stats. A number search resolves it exactly - MonstersAsync
        // matches CAST("Number" AS text) - so this stays one extra query rather than a new one.
        this.Selected = this.Results.FirstOrDefault(m => m.Number == monsterNumber)
            ?? (await catalog.MonstersAsync(
                    monsterNumber.ToString(CultureInfo.InvariantCulture), 0, 1, cancellationToken)
                .ConfigureAwait(false)).FirstOrDefault();
        this.Spawns = await catalog.SpawnsAsync(monsterNumber, cancellationToken).ConfigureAwait(false);
        this.SpawnCommand = GmCommands.BuildCreateMonster(monsterNumber, intelligent: false);
    }
}
