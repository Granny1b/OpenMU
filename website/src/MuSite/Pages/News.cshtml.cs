using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Services;

namespace MuSite.Pages;

public sealed class NewsModel(NewsStore news) : PageModel
{
    public IReadOnlyList<NewsItem> Posts { get; private set; } = [];

    public NewsItem? Single { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? slug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            this.Posts = await news.GetPublishedAsync(20, cancellationToken).ConfigureAwait(false);
            return this.Page();
        }

        this.Single = await news.GetBySlugAsync(slug, cancellationToken).ConfigureAwait(false);
        return this.Single is null ? this.NotFound() : this.Page();
    }
}
