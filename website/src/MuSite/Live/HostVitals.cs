using System.Globalization;
using Microsoft.Extensions.Options;

namespace MuSite.Live;

/// <summary>What the machine looked like at one instant.</summary>
/// <param name="At">When the reading was taken.</param>
/// <param name="CpuPercent">Busy share of all cores since the previous reading, or null on the first one.</param>
/// <param name="MemoryUsedPercent">Used share of RAM, or null when /proc/meminfo could not be read.</param>
/// <param name="MemoryTotalBytes">Total RAM.</param>
/// <param name="MemoryAvailableBytes">RAM available to start a new process without swapping.</param>
/// <param name="Load1">Run queue averaged over one minute.</param>
/// <param name="Load5">Run queue averaged over five minutes.</param>
/// <param name="Load15">Run queue averaged over fifteen minutes.</param>
/// <param name="CpuCount">Cores, so a load average can be read as a ratio.</param>
/// <param name="Problem">Why the reading is empty, when it is.</param>
public sealed record HostSample(
    DateTimeOffset At,
    double? CpuPercent,
    double? MemoryUsedPercent,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    double? Load1,
    double? Load5,
    double? Load15,
    int CpuCount,
    string? Problem = null);

/// <summary>
/// Reads the machine's vitals straight from the kernel, every few seconds, into memory.
///
/// WHY THIS EXISTS NEXT TO VECTOR
///
/// Vector already scrapes host_metrics into PostgreSQL every 30 seconds, and /admin/metrics charts
/// it. That interval is deliberate - it matches <see cref="ServerProbe"/> so the game series and
/// the machine series land on the same buckets - and lowering it is the expensive way to get a
/// faster reading: every scrape is about 24 rows written to a database that shares the box with the
/// game server, so 5 second collection would be six times the rows and six times the write load,
/// for history nobody reads at that resolution.
///
/// "Is the box struggling RIGHT NOW" does not need history at all. It needs the newest value and
/// nothing else, so this keeps exactly one sample, in memory, and writes nothing anywhere.
///
/// WHAT IT COSTS
///
/// Three small reads out of /proc per tick - loadavg is a few dozen bytes, meminfo and stat a few
/// kilobytes - plus parsing. There is no query, no socket and no allocation worth the name. Running
/// it at 5 seconds instead of 30 costs six times almost nothing.
///
/// WHY /proc IS THE HOST'S, FROM INSIDE A CONTAINER
///
/// cpu, memory and load are not namespaced, so /proc/stat, /proc/meminfo and /proc/loadavg inside
/// the container report the real machine - the same property vector.yaml relies on and records as
/// verified. No mount, no Docker socket, no privilege.
///
/// IT DEGRADES INSTEAD OF THROWING
///
/// On a developer's Windows or macOS machine there is no /proc. Every reader returns null rather
/// than raising, the sample carries a <see cref="HostSample.Problem"/> saying so, and the page
/// shows dashes. A dashboard that cannot read the kernel must not take the site down with it.
/// </summary>
public sealed class HostVitals(IOptionsMonitor<SiteOptions> options, ILogger<HostVitals> logger)
    : BackgroundService
{
    /// <summary>Shortest interval accepted, so a typo cannot turn this into a busy loop.</summary>
    private const int MinIntervalSeconds = 2;

    /// <summary>Longest interval accepted; past this, use /admin/metrics instead.</summary>
    private const int MaxIntervalSeconds = 60;

    private const string ProcStat = "/proc/stat";
    private const string ProcMemInfo = "/proc/meminfo";
    private const string ProcLoadAvg = "/proc/loadavg";

    private volatile HostSample _sample = new(
        DateTimeOffset.UtcNow, null, null, null, null, null, null, null, 0, "not read yet");

    /// <summary>Previous /proc/stat totals, for the CPU delta. Only touched by the loop.</summary>
    private (ulong Total, ulong Idle)? _previousCpu;

    /// <summary>The newest reading.</summary>
    public HostSample Sample => this._sample;

    /// <summary>
    /// How often the reading is refreshed, after clamping.
    /// </summary>
    /// <remarks>
    /// Resolved ONCE, not per call. The timer is built from it at startup, and the page renders it
    /// as the meta refresh interval; reading the options monitor live would let those two disagree
    /// after a configuration reload, so the page would promise a cadence the loop is not keeping.
    /// </remarks>
    public TimeSpan Interval { get; } = TimeSpan.FromSeconds(Math.Clamp(
        options.CurrentValue.LiveVitalsSeconds, MinIntervalSeconds, MaxIntervalSeconds));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(this.Interval);

        // Read once immediately so the first page load after a restart is not empty, then settle
        // into the timer. The very first reading carries no CPU percentage: it takes two samples of
        // /proc/stat to know how busy the interval between them was.
        do
        {
            try
            {
                this.Read();
            }
            catch (Exception ex)
            {
                // Same reasoning as ServerProbe: a reader that throws must not end the loop, or the
                // page freezes on a stale number while looking authoritative.
                logger.LogError(ex, "Reading the host vitals failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Parses a "MemTotal:  16316412 kB" style line into bytes.</summary>
    private static long? ParseMemInfoKilobytes(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var rest = line.AsSpan(colon + 1).Trim();
        var space = rest.IndexOf(' ');
        var number = space < 0 ? rest : rest[..space];

        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)
            ? kb * 1024
            : null;
    }

    private void Read()
    {
        var (cpu, problem) = this.ReadCpu();
        var (memoryUsedPercent, memoryTotal, memoryAvailable) = ReadMemory();
        var (load1, load5, load15) = ReadLoad();

        this._sample = new HostSample(
            DateTimeOffset.UtcNow,
            cpu,
            memoryUsedPercent,
            memoryTotal,
            memoryAvailable,
            load1,
            load5,
            load15,
            Environment.ProcessorCount,
            problem);
    }

    /// <summary>
    /// Busy share of all cores since the previous call.
    ///
    /// The columns of the aggregate "cpu" line are cumulative jiffies per state. Busy is everything
    /// that is not idle, and iowait counts as idle: a core waiting on the disk is not doing work,
    /// and counting it as busy makes a slow disk look like a CPU shortage - the opposite diagnosis.
    /// </summary>
    private (double? Percent, string? Problem) ReadCpu()
    {
        string line;
        try
        {
            using var reader = new StreamReader(ProcStat);
            line = reader.ReadLine() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            this._previousCpu = null;
            return (null, "this host exposes no /proc/stat");
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || !parts[0].Equals("cpu", StringComparison.Ordinal))
        {
            this._previousCpu = null;
            return (null, "/proc/stat did not start with an aggregate cpu line");
        }

        ulong total = 0;
        ulong idle = 0;
        for (var i = 1; i < parts.Length; i++)
        {
            if (!ulong.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            total += value;

            // Columns are user, nice, system, idle, iowait, ... - idle is 4, iowait is 5.
            if (i is 4 or 5)
            {
                idle += value;
            }
        }

        var previous = this._previousCpu;
        this._previousCpu = (total, idle);

        if (previous is not { } last || total <= last.Total)
        {
            // First reading, or the counters went backwards because the machine rebooted. Either
            // way there is no interval to measure; say nothing rather than draw a fabricated spike.
            return (null, null);
        }

        var totalDelta = total - last.Total;
        var idleDelta = idle >= last.Idle ? idle - last.Idle : 0UL;

        // These are unsigned, so an idle delta somehow larger than the total one would not go
        // negative - it would wrap to about 1.8e19 and report a quiet machine as pinned at 100%.
        if (idleDelta > totalDelta)
        {
            idleDelta = totalDelta;
        }

        var busy = 100.0 * (totalDelta - idleDelta) / totalDelta;

        return (Math.Clamp(busy, 0, 100), null);
    }

    /// <summary>
    /// Used share of RAM, from MemTotal and MemAvailable.
    ///
    /// MemAvailable, not MemFree: Linux spends free memory on page cache, so MemFree on a healthy
    /// machine reads near zero and would report a box that is fine as one about to die. MemAvailable
    /// is the kernel's own estimate of what a new process could get, cache included.
    /// </summary>
    private static (double? UsedPercent, long? Total, long? Available) ReadMemory()
    {
        long? total = null;
        long? available = null;
        long? free = null;

        try
        {
            foreach (var line in File.ReadLines(ProcMemInfo))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseMemInfoKilobytes(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseMemInfoKilobytes(line);
                }
                else if (line.StartsWith("MemFree:", StringComparison.Ordinal))
                {
                    free = ParseMemInfoKilobytes(line);
                }

                if (total is not null && available is not null)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            return (null, null, null);
        }

        // MemAvailable landed in Linux 3.14. Older kernels get the cruder MemFree so the tile shows
        // something rather than a dash.
        available ??= free;

        if (total is not { } totalBytes || totalBytes <= 0 || available is not { } availableBytes)
        {
            return (null, total, available);
        }

        var used = 100.0 * (totalBytes - availableBytes) / totalBytes;
        return (Math.Clamp(used, 0, 100), totalBytes, availableBytes);
    }

    /// <summary>The three load averages from /proc/loadavg.</summary>
    private static (double? One, double? Five, double? Fifteen) ReadLoad()
    {
        string content;
        try
        {
            content = File.ReadAllText(ProcLoadAvg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            return (null, null, null);
        }

        var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return (null, null, null);
        }

        static double? Parse(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

        return (Parse(parts[0]), Parse(parts[1]), Parse(parts[2]));
    }
}
