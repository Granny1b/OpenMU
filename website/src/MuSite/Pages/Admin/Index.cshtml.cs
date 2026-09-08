using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MuSite.Auth;
using MuSite.Data;
using MuSite.Game;
using MuSite.Services;

namespace MuSite.Pages.Admin;

public sealed class IndexModel(
    AdminActions admin,
    AuditLog audit,
    SiteSettings settings,
    SchemaContract contract,
    IOptions<SiteOptions> options) : PageModel
{
    public int Today { get; private set; }

    public int Week { get; private set; }

    public int ActiveBans { get; private set; }

    public bool RegistrationOpen { get; private set; }

    public bool Maintenance { get; private set; }

    public bool SchemaHealthy => contract.IsHealthy;

    public int DailyCap => options.Value.RegistrationDailyCap;

    public IReadOnlyList<string> SurvivingSeeds { get; private set; } = [];

    public IReadOnlyList<AuditEntry> Recent { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) => await this.LoadAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IActionResult> OnPostAsync(string toggle, CancellationToken cancellationToken)
    {
        // Toggling a switch is not destructive and is fully reversible, so it does not ask for the
        // password again - unlike a ban or a password reset.
        switch (toggle)
        {
            case "registration":
                var open = await settings.GetBoolAsync(SiteSettings.RegistrationOpen, false, cancellationToken).ConfigureAwait(false);
                await settings.SetBoolAsync(SiteSettings.RegistrationOpen, !open, cancellationToken).ConfigureAwait(false);
                await audit.WriteAsync(open ? "registration.closed" : "registration.opened",
                    this.User.LoginName(), null, null, this.ClientIp(), "from the dashboard", cancellationToken).ConfigureAwait(false);
                this.TempData["Notice"] = open ? "Registration is now closed." : "Registration is now open.";
                break;

            case "maintenance":
                var maintenance = await settings.GetBoolAsync(SiteSettings.Maintenance, false, cancellationToken).ConfigureAwait(false);
                await settings.SetBoolAsync(SiteSettings.Maintenance, !maintenance, cancellationToken).ConfigureAwait(false);
                await audit.WriteAsync(maintenance ? "maintenance.hidden" : "maintenance.shown",
                    this.User.LoginName(), null, null, this.ClientIp(), "from the dashboard", cancellationToken).ConfigureAwait(false);
                this.TempData["Notice"] = maintenance ? "Maintenance notice hidden." : "Maintenance notice shown.";
                break;
        }

        return this.RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var counts = await admin.GetDashboardCountsAsync(cancellationToken).ConfigureAwait(false);
        this.Today = counts.Today;
        this.Week = counts.Week;
        this.ActiveBans = counts.ActiveBans;

        this.SurvivingSeeds = await admin.GetSurvivingSeedAccountsAsync(cancellationToken).ConfigureAwait(false);
        this.Recent = await audit.RecentAsync(20, cancellationToken).ConfigureAwait(false);
        this.RegistrationOpen = await settings.GetBoolAsync(SiteSettings.RegistrationOpen, false, cancellationToken).ConfigureAwait(false);
        this.Maintenance = await settings.GetBoolAsync(SiteSettings.Maintenance, false, cancellationToken).ConfigureAwait(false);
    }
}
