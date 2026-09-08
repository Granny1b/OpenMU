using System.Text.RegularExpressions;
using Dapper;
using Ganss.Xss;
using Markdig;
using MuSite.Data;

namespace MuSite.Services;

/// <summary>One announcement.</summary>
public sealed record NewsItem(
    Guid Id, string Slug, string Title, string BodyMarkdown, string BodyHtml, string Author,
    bool IsPublished, bool IsPinned, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

/// <summary>
/// Announcements, written in Markdown by an administrator and rendered once at save time.
///
/// The rendered HTML goes through TWO gates, because it is later written to the page with Html.Raw:
///   1. Markdig with DisableHtml(), so a raw &lt;script&gt; in the source is escaped rather than
///      passed through;
///   2. HtmlSanitizer over the result, which also catches what Markdown itself can produce -
///      javascript: and data: URLs in an ordinary [link](...), which DisableHtml does not touch.
///
/// One gate is not enough: the first stops embedded HTML, the second stops hostile links. An
/// administrator account is the highest-value target on the site, so its own input is not trusted
/// blindly either.
/// </summary>
public sealed class NewsStore(SiteDataSources sources)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .Build();

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    /// <summary>Renders Markdown to the HTML that will be stored and later shown.</summary>
    public static string Render(string markdown)
        => Sanitizer.Sanitize(Markdown.ToHtml(markdown ?? string.Empty, Pipeline));

    /// <summary>Turns a title into a URL slug.</summary>
    public static string Slugify(string title)
    {
        var lower = (title ?? string.Empty).ToLowerInvariant();
        var cleaned = Regex.Replace(lower, "[^a-z0-9]+", "-", RegexOptions.CultureInvariant).Trim('-');
        var slug = cleaned.Length > 60 ? cleaned[..60].TrimEnd('-') : cleaned;
        return slug.Length == 0 ? "post" : slug;
    }

    /// <summary>Published announcements, newest first, pinned ones on top.</summary>
    public async Task<IReadOnlyList<NewsItem>> GetPublishedAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<NewsItem>(new CommandDefinition(
            Select + " WHERE is_published ORDER BY is_pinned DESC, published_at DESC LIMIT @limit",
            new { limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>Every announcement, for the admin list.</summary>
    public async Task<IReadOnlyList<NewsItem>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<NewsItem>(new CommandDefinition(
            Select + " ORDER BY is_pinned DESC, created_at DESC LIMIT 200",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>One published announcement by slug.</summary>
    public async Task<NewsItem?> GetBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<NewsItem>(new CommandDefinition(
            Select + " WHERE slug = @slug AND is_published",
            new { slug },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>One announcement by id, published or not, for editing.</summary>
    public async Task<NewsItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<NewsItem>(new CommandDefinition(
            Select + " WHERE id = @id",
            new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Creates an announcement and returns its id.</summary>
    public async Task<Guid> CreateAsync(string title, string markdown, string author, bool publish, bool pin, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();

        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO news (id, slug, title, body_markdown, body_html, author, is_published, is_pinned, published_at)
            VALUES (@id, @slug, @title, @markdown, @html, @author, @publish, @pin,
                    CASE WHEN @publish THEN now() ELSE NULL END)
            """,
            new
            {
                id,
                slug = await this.UniqueSlugAsync(Slugify(title), null, cancellationToken).ConfigureAwait(false),
                title,
                markdown,
                html = Render(markdown),
                author,
                publish,
                pin,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return id;
    }

    /// <summary>Updates an announcement, re-rendering its HTML.</summary>
    public async Task UpdateAsync(Guid id, string title, string markdown, bool publish, bool pin, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE news
               SET title = @title, body_markdown = @markdown, body_html = @html,
                   is_published = @publish, is_pinned = @pin, updated_at = now(),
                   published_at = CASE WHEN @publish AND published_at IS NULL THEN now()
                                       WHEN @publish THEN published_at
                                       ELSE NULL END
             WHERE id = @id
            """,
            new { id, title, markdown, html = Render(markdown), publish, pin },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private const string Select =
        """
        SELECT id AS Id, slug AS Slug, title AS Title, body_markdown AS BodyMarkdown,
               body_html AS BodyHtml, author AS Author, is_published AS IsPublished,
               is_pinned AS IsPinned, created_at AS CreatedAt, published_at AS PublishedAt
          FROM news
        """;

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();

        // Only what Markdown legitimately produces. Anything else - including every event handler
        // attribute - is stripped.
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "em", "del", "code", "pre",
            "blockquote", "ul", "ol", "li", "a", "img", "table", "thead", "tbody", "tr", "th", "td",
        })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.Add("href");
        sanitizer.AllowedAttributes.Add("title");
        sanitizer.AllowedAttributes.Add("alt");
        sanitizer.AllowedAttributes.Add("src");

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");

        return sanitizer;
    }

    private async Task<string> UniqueSlugAsync(string desired, Guid? excluding, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var slug = desired;
        for (var suffix = 2; suffix < 100; suffix++)
        {
            var taken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM news WHERE slug = @slug AND (@excluding::uuid IS NULL OR id <> @excluding::uuid))",
                new { slug, excluding },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (!taken)
            {
                return slug;
            }

            slug = $"{desired}-{suffix}";
        }

        return $"{desired}-{Guid.NewGuid():N}"[..60];
    }
}
