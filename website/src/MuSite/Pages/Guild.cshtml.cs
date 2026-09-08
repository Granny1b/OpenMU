using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Data;
using MuSite.Services;

namespace MuSite.Pages;

public sealed class GuildModel(RankingCache cache) : PageModel
{
    public GuildProfile? Guild { get; private set; }

    public string Age { get; private set; } = "just now";

    public async Task<IActionResult> OnGetAsync(string name, CancellationToken cancellationToken)
    {
        var result = await cache.GetGuildAsync(name, cancellationToken).ConfigureAwait(false);
        if (result.Value is null)
        {
            return this.NotFound();
        }

        this.Guild = result.Value;
        this.Age = result.Age;
        return this.Page();
    }
}
