using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Auth;
using MuSite.Services;

namespace MuSite.Pages.Admin;

public sealed class NewsEditModel(NewsStore news, AuditLog audit) : PageModel
{
    [BindProperty]
    public NewsInput Input { get; set; } = new();

    public bool IsNew { get; private set; } = true;

    public string? Error { get; private set; }

    public string? Preview { get; private set; }

    private Guid? PostId { get; set; }

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, "new", StringComparison.OrdinalIgnoreCase))
        {
            return this.Page();
        }

        if (!Guid.TryParse(id, out var postId))
        {
            return this.NotFound();
        }

        var post = await news.GetByIdAsync(postId, cancellationToken).ConfigureAwait(false);
        if (post is null)
        {
            return this.NotFound();
        }

        this.IsNew = false;
        this.PostId = postId;
        this.Preview = post.BodyHtml;
        this.Input = new NewsInput
        {
            Title = post.Title,
            Body = post.BodyMarkdown,
            Publish = post.IsPublished,
            Pin = post.IsPinned,
        };

        return this.Page();
    }

    public async Task<IActionResult> OnPostAsync(string id, CancellationToken cancellationToken)
    {
        var title = this.Input.Title?.Trim() ?? string.Empty;
        var body = this.Input.Body ?? string.Empty;

        if (title.Length == 0 || body.Trim().Length == 0)
        {
            this.Error = "A title and a body are both needed.";
            this.IsNew = string.IsNullOrWhiteSpace(id) || id == "new";
            return this.Page();
        }

        var actor = this.User.LoginName();

        if (!string.IsNullOrWhiteSpace(id) && id != "new" && Guid.TryParse(id, out var postId))
        {
            await news.UpdateAsync(postId, title, body, this.Input.Publish, this.Input.Pin, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync("news.updated", actor, postId, title, this.ClientIp(),
                this.Input.Publish ? "published" : "draft", cancellationToken).ConfigureAwait(false);
            this.TempData["Notice"] = "Announcement saved.";
        }
        else
        {
            var created = await news.CreateAsync(title, body, actor, this.Input.Publish, this.Input.Pin, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync("news.created", actor, created, title, this.ClientIp(),
                this.Input.Publish ? "published" : "draft", cancellationToken).ConfigureAwait(false);
            this.TempData["Notice"] = "Announcement created.";
        }

        return this.RedirectToPage("/Admin/News");
    }

    /// <summary>What the editor collects.</summary>
    public sealed class NewsInput
    {
        public string? Title { get; set; }

        public string? Body { get; set; }

        public bool Publish { get; set; } = true;

        public bool Pin { get; set; }
    }
}
