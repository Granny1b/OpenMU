using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Game;

namespace MuSite.Pages.Admin;

public sealed class CommandsModel : PageModel
{
    /// <summary>The filter text.</summary>
    public string? Query { get; private set; }

    /// <summary>Whether to show only the commands a game master needs.</summary>
    public bool GameMasterOnly { get; private set; }

    /// <summary>The matching commands.</summary>
    public IReadOnlyList<GmCommand> Results { get; private set; } = [];

    /// <summary>How many commands the catalogue holds in total.</summary>
    public int Total => GmCommands.All.Count;

    /// <summary>How many need enabling before they work.</summary>
    public int DisabledCount => GmCommands.All.Count(c => c.IsDisabledByDefault);

    public void OnGet([FromQuery] string? q, [FromQuery] bool gm)
    {
        this.Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        this.GameMasterOnly = gm;

        this.Results = GmCommands.All
            .Where(c => !gm || c.Status == GmStatus.GameMaster)
            .Where(c => this.Query is null
                        || c.Command.Contains(this.Query, StringComparison.OrdinalIgnoreCase)
                        || c.Description.Contains(this.Query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
