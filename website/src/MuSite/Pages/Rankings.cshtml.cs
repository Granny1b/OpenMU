using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Data;
using MuSite.Game;
using MuSite.Services;

namespace MuSite.Pages;

public sealed class RankingsModel(RankingCache cache) : PageModel
{
    private const int PageSize = 50;

    public static readonly (RankingBoard Board, string Title)[] Boards =
    [
        (RankingBoard.Level, "By level"),
        (RankingBoard.Resets, "By resets"),
        (RankingBoard.Master, "By master level"),
        (RankingBoard.Pk, "By player kills"),
    ];

    public RankingBoard Board { get; private set; } = RankingBoard.Level;

    public string BoardTitle => Boards.First(b => b.Board == this.Board).Title;

    public IReadOnlyList<RankingRow> Rows { get; private set; } = [];

    public int Total { get; private set; }

    /// <summary>1-based page number. NOT called "Page": that would hide PageModel.Page().</summary>
    public int PageNumber { get; private set; } = 1;

    public int Offset => (this.PageNumber - 1) * PageSize;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(this.Total / (double)PageSize));

    public string? Search { get; private set; }

    public string Age { get; private set; } = "just now";

    /// <summary>The search as a query string fragment, for the board links.</summary>
    public string SearchQuery => string.IsNullOrWhiteSpace(this.Search)
        ? string.Empty
        : $"?q={Uri.EscapeDataString(this.Search)}";

    /// <summary>Link to another board, carrying the current search.</summary>
    public string BoardLink(RankingBoard board)
        => $"/rankings/{board.ToString().ToLowerInvariant()}{this.SearchQuery}";

    public string PageLink(int page)
    {
        var query = $"?page={page}";
        if (!string.IsNullOrWhiteSpace(this.Search))
        {
            query += $"&q={Uri.EscapeDataString(this.Search)}";
        }

        return $"/rankings/{this.Board.ToString().ToLowerInvariant()}{query}";
    }

    /// <summary>The hero-state or game-master badge for a row, or nothing.</summary>
    public static string BadgeFor(RankingRow row)
    {
        if (row.CharStatus == GameEnums.CharacterStatus.GameMaster)
        {
            return """<span class="mu-badge mu-badge--gm">GM</span>""";
        }

        return row.HeroState switch
        {
            GameEnums.HeroState.Hero => """<span class="mu-badge mu-badge--hero">Hero</span>""",
            GameEnums.HeroState.LightHero => """<span class="mu-badge mu-badge--hero">Light Hero</span>""",
            GameEnums.HeroState.PlayerKiller1stStage => """<span class="mu-badge mu-badge--pk">Outlaw</span>""",
            GameEnums.HeroState.PlayerKiller2ndStage => """<span class="mu-badge mu-badge--pk">Murderer</span>""",
            _ => string.Empty,
        };
    }

    public async Task<IActionResult> OnGetAsync(string? board, [FromQuery] int page = 1, [FromQuery] string? q = null, CancellationToken cancellationToken = default)
    {
        // Unknown board names 404 rather than silently falling back, so a typo in a shared link is
        // visible instead of quietly showing a different board.
        if (!string.IsNullOrEmpty(board))
        {
            if (!Enum.TryParse<RankingBoard>(board, ignoreCase: true, out var parsed))
            {
                return this.NotFound();
            }

            this.Board = parsed;
        }

        this.PageNumber = Math.Max(1, page);
        this.Search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var count = await cache.CountRankingAsync(this.Board, this.Search, cancellationToken).ConfigureAwait(false);
        this.Total = count.Value;

        var rows = await cache.GetRankingAsync(this.Board, this.Offset, PageSize, this.Search, cancellationToken).ConfigureAwait(false);
        this.Rows = rows.Value;
        this.Age = rows.Age;

        return this.Page();
    }
}
