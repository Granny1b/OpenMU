using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Data;
using MuSite.Services;

namespace MuSite.Pages;

public sealed class GuildsModel(RankingCache cache) : PageModel
{
    private const int PageSize = 50;

    public IReadOnlyList<GuildRow> Rows { get; private set; } = [];

    /// <summary>1-based page number. NOT called "Page": that would hide PageModel.Page().</summary>
    public int PageNumber { get; private set; } = 1;

    public int Offset => (this.PageNumber - 1) * PageSize;

    /// <summary>
    /// True when a further page exists. Guilds are read one row over the page size and the extra row
    /// trimmed, which answers "is there a next page" without a second count query.
    /// </summary>
    public bool HasMore { get; private set; }

    public string? Search { get; private set; }

    public string Age { get; private set; } = "just now";

    public string PageLink(int page)
    {
        var query = $"?page={page}";
        if (!string.IsNullOrWhiteSpace(this.Search))
        {
            query += $"&q={Uri.EscapeDataString(this.Search)}";
        }

        return $"/guilds{query}";
    }

    public async Task OnGetAsync([FromQuery] int page = 1, [FromQuery] string? q = null, CancellationToken cancellationToken = default)
    {
        this.PageNumber = Math.Max(1, page);
        this.Search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var result = await cache.GetGuildsAsync(this.Offset, PageSize + 1, this.Search, cancellationToken).ConfigureAwait(false);
        this.Age = result.Age;
        this.HasMore = result.Value.Count > PageSize;
        this.Rows = this.HasMore ? result.Value.Take(PageSize).ToList() : result.Value;
    }
}
