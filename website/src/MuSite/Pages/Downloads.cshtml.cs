using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MuSite.Pages;

public sealed class DownloadsModel(IOptions<SiteOptions> options) : PageModel
{
    public string ConnectHost => string.IsNullOrWhiteSpace(options.Value.ConnectHost)
        ? "ask an administrator"
        : options.Value.ConnectHost;

    public string ClientUrl => options.Value.ClientUrl;

    public int MaxPassword => options.Value.MaxPassword;

    public void OnGet()
    {
    }
}
