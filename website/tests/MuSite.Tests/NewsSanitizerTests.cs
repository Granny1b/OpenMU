using MuSite.Services;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// News HTML is written to the page with Html.Raw, so what comes out of the renderer is what runs in
/// a visitor's browser. Two gates produce it - Markdig with DisableHtml, then HtmlSanitizer - and
/// neither alone is enough: the first stops embedded HTML, the second stops the hostile links that
/// ordinary Markdown syntax can produce. An administrator account is the highest-value target on the
/// site, so its own input is not trusted either.
/// </summary>
public class NewsSanitizerTests
{
    [Fact]
    public void RawScriptTagsDoNotSurvive()
    {
        var html = NewsStore.Render("Hello <script>alert(document.cookie)</script> there");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(document.cookie)</script>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JaVaScRiPt:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [InlineData("[click](vbscript:msgbox(1))")]
    public void HostileLinkSchemesAreStripped(string markdown)
    {
        // These are plain Markdown links, so DisableHtml does not touch them - only the sanitizer's
        // scheme allowlist does.
        var html = NewsStore.Render(markdown);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEventHandlerNeverBecomesAnAttribute()
    {
        var html = NewsStore.Render("""<img src="x" onerror="alert(1)" />""");

        // DisableHtml escapes the tag, so what reaches the browser is the TEXT
        // `&lt;img src="x" onerror="alert(1)" /&gt;` inside a paragraph. The literal substring
        // "onerror" therefore survives in the output and always will - but with the angle brackets
        // escaped there is no img element for it to be an attribute of, and an attribute that is
        // not on an element cannot fire. Asserting on that substring tests the wrong thing; these
        // two assertions test the property that matters.
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);

        // The positive half is not redundant. Without it, an output where the input had been
        // dropped entirely - or where Render had thrown and returned nothing - would pass just as
        // happily as one where the escaping worked.
        Assert.Contains("&lt;img", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryFormattingIsKept()
    {
        var html = NewsStore.Render("**bold** and *italic* and [a link](https://example.org)");

        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<em>italic</em>", html, StringComparison.Ordinal);
        Assert.Contains("https://example.org", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ListsHeadingsAndCodeSurvive()
    {
        var html = NewsStore.Render("## Patch notes\n\n- one\n- two\n\n`code`");

        Assert.Contains("<h2", html, StringComparison.Ordinal);
        Assert.Contains("<li>one</li>", html, StringComparison.Ordinal);
        Assert.Contains("<code>code</code>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Golden Invasion times", "golden-invasion-times")]
    [InlineData("  Spaces   everywhere  ", "spaces-everywhere")]
    [InlineData("Ünïcödé & symbols!", "n-c-d-symbols")]
    [InlineData("", "post")]
    [InlineData("!!!", "post")]
    public void SlugsAreUrlSafe(string title, string expected)
        => Assert.Equal(expected, NewsStore.Slugify(title));

    [Fact]
    public void LongTitlesProduceABoundedSlug()
    {
        var slug = NewsStore.Slugify(new string('a', 200));

        Assert.True(slug.Length <= 60);
        Assert.DoesNotContain("-", slug, StringComparison.Ordinal);
    }
}
