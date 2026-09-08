using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MuSite.Data;
using MuSite.Live;
using MuSite.Services;

namespace MuSite.Pages;

public sealed class IndexModel(
    RankingCache cache,
    NewsStore news,
    SchemaContract contract,
    ServerProbe probe,
    IOptions<SiteOptions> options,
    ILogger<IndexModel> logger) : PageModel
{
    public string ServerName => options.Value.ServerName;

    public string ConnectHost => string.IsNullOrWhiteSpace(options.Value.ConnectHost)
        ? "the address in the launcher"
        : options.Value.ConnectHost;

    public bool SchemaHealthy => contract.IsHealthy;

    public bool IsUp => probe.State.IsUp;

    public int Accounts { get; private set; }

    public int Characters { get; private set; }

    public IReadOnlyList<RankingRow> Top { get; private set; } = [];

    public IReadOnlyList<NewsItem> News { get; private set; } = [];

    public string Age { get; private set; } = "just now";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // The home page must render even when the database is unreachable - it is the page that tells
        // players something is wrong, so it cannot be the page that 500s when something is wrong.
        try
        {
            var totals = await cache.GetTotalsAsync(cancellationToken).ConfigureAwait(false);
            this.Accounts = totals.Value.Accounts;
            this.Characters = totals.Value.Characters;

            var top = await cache.GetRankingAsync(RankingBoard.Level, 0, 5, null, cancellationToken).ConfigureAwait(false);
            this.Top = top.Value;
            this.Age = top.Age;

            this.News = await news.GetPublishedAsync(3, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let the front page 500: it is the page that tells players something is wrong.
            logger.LogError(ex, "Home page could not read the game database.");
            this.Top = [];
        }
    }
}
