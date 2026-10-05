using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MuSite.Auth;
using MuSite.Game;
using MuSite.Services;

namespace MuSite.Pages.Account;

/// <summary>
/// Changes the password of the signed-in account.
///
/// Throttled twice, like the sign-in: per client address by the rate limiter, and per account by
/// <see cref="LoginAttempts"/>. The form verifies the current password, so without both it is a
/// password oracle for anyone holding a stolen session cookie. The per-account counter is the
/// sign-in's own, so guessing here and guessing at /login draw on the same budget.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.PasswordChange)]
public sealed class PasswordModel(
    GameAccount accounts,
    SessionStore sessions,
    SessionState sessionState,
    LoginAttempts attempts,
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

        var loginName = this.User.LoginName();
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

        if (attempts.IsLockedOut(loginName))
        {
            this.Error = "Too many wrong passwords for this account. Try again in a few minutes.";
            return this.Page();
        }

        if (!await accounts.ChangePasswordAsync(accountId, current, replacement, cancellationToken).ConfigureAwait(false))
        {
            attempts.RecordFailure(loginName);
            this.Error = "That is not your current password.";
            return this.Page();
        }

        attempts.RecordSuccess(loginName);

        // Sign out every OTHER session. The usual reason to change a password is that someone else
        // might know it, and leaving their session alive defeats the exercise. Evicting them from the
        // revalidation cache is what makes that take effect on their next request rather than within
        // a minute.
        var currentSession = this.User.SessionId();
        var revoked = await sessions.RevokeAllAsync(accountId, currentSession, cancellationToken).ConfigureAwait(false);
        foreach (var sessionId in revoked)
        {
            sessionState.Evict(sessionId);
        }

        await audit.WriteAsync("password.changed", loginName, accountId, loginName,
            this.ClientIp(), $"{revoked.Count} other session(s) signed out", cancellationToken).ConfigureAwait(false);

        this.TempData["Notice"] = revoked.Count > 0
            ? $"Password changed. {revoked.Count} other session(s) were signed out."
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
