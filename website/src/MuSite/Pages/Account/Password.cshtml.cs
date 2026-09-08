using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MuSite.Auth;
using MuSite.Game;
using MuSite.Services;

namespace MuSite.Pages.Account;

public sealed class PasswordModel(
    GameAccount accounts,
    SessionStore sessions,
    AuditLog audit,
    IOptions<SiteOptions> options) : PageModel
{
    [BindProperty]
    public PasswordInput Input { get; set; } = new();

    public string? Error { get; private set; }

    public int MinPassword => options.Value.MinPassword;

    public int MaxPassword => options.Value.MaxPassword;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (this.User.AccountId() is not { } accountId)
        {
            return this.RedirectToPage("/Login");
        }

        var current = this.Input.Current ?? string.Empty;
        var replacement = this.Input.New ?? string.Empty;

        if (replacement != this.Input.Confirm)
        {
            this.Error = "The two new passwords do not match.";
            return this.Page();
        }

        if (replacement.Length < this.MinPassword || replacement.Length > this.MaxPassword)
        {
            this.Error = $"The new password must be between {this.MinPassword} and {this.MaxPassword} characters. "
                       + "That upper limit is what the game client can send.";
            return this.Page();
        }

        if (!await accounts.ChangePasswordAsync(accountId, current, replacement, cancellationToken).ConfigureAwait(false))
        {
            this.Error = "That is not your current password.";
            return this.Page();
        }

        // Sign out every OTHER session. The usual reason to change a password is that someone else
        // might know it, and leaving their session alive defeats the exercise.
        var currentSession = this.User.SessionId();
        var revoked = await sessions.RevokeAllAsync(accountId, currentSession, cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync("password.changed", this.User.LoginName(), accountId, this.User.LoginName(),
            this.ClientIp(), $"{revoked} other session(s) signed out", cancellationToken).ConfigureAwait(false);

        this.TempData["Notice"] = revoked > 0
            ? $"Password changed. {revoked} other session(s) were signed out."
            : "Password changed.";

        return this.RedirectToPage("/Account/Index");
    }

    /// <summary>What the form collects.</summary>
    public sealed class PasswordInput
    {
        public string? Current { get; set; }

        public string? New { get; set; }

        public string? Confirm { get; set; }
    }
}
