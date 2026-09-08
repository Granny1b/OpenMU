using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Auth;

namespace MuSite.Pages;

public sealed class LogoutModel(SessionStore sessions, SessionState state) : PageModel
{
    public void OnGet()
    {
        // A GET that signed you out would let any page on the internet sign you out with an <img>.
        // The button posts, with the antiforgery token the global filter requires.
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (this.User.SessionId() is { } sessionId)
        {
            // Revoke the row, not just the cookie: a copied cookie must stop working too.
            await sessions.RevokeAsync(sessionId, cancellationToken).ConfigureAwait(false);
            state.Evict(sessionId);
        }

        await this.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        return this.LocalRedirect("/");
    }
}
