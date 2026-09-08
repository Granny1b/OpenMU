using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Services;

namespace MuSite.Pages.Admin;

public sealed class AuditModel(AuditLog audit) : PageModel
{
    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
        => this.Entries = await audit.RecentAsync(200, cancellationToken).ConfigureAwait(false);
}
