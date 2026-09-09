using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Live;
using MuSite.Services;

namespace MuSite.Pages.Admin;

/// <summary>
/// Charts what the VPS and the game server have been doing.
///
/// TWO COLLECTORS FEED THIS PAGE
///
///   * Vector reads the kernel every thirty seconds and writes host_metric - cpu, memory, disk,
///     load and network for the whole machine.
///   * ServerProbe asks the connect server for its server list on the same interval and writes
///     server_sample - up or down, load percentage, and the exact player count when an API key is
///     configured.
///
/// Both intervals are thirty seconds ON PURPOSE, so the game series and the machine series land on
/// the same buckets and "the server filled up at eight" can be read against "the box ran out of
/// memory at eight" on one screen.
/// </summary>
public sealed class MetricsModel(ServerMetrics metrics, ServerProbe probe) : PageModel
{
    /// <summary>
    /// Roughly how many points to draw. More than this and the buckets are narrower than the pixels
    /// they are drawn into; fewer and a spike is averaged out of existence.
    /// </summary>
    private const int TargetPoints = 120;

    /// <summary>The windows offered.</summary>
    public static readonly (int Hours, string Label)[] Windows =
    [
        (1, "Last hour"),
        (6, "Last 6 hours"),
        (24, "Last 24 hours"),
        (24 * 7, "Last 7 days"),
    ];

    /// <summary>The window being looked at, in hours.</summary>
    public int Hours { get; private set; } = 24;

    /// <summary>Seconds per point, derived from the window.</summary>
    public int Bucket { get; private set; } = 30;

    /// <summary>Left edge of every chart.</summary>
    public DateTimeOffset From { get; private set; }

    /// <summary>Right edge of every chart.</summary>
    public DateTimeOffset To { get; private set; }

    /// <summary>True when collection has not been deployed yet.</summary>
    public bool TablesMissing { get; private set; }

    /// <summary>What the probe saw most recently, straight from memory.</summary>
    public ProbeState Live => probe.State;

    /// <summary>Players online over time.</summary>
    public IReadOnlyList<Point> Players { get; private set; } = [];

    /// <summary>Share of samples in each bucket where the server answered.</summary>
    public IReadOnlyList<Point> Availability { get; private set; } = [];

    /// <summary>Processor busy percentage.</summary>
    public IReadOnlyList<Point> Cpu { get; private set; } = [];

    /// <summary>Memory in use, as a percentage of the machine's total.</summary>
    public IReadOnlyList<Point> Memory { get; private set; } = [];

    /// <summary>One minute load average.</summary>
    public IReadOnlyList<Point> Load { get; private set; } = [];

    /// <summary>Bytes per second received.</summary>
    public IReadOnlyList<Point> NetworkIn { get; private set; } = [];

    /// <summary>Bytes per second sent.</summary>
    public IReadOnlyList<Point> NetworkOut { get; private set; } = [];

    /// <summary>Used percentage of the largest filesystem.</summary>
    public IReadOnlyList<Point> Disk { get; private set; } = [];

    /// <summary>Errors logged per bucket.</summary>
    public IReadOnlyList<Point> Errors { get; private set; } = [];

    /// <summary>Warnings logged per bucket.</summary>
    public IReadOnlyList<Point> Warnings { get; private set; } = [];

    /// <summary>Every filesystem, with how full it is right now.</summary>
    public IReadOnlyList<DiskNow> Disks { get; private set; } = [];

    /// <summary>Total memory of the machine in bytes, 0 when unknown.</summary>
    public double MemoryTotal { get; private set; }

    /// <summary>Cores the machine reports, 0 when unknown.</summary>
    public double Cores { get; private set; }

    /// <summary>True when at least one measurement exists.</summary>
    public bool HasHostData => this.Cpu.Count > 0 || this.Memory.Count > 0 || this.Disks.Count > 0;

    /// <summary>How full one filesystem is.</summary>
    /// <param name="Device">The block device.</param>
    /// <param name="Used">Bytes in use.</param>
    /// <param name="Total">Bytes in total.</param>
    public sealed record DiskNow(string Device, double Used, double Total);

    /// <summary>Loads every series for the chosen window.</summary>
    /// <param name="hours">Requested window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task OnGetAsync(int? hours, CancellationToken cancellationToken)
    {
        // An unrecognised window falls back rather than being honoured: ?hours=100000 would scan
        // every row in the largest table in the database, from an unauthenticated-looking URL.
        this.Hours = Array.Exists(Windows, window => window.Hours == hours) ? hours!.Value : 24;

        // Wide enough that a point is never narrower than a pixel, and never below the collection
        // interval - a bucket smaller than the scrape rate produces empty buckets between real ones,
        // which the chart would then draw as a row of gaps.
        this.Bucket = Math.Max(30, this.Hours * 3600 / TargetPoints);

        this.To = DateTimeOffset.UtcNow;
        this.From = this.To.AddHours(-this.Hours);

        // DELIBERATELY ONE AFTER ANOTHER, not Task.WhenAll. Running all eleven together would cut
        // the page's latency to the slowest of them, at the cost of eleven simultaneous connections
        // each running an aggregate - a burst of exactly the kind a small VPS feels. Sequentially
        // this is about 150 ms of database work for a seven day window, and ServerMetrics caches
        // each series for 25 seconds, so it happens at most once per collection interval no matter
        // how many people are looking or how often they reload.
        this.Players = await metrics.PlayersAsync(this.Hours, this.Bucket, cancellationToken);
        this.Availability = await metrics.AvailabilityAsync(this.Hours, this.Bucket, cancellationToken);
        this.Cpu = await metrics.CpuBusyAsync(this.Hours, this.Bucket, cancellationToken);
        this.Load = await metrics.GaugeAsync("load1", this.Hours, this.Bucket, cancellationToken);

        this.NetworkIn = await metrics.RateAsync("network_receive_bytes_total", this.Hours, this.Bucket, cancellationToken);
        this.NetworkOut = await metrics.RateAsync("network_transmit_bytes_total", this.Hours, this.Bucket, cancellationToken);

        await this.LoadMemoryAsync(cancellationToken);
        await this.LoadDisksAsync(cancellationToken);
        await this.LoadLogRatesAsync(cancellationToken);

        var cores = await metrics.LatestByScopeAsync("logical_cpus", cancellationToken);
        this.Cores = cores.Count > 0 ? cores[0].Value : 0;

        this.TablesMissing = metrics.TablesMissing;
    }

    /// <summary>The most recent value of a series, or null when it is empty.</summary>
    /// <param name="series">The series.</param>
    /// <returns>The last value.</returns>
    public static double? Now(IReadOnlyList<Point> series) =>
        series.Count == 0 ? null : series[^1].Value;

    /// <summary>Formats a byte count the way a person would say it.</summary>
    /// <param name="bytes">The count.</param>
    /// <returns>A short human-readable size.</returns>
    public static string Bytes(double bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB", "PB"];
        var unit = 0;
        while (bytes >= 1000 && unit < units.Length - 1)
        {
            bytes /= 1000;
            unit++;
        }

        return $"{bytes:0.#} {units[unit]}";
    }

    /// <summary>The link to another window, keeping nothing else.</summary>
    /// <param name="hours">The window.</param>
    /// <returns>A relative URL.</returns>
    public string Link(int hours) => $"/admin/metrics?hours={hours}";

    /// <summary>Memory as a percentage, which needs both the used series and the machine's total.</summary>
    private async Task LoadMemoryAsync(CancellationToken cancellationToken)
    {
        var totals = await metrics.LatestByScopeAsync("memory_total_bytes", cancellationToken);
        this.MemoryTotal = totals.Count > 0 ? totals[0].Value : 0;

        var used = await metrics.GaugeAsync("memory_used_bytes", this.Hours, this.Bucket, cancellationToken);
        if (this.MemoryTotal <= 0)
        {
            // Charting bytes against an unknown total would be a line with no meaning, so the raw
            // series is kept and the card labels it in bytes instead of percent.
            this.Memory = used;
            return;
        }

        this.Memory = [.. used.Select(point => new Point(point.At, point.Value / this.MemoryTotal * 100))];
    }

    /// <summary>Every filesystem now, plus a trend for the biggest one.</summary>
    private async Task LoadDisksAsync(CancellationToken cancellationToken)
    {
        var totals = await metrics.LatestByScopeAsync("filesystem_total_bytes", cancellationToken);
        var used = await metrics.LatestByScopeAsync("filesystem_used_bytes", cancellationToken);
        var usedByDevice = used.ToDictionary(row => row.Scope, row => row.Value, StringComparer.Ordinal);

        this.Disks =
        [
            .. totals
                .Where(total => total.Value > 0 && usedByDevice.ContainsKey(total.Scope))
                .Select(total => new DiskNow(total.Scope, usedByDevice[total.Scope], total.Value))
                .OrderByDescending(disk => disk.Total)
        ];

        if (this.Disks.Count == 0)
        {
            return;
        }

        // The trend is for the largest filesystem, which on a single-disk VPS is the only one and
        // in every case is the one that matters - it is where the database and the logs live.
        var biggest = this.Disks[0];
        var series = await metrics.GaugeForScopeAsync(
            "filesystem_used_bytes", biggest.Device, this.Hours, this.Bucket, cancellationToken);

        this.Disk = [.. series.Select(point => new Point(point.At, point.Value / biggest.Total * 100))];
    }

    /// <summary>Splits the log rate into the two levels worth watching.</summary>
    private async Task LoadLogRatesAsync(CancellationToken cancellationToken)
    {
        var rates = await metrics.LogRateAsync(this.Hours, this.Bucket, cancellationToken);

        // Fatal counts as an error. A separate line for a level that produces one point a year
        // would be an empty chart next to a useful one.
        this.Errors =
        [
            .. rates
                .Where(rate => rate.Scope is "Error" or "Fatal")
                .GroupBy(rate => rate.At)
                .Select(group => new Point(group.Key, group.Sum(rate => rate.Value)))
                .OrderBy(point => point.At)
        ];

        this.Warnings =
        [
            .. rates
                .Where(rate => rate.Scope == "Warning")
                .Select(rate => new Point(rate.At, rate.Value))
                .OrderBy(point => point.At)
        ];
    }
}
