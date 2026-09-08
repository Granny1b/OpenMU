using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MuSite.Auth;
using MuSite.Game;
using MuSite.Services;

namespace MuSite.Pages;

[EnableRateLimiting(RateLimitPolicies.Register)]
public sealed class RegisterModel(
    GameAccount accounts,
    RegistrationThrottle throttle,
    IOptions<SiteOptions> options) : PageModel
{
    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public string? Error { get; private set; }

    public int MinPassword => options.Value.MinPassword;

    public int MaxPassword => options.Value.MaxPassword;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
        => await throttle.IsOpenAsync(cancellationToken).ConfigureAwait(false)
            ? this.Page()
            : this.NotFound();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await throttle.IsOpenAsync(cancellationToken).ConfigureAwait(false))
        {
            return this.NotFound();
        }

        var loginName = this.Input.LoginName?.Trim() ?? string.Empty;
        var password = this.Input.Password ?? string.Empty;

        if (password != this.Input.Confirm)
        {
            this.Error = "The two passwords do not match.";
            return this.Page();
        }

        var result = await accounts.RegisterAsync(loginName, password, this.Input.Email?.Trim(), cancellationToken)
            .ConfigureAwait(false);

        switch (result)
        {
            case RegisterResult.Created:
                await throttle.NoteRegistrationAsync(loginName, this.ClientIp(), cancellationToken).ConfigureAwait(false);
                this.TempData["Notice"] = "Your account is ready. Sign in below, then start the game client.";
                return this.RedirectToPage("/Login");

            case RegisterResult.InvalidName:
                this.Error = "Account names are 3 to 10 letters or digits, and that one is not available.";
                break;

            case RegisterResult.InvalidPassword:
                this.Error = $"The password must be between {this.MinPassword} and {this.MaxPassword} characters.";
                break;

            case RegisterResult.NameTaken:
                // Deliberately the same wording as InvalidName: a distinct "that name is taken"
                // turns this form into a way to test which accounts exist.
                this.Error = "Account names are 3 to 10 letters or digits, and that one is not available.";
                break;

            default:
                this.Error = "Registration is closed at the moment.";
                break;
        }

        this.Input.Password = null;
        this.Input.Confirm = null;
        return this.Page();
    }

    /// <summary>What the form collects.</summary>
    public sealed class RegisterInput
    {
        public string? LoginName { get; set; }

        public string? Password { get; set; }

        public string? Confirm { get; set; }

        public string? Email { get; set; }
    }
}
