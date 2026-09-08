using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Auth;
using MuSite.Game;

namespace MuSite.Pages.Account;

public sealed class IndexModel(GameAccount accounts, SessionStore sessions) : PageModel
{
    public AccountOverview? Overview { get; private set; }

    public IReadOnlyList<OwnCharacter> Characters { get; private set; } = [];

    public int OtherSessions { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (this.User.AccountId() is not { } accountId)
        {
            return this.RedirectToPage("/Login");
        }

        this.Overview = await accounts.GetOverviewAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (this.Overview is null)
        {
            // The session outlived the account. Revalidation will reject it on the next request.
            return this.RedirectToPage("/Login");
        }

        this.Characters = await accounts.GetOwnCharactersAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (this.User.SessionId() is { } sessionId)
        {
            this.OtherSessions = await sessions.CountOtherAsync(accountId, sessionId, cancellationToken).ConfigureAwait(false);
        }

        return this.Page();
    }
}
