using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Live;

namespace MuSite.Pages.Admin;

/// <summary>
/// "Is the box struggling right now?" - the newest reading of the machine, and nothing else.
///
/// WHY THIS IS A SEPARATE PAGE FROM /admin/metrics
///
/// This one reloads itself every few seconds, so whatever it renders is rendered that often.
/// /admin/metrics draws seven SVG charts from aggregate queries over a window up to seven days
/// wide - about 150 ms of database work per uncached load - and re-running that at 5 second
/// intervals would put more load on the server than it reports on.
///
/// So this page reads NO database and calls NO socket. Everything on it comes from two objects
/// already held in memory: <see cref="HostVitals"/>, refreshed on its own timer, and
/// <see cref="ServerProbe.State"/>, which the probe assigns on the 30 second cycle it already ran.
/// A request here is a dictionary lookup and some string formatting.
///
/// THE GAME FIGURES ARE DELIBERATELY STALE
///
/// They are shown with the age of the reading next to them, because the probe still runs at 30
/// seconds and speeding it up is the one change here that WOULD cost something: each probe opens a
/// connection to the connect server and speaks the client protocol, and the connect server refuses
/// an address holding more than MaxConnectionsPerAddress of them. The machine's vitals are what
/// needs to be live; how many players are online does not change meaningfully in five seconds.
/// </summary>
public sealed class MetricsLiveModel(HostVitals vitals, ServerProbe probe) : PageModel
{
    /// <summary>The newest machine reading.</summary>
    public HostSample Vitals => vitals.Sample;

    /// <summary>What the 30 second probe last saw of the game server.</summary>
    public ProbeState Game => probe.State;

    /// <summary>The interval this page asks the browser to reload on.</summary>
    public int RefreshSeconds => (int)vitals.Interval.TotalSeconds;

    /// <summary>Formats a byte count as whole gibibytes, e.g. "7.6 GiB".</summary>
    /// <param name="bytes">The count, or null.</param>
    /// <returns>A short human readable size, or an em dash.</returns>
    public static string Gibibytes(long? bytes) =>
        bytes is { } value ? $"{value / 1024d / 1024d / 1024d:0.0} GiB" : "—";

    /// <summary>Formats how long ago a reading was taken.</summary>
    /// <param name="at">When it was taken, or null if never.</param>
    /// <returns>A short relative age, e.g. "12s ago".</returns>
    public static string Age(DateTimeOffset? at)
    {
        if (at is not { } taken)
        {
            return "never";
        }

        var elapsed = DateTimeOffset.UtcNow - taken;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalSeconds < 90
            ? $"{elapsed.TotalSeconds:0}s ago"
            : $"{elapsed.TotalMinutes:0}m ago";
    }

    /// <summary>Sets the reload interval the layout renders as a meta refresh.</summary>
    public void OnGet() => this.ViewData["RefreshSeconds"] = this.RefreshSeconds;
}
