using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MuSite.Auth;
using MuSite.Game;
using MuSite.Services;

namespace MuSite.Pages;

[EnableRateLimiting(RateLimitPolicies.Login)]
public sealed class LoginModel(
    GameAccount accounts,
    SessionStore sessions,
    RoleResolver roles,
    LoginAttempts attempts,
    AuditLog audit) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public string? Error { get; private set; }

    public string? Notice => this.TempData["Notice"] as string;

    public IActionResult OnGet()
        => this.User.Identity?.IsAuthenticated == true ? this.RedirectToPage("/Account/Index") : this.Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var loginName = this.Input.LoginName?.Trim() ?? string.Empty;
        var password = this.Input.Password ?? string.Empty;

        // Same wording for every failure below: a message that distinguishes "no such account" from
        // "wrong password" turns this form into an account-name oracle.
        const string refused = "That account name and password do not match.";

        if (loginName.Length == 0 || password.Length == 0)
        {
            this.Error = refused;
            return this.Page();
        }

        if (attempts.IsLockedOut(loginName))
        {
            this.Error = "Too many failed attempts for that account. Try again in a few minutes.";
            return this.Page();
        }

        var result = await accounts.LoginAsync(loginName, password, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            attempts.RecordFailure(loginName);
            this.Error = refused;
            return this.Page();
        }

        // Only AFTER the password checked out - saying "you are banned" to anyone who types the name
        // would leak both that the account exists and that it is banned.
        if (GameEnums.AccountState.IsBanned(result.State))
        {
            attempts.RecordSuccess(loginName);
            this.Error = result.State == GameEnums.AccountState.TemporarilyBanned
                ? "This account is temporarily suspended. It will be reinstated automatically."
                : "This account is banned.";
            return this.Page();
        }

        attempts.RecordSuccess(loginName);

        var ip = this.ClientIp();
        var sessionId = await sessions.CreateAsync(
            result.AccountId, result.LoginName, ip, this.Request.Headers.UserAgent.ToString(), cancellationToken)
            .ConfigureAwait(false);

        var role = roles.Resolve(result.LoginName, result.State);

        await this.HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            SitePrincipal.Build(result.AccountId, result.LoginName, sessionId, role),
            new AuthenticationProperties { IsPersistent = true }).ConfigureAwait(false);

        if (role != SiteRole.None)
        {
            // Worth a permanent record: these are the sessions that can ban players.
            await audit.WriteAsync("signin.admin", result.LoginName, result.AccountId, result.LoginName, ip,
                $"role {role}", cancellationToken).ConfigureAwait(false);
        }

        return this.LocalRedirect("/account");
    }

    /// <summary>What the form collects.</summary>
    public sealed class LoginInput
    {
        public string? LoginName { get; set; }

        public string? Password { get; set; }
    }
}
