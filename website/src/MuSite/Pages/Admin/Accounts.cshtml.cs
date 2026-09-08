using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Game;

namespace MuSite.Pages.Admin;

public sealed class AccountsModel(AdminActions admin) : PageModel
{
    public string? Query { get; private set; }

    public IReadOnlyList<AccountSearchRow> Results { get; private set; } = [];

    public async Task OnGetAsync([FromQuery] string? q, CancellationToken cancellationToken)
    {
        this.Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (this.Query is not null)
        {
            this.Results = await admin.SearchAsync(this.Query, 100, cancellationToken).ConfigureAwait(false);
        }
    }
}
