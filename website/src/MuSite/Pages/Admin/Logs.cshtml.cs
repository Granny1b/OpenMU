using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Services;

namespace MuSite.Pages.Admin;

/// <summary>
/// Searches the game server's log, shipped in by Vector.
///
/// This reads openmu_web.server_log; it is not a view onto the files. OpenMU's own admin panel has
/// /logfiles for those, which is a file browser - fine for reading today's log end to end, useless
/// for "every error from the persistence layer this week", which is what this page is for.
/// </summary>
public sealed class LogsModel(ServerLog log) : PageModel
{
    /// <summary>Rows per page.</summary>
    private const int PageSize = 100;

    /// <summary>The time windows offered. 0 is everything still in the table.</summary>
    public static readonly (int Hours, string Label)[] Windows =
    [
        (1, "Last hour"),
        (24, "Last 24 hours"),
        (24 * 7, "Last 7 days"),
        (0, "Everything kept"),
    ];

    /// <summary>What is being looked at.</summary>
    public LogFilter Filter { get; private set; } = new(24, null, null, null);

    /// <summary>This page of lines.</summary>
    public IReadOnlyList<LogEntry> Entries { get; private set; } = [];

    /// <summary>The levels present in the window, with counts.</summary>
    public IReadOnlyList<LevelCount> Levels { get; private set; } = [];

    /// <summary>The busiest categories in the window.</summary>
    public IReadOnlyList<SourceCount> Sources { get; private set; } = [];

    /// <summary>How many lines match in total.</summary>
    public int Total { get; private set; }

    /// <summary>1-based page number. NOT called "Page": that would hide PageModel.Page().</summary>
    public int PageNumber { get; private set; } = 1;

    /// <summary>How many pages the current filter fills.</summary>
    public int PageCount => Math.Max(1, (int)Math.Ceiling(this.Total / (double)PageSize));

    /// <summary>The first row of the current page.</summary>
    public int FirstRow => this.Total == 0 ? 0 : ((this.PageNumber - 1) * PageSize) + 1;

    /// <summary>The last row of the current page.</summary>
    public int LastRow => Math.Min(this.PageNumber * PageSize, this.Total);

    /// <summary>
    /// True when a level, category or search is narrowing the window.
    ///
    /// The empty state needs this rather than the level counts: those respect the search, so a
    /// search matching nothing empties them, and the page then claimed there was nothing in the
    /// window at all while a hundred lines sat behind the filter.
    /// </summary>
    public bool Filtered => this.Filter.Level is not null
        || this.Filter.Source is not null
        || this.Filter.Search is not null;

    /// <summary>
    /// True when server_log is not there at all - almost always a mu-site-migrate image built
    /// before the migration existed. The page says so instead of showing an empty table, because
    /// "no logs" and "no table" look identical and lead to completely different fixes.
    /// </summary>
    public bool TableMissing => log.TableMissing;

    /// <summary>A link to the same search with one thing changed.</summary>
    public string Link(int? hours = null, string? level = null, string? source = null, int page = 1)
    {
        var link = $"/admin/logs?hours={hours ?? this.Filter.Hours}";

        // An explicit empty string means "clear this filter", which is why these are not just
        // null-coalesced: level: "" has to win over the current level.
        var wantedLevel = level ?? this.Filter.Level;
        var wantedSource = source ?? this.Filter.Source;

        if (!string.IsNullOrEmpty(wantedLevel))
        {
            link += $"&level={Uri.EscapeDataString(wantedLevel)}";
        }

        if (!string.IsNullOrEmpty(wantedSource))
        {
            link += $"&source={Uri.EscapeDataString(wantedSource)}";
        }

        if (!string.IsNullOrEmpty(this.Filter.Search))
        {
            link += $"&q={Uri.EscapeDataString(this.Filter.Search)}";
        }

        if (page > 1)
        {
            link += $"&page={page}";
        }

        return link;
    }

    public async Task OnGetAsync(
        [FromQuery] int? hours,
        [FromQuery] string? level,
        [FromQuery] string? source,
        [FromQuery] string? q,
        [FromQuery] int page,
        CancellationToken cancellationToken)
    {
        // An unrecognised window falls back to 24 hours rather than being honoured: ?hours=999999
        // is a full table scan on the largest table in the database.
        var window = hours is { } requested && Windows.Any(w => w.Hours == requested) ? requested : 24;

        this.Filter = new LogFilter(
            window,
            string.IsNullOrWhiteSpace(level) ? null : level.Trim(),
            string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
            string.IsNullOrWhiteSpace(q) ? null : q.Trim());

        this.Levels = await log.LevelsAsync(this.Filter, cancellationToken).ConfigureAwait(false);
        this.Sources = await log.SourcesAsync(this.Filter, 40, cancellationToken).ConfigureAwait(false);
        this.Total = await log.CountAsync(this.Filter, cancellationToken).ConfigureAwait(false);

        // Clamped both ways: an out-of-range OFFSET returns nothing, which reads as "no logs".
        this.PageNumber = Math.Clamp(page < 1 ? 1 : page, 1, this.PageCount);

        this.Entries = await log
            .PageAsync(this.Filter, (this.PageNumber - 1) * PageSize, PageSize, cancellationToken)
            .ConfigureAwait(false);
    }
}
