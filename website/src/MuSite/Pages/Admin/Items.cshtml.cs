using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Data;
using MuSite.Game;

namespace MuSite.Pages.Admin;

public sealed class ItemsModel(GameCatalog catalog) : PageModel
{
    /// <summary>The search text.</summary>
    public string? Query { get; private set; }

    /// <summary>The item group filter, when one is applied.</summary>
    public int? Group { get; private set; }

    /// <summary>The matching item definitions.</summary>
    public IReadOnlyList<ItemRow> Results { get; private set; } = [];

    /// <summary>The item the builder is configured for.</summary>
    public ItemRow? Selected { get; private set; }

    /// <summary>What the builder was asked for.</summary>
    public ItemRequest? Request { get; private set; }

    /// <summary>The built command, when the request is valid.</summary>
    public string? Command { get; private set; }

    /// <summary>Anything wrong with the request, checked against the item's own limits.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>Whether /item needs enabling on the panel's Plugins page first.</summary>
    public bool CommandDisabledByDefault => GmCommands.Find("/item")?.IsDisabledByDefault ?? false;

    public async Task OnGetAsync(
        [FromQuery] string? q,
        [FromQuery] int? group,
        [FromQuery] int? number,
        [FromQuery] int lvl,
        [FromQuery] int ex,
        [FromQuery] bool sk,
        [FromQuery] bool lu,
        [FromQuery] int opt,
        [FromQuery] int anc,
        [FromQuery] int ancLvl,
        CancellationToken cancellationToken)
    {
        this.Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        this.Group = group;
        this.Results = await catalog.ItemsAsync(this.Query, group, 300, cancellationToken).ConfigureAwait(false);

        if (group is not { } selectedGroup || number is not { } selectedNumber)
        {
            return;
        }

        this.Selected = await catalog.ItemAsync(selectedGroup, selectedNumber, cancellationToken).ConfigureAwait(false);

        // AncientBonusLevel is 1 server-side when unset, so an unfilled form must not read as 0 -
        // that would trip the "only applies when Ancient is 1 or 2" check on every plain item.
        this.Request = new ItemRequest(
            selectedGroup, selectedNumber, lvl, ex, sk, lu, opt, anc, ancLvl <= 0 ? 1 : ancLvl);

        this.Problems = GmCommands.ValidateItem(this.Selected, this.Request);

        // A command is only offered when it would work. Handing over one that the server will
        // reject is worse than handing over nothing, because the failure happens in game.
        if (this.Problems.Count == 0)
        {
            this.Command = GmCommands.BuildItem(this.Request);
        }
    }
}
