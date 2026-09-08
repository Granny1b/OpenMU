using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Services;

namespace MuSite.Pages.Admin;

public sealed class NewsModel(NewsStore news) : PageModel
{
    public IReadOnlyList<NewsItem> Posts { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
        => this.Posts = await news.GetAllAsync(cancellationToken).ConfigureAwait(false);
}
