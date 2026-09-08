using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Auth;
using MuSite.Game;

namespace MuSite.Pages.Admin;

/// <summary>
/// The account page. Every handler here takes the account id from the ROUTE and passes that id
/// straight through - no handler ever resolves a login name, because the search matches names
/// case-insensitively while the unique index is case-sensitive, and a guard and a write that
/// disagree about which row they mean is an account takeover.
/// </summary>
public sealed class AccountModel(
    AdminActions admin,
    GameAccount accounts,
    RoleResolver roles,
    StepUp stepUp) : PageModel
{
    public AccountDetail? Detail { get; private set; }

    public IReadOnlyList<BanRecord> Bans { get; private set; } = [];

    public SiteRole TargetRole { get; private set; }

    public string? Error { get; private set; }

    public string? NewPassword { get; private set; }

    /// <summary>True when the target is an administrator or the owner and cannot be acted on here.</summary>
    public bool IsProtected => this.TargetRole == SiteRole.Owner
        || (this.TargetRole == SiteRole.Admin && this.User.Role() != SiteRole.Owner);

    /// <summary>Password reset is the owner's alone.</summary>
    public bool CanResetPassword => this.User.Role() == SiteRole.Owner;

    [BindProperty]
    public string? Confirm { get; set; }

    [BindProperty]
    public string? Reason { get; set; }

    [BindProperty]
    public int? Days { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
        => await this.LoadAsync(id, cancellationToken).ConfigureAwait(false) ? this.Page() : this.NotFound();

    public async Task<IActionResult> OnPostBanAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.LoadAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return this.NotFound();
        }

        var actor = this.Actor();
        if (!await stepUp.ConfirmAsync(actor.LoginName, this.Confirm ?? string.Empty, actor.Ip, "ban", cancellationToken).ConfigureAwait(false))
        {
            this.Error = stepUp.IsLockedOut(actor.LoginName)
                ? "Too many wrong confirmations. Wait a few minutes."
                : "That is not your password. The ban was not applied.";
            return this.Page();
        }

        var until = this.Days is { } days and > 0 ? DateTimeOffset.UtcNow.AddDays(days) : (DateTimeOffset?)null;
        var result = await admin.BanAsync(id, until, this.Reason?.Trim() ?? string.Empty, actor, cancellationToken).ConfigureAwait(false);

        return this.AfterAction(id, result, until is null ? "Account banned." : $"Account banned until {until:yyyy-MM-dd HH:mm} UTC.", cancellationToken);
    }

    public async Task<IActionResult> OnPostUnbanAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.LoadAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return this.NotFound();
        }

        var actor = this.Actor();
        if (!await stepUp.ConfirmAsync(actor.LoginName, this.Confirm ?? string.Empty, actor.Ip, "unban", cancellationToken).ConfigureAwait(false))
        {
            this.Error = "That is not your password. The ban was not lifted.";
            return this.Page();
        }

        var result = await admin.UnbanAsync(id, actor, cancellationToken).ConfigureAwait(false);
        return this.AfterAction(id, result, "Ban lifted.", cancellationToken);
    }

    public async Task<IActionResult> OnPostResetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.LoadAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return this.NotFound();
        }

        var actor = this.Actor();
        if (actor.Role != SiteRole.Owner)
        {
            return this.Forbid();
        }

        if (!await stepUp.ConfirmAsync(actor.LoginName, this.Confirm ?? string.Empty, actor.Ip, "password reset", cancellationToken).ConfigureAwait(false))
        {
            this.Error = "That is not your password. The password was not reset.";
            return this.Page();
        }

        var (result, password) = await admin.ResetPasswordAsync(id, actor, cancellationToken).ConfigureAwait(false);
        if (result != AdminActionResult.Done)
        {
            this.Error = Describe(result);
            return this.Page();
        }

        // Shown on this render only - never put in TempData, which would carry it through a redirect
        // in a cookie the browser stores.
        this.NewPassword = password;
        await this.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return this.Page();
    }

    private AdminContext Actor() => new(this.User.LoginName(), this.User.Role(), this.ClientIp());

    private IActionResult AfterAction(Guid id, AdminActionResult result, string success, CancellationToken cancellationToken)
    {
        if (result == AdminActionResult.Done)
        {
            this.TempData["Notice"] = success;
            return this.RedirectToPage(new { id });
        }

        this.Error = Describe(result);
        return this.Page();
    }

    private static string Describe(AdminActionResult result) => result switch
    {
        AdminActionResult.NotFound => "That account no longer exists.",
        AdminActionResult.Protected => "That account is an administrator or the owner and cannot be acted on here.",
        AdminActionResult.NoChange => "Nothing to do - the account is already in that state.",
        AdminActionResult.NotConfirmed => "That is not your password.",
        _ => "The action could not be completed.",
    };

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        this.Detail = await admin.GetAsync(id, accounts, cancellationToken).ConfigureAwait(false);
        if (this.Detail is null)
        {
            return false;
        }

        this.TargetRole = roles.Resolve(this.Detail.LoginName, this.Detail.State);
        this.Bans = await admin.GetBanHistoryAsync(id, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
