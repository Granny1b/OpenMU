using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MuSite.Data;

namespace MuSite.Pages;

public sealed class IndexModel(SchemaContract contract, IOptions<SiteOptions> options) : PageModel
{
    public string ServerName => options.Value.ServerName;

    public bool SchemaHealthy => contract.IsHealthy;

    public void OnGet()
    {
    }
}
