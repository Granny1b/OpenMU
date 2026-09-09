using System.Text.RegularExpressions;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Markup rules the Content-Security-Policy makes invisible.
///
/// Program.cs serves `style-src 'self'` with no 'unsafe-inline' and no script-src at all, so the
/// browser silently DROPS every style="..." attribute and refuses every script. Nothing errors -
/// the page just renders unstyled, and curl cannot see it because curl does not enforce CSP.
///
/// That is not hypothetical. The /item builder shipped with 24 inline styles and every one was
/// dead: its option checkboxes ran together into a paragraph of text, its selects were full width,
/// and its fields never went into columns. It looked fine in every check that was run because none
/// of them was a browser.
/// </summary>
public sealed class MarkupTests
{
    private static readonly string PagesRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MuSite/Pages"));

    public static TheoryData<string> Pages
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var page in Directory.EnumerateFiles(PagesRoot, "*.cshtml", SearchOption.AllDirectories))
            {
                data.Add(Path.GetRelativePath(PagesRoot, page));
            }

            return data;
        }
    }

    [Fact]
    public void ThePagesDirectoryWasFound()
    {
        // Without this the theories below pass vacuously if the path ever moves.
        Assert.True(Directory.Exists(PagesRoot), $"no Pages directory at {PagesRoot}");
        Assert.True(Directory.EnumerateFiles(PagesRoot, "*.cshtml", SearchOption.AllDirectories).Count() > 10);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void NoPageCarriesAnInlineStyleAttribute(string page)
    {
        var markup = File.ReadAllText(Path.Combine(PagesRoot, page));

        Assert.DoesNotContain("style=\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("style='", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContentSecurityPolicyStillForbidsInlineStylesAndScript()
    {
        // The tempting "fix" for a page that renders unstyled is to add 'unsafe-inline'. That would
        // trade the site's whole XSS posture for a layout bug whose real fix is a class in mu.css.
        var program = File.ReadAllText(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MuSite/Program.cs")));

        // Read a window rather than regex to a ";" - the policy STRING is full of semicolons, and a
        // lazy match stops at the first one, inside "default-src 'none';", proving nothing.
        var start = program.IndexOf("\"Content-Security-Policy\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "could not find the Content-Security-Policy assignment in Program.cs");

        var next = program.IndexOf("headers[", start + 1, StringComparison.Ordinal);
        var value = next > start
            ? program[start..next]
            : program[start..Math.Min(program.Length, start + 600)];
        Assert.Contains("style-src 'self'", value, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", value, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", value, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMuClassInTheMarkupIsDefinedInTheStylesheet()
    {
        // A misspelled class is as invisible as a dropped inline style: no error, no styling. The
        // only classes exempt are the ones built at runtime, which BarStepTests covers instead.
        var stylesheet = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/MuSite/wwwroot/css/mu.css")));

        var undefined = new SortedSet<string>();
        foreach (var page in Directory.EnumerateFiles(PagesRoot, "*.cshtml", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(page);
            foreach (Match attribute in Regex.Matches(markup, @"class=""(?<names>[^""]*)"""))
            {
                foreach (var name in attribute.Groups["names"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!name.StartsWith("mu-", StringComparison.Ordinal) || name.Contains('@'))
                    {
                        continue;
                    }

                    if (!stylesheet.Contains("." + name, StringComparison.Ordinal))
                    {
                        undefined.Add($"{Path.GetRelativePath(PagesRoot, page)}: {name}");
                    }
                }
            }
        }

        Assert.Empty(undefined);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void NoPageCarriesScriptOrAnEventHandler(string page)
    {
        var markup = File.ReadAllText(Path.Combine(PagesRoot, page));

        Assert.DoesNotContain("<script", markup, StringComparison.OrdinalIgnoreCase);

        // onclick=, onchange=, onsubmit= and friends are inline script, refused by the same policy.
        var handler = Regex.Match(markup, @"\son[a-z]+\s*=\s*[""']", RegexOptions.IgnoreCase);
        Assert.False(handler.Success, $"{page} has an inline event handler: {handler.Value.Trim()}");
    }
}
