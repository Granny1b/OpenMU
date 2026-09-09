using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using MuSite.Charts;
using MuSite.Services;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The chart geometry, which is the part that cannot be checked by looking at a screenshot.
/// </summary>
public sealed class ChartTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChartFormat Percent = new("%", 0, 100);

    [Fact]
    public void CoordinatesAreTheSameInALocaleThatWritesDecimalCommas()
    {
        // "12,5" in an SVG path is not a number with a fraction - it is TWO numbers - so a machine
        // set to a comma-decimal locale would render every chart as noise. Nothing about the page
        // would look broken in a build or a test run on an English machine.
        var points = Sample();

        var invariant = Render(points, CultureInfo.InvariantCulture);
        var german = Render(points, new CultureInfo("de-DE"));

        Assert.Equal(invariant, german);
    }

    [Fact]
    public void AGapInTheDataBreaksTheLineInsteadOfBridgingIt()
    {
        // Straight line across an outage would claim measurements nobody took, and would hide the
        // outage itself - which is the single thing an operator most needs to see.
        List<Point> withHole =
        [
            new(Noon, 10),
            new(Noon.AddMinutes(1), 12),
            // twenty minutes of nothing
            new(Noon.AddMinutes(21), 14),
            new(Noon.AddMinutes(22), 16),
        ];

        var svg = Render(withHole, CultureInfo.InvariantCulture);
        var line = PathOf(svg, "mu-chart__line");

        Assert.Equal(2, Regex.Matches(line, "M").Count);
    }

    [Fact]
    public void ContinuousDataDrawsOneUnbrokenLine()
    {
        var svg = Render(Sample(), CultureInfo.InvariantCulture);

        Assert.Single(Regex.Matches(PathOf(svg, "mu-chart__line"), "M"));
    }

    [Fact]
    public void NothingToDrawSaysSoRatherThanRenderingAnEmptyBox()
    {
        var svg = Render([], CultureInfo.InvariantCulture);

        Assert.Contains("mu-chart__empty", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void ASinglePointIsDrawnAsADot()
    {
        // One point has no line. Without this it would render as a completely blank chart, which
        // looks identical to "collection is broken".
        var svg = Render([new Point(Noon.AddMinutes(5), 42)], CultureInfo.InvariantCulture);

        Assert.Contains("mu-chart__dot", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void APercentageChartIsScaledToOneHundredAndNotToItsOwnPeak()
    {
        // Auto-scaling a quiet hour would draw 3% CPU as a line near the top of the chart. The
        // shape of a percentage only means something against a fixed ceiling.
        var quiet = new List<Point> { new(Noon, 3), new(Noon.AddMinutes(1), 4) };

        var svg = Render(quiet, CultureInfo.InvariantCulture);

        Assert.Contains(">100%<", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnboundedChartRoundsItsCeilingToSomethingReadable()
    {
        var spiky = new List<Point> { new(Noon, 37.194), new(Noon.AddMinutes(1), 12) };

        var svg = (string)RenderRaw(spiky, new ChartFormat(" MB/s", 0), CultureInfo.InvariantCulture);

        // 37.194 tops out at 50, not at 37.194.
        Assert.Contains(">50 MB/s<", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAllZeroSeriesStillProducesAScale()
    {
        // Dividing by a zero maximum would put every point at NaN, and NaN in a path attribute
        // makes the browser drop the whole path silently.
        var flat = new List<Point> { new(Noon, 0), new(Noon.AddMinutes(1), 0) };

        var svg = (string)RenderRaw(flat, new ChartFormat(), CultureInfo.InvariantCulture);

        Assert.DoesNotContain("NaN", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesAboveTheCeilingAreClampedIntoThePlotArea()
    {
        // A percentage over 100 can arrive from a counter that reset mid-bucket. It must not draw
        // above the top of the box and over the card's heading.
        var over = new List<Point> { new(Noon, 250) };

        var svg = Render(over, CultureInfo.InvariantCulture);

        // The bounds are read out of the viewBox rather than written down here, so changing the
        // chart's proportions does not fail a test about clamping.
        var viewBox = Regex.Match(svg, @"viewBox=""0 0 ([\d.]+) ([\d.]+)""");
        Assert.True(viewBox.Success, "the chart has no viewBox");
        var height = double.Parse(viewBox.Groups[2].Value, CultureInfo.InvariantCulture);

        var drawn = Regex.Matches(svg, @"c[xy]=""(-?[\d.]+)""|,(-?[\d.]+)(?=[LMZ]|$)");
        Assert.NotEmpty(drawn);
        foreach (Match match in drawn)
        {
            var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            Assert.InRange(double.Parse(raw, CultureInfo.InvariantCulture), 0, height);
        }
    }

    [Fact]
    public void EveryPointGetsAHoverReadoutBecauseThereIsNoJavaScript()
    {
        var svg = Render(Sample(), CultureInfo.InvariantCulture);

        Assert.Equal(Sample().Count, Regex.Matches(svg, "mu-chart__hit").Count);
    }

    [Fact]
    public void TitleTextIsEscaped()
    {
        // Device and level names reach the title, and one of them containing a bracket must not be
        // able to close the element.
        var svg = (string)Chart.Line(
            Sample(), Noon, Noon.AddHours(1), 60, Percent, "<script>alert(1)</script>").ToString()!;

        Assert.DoesNotContain("<script>", svg, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMeterBandsChangeWithHowFullItIs()
    {
        Assert.Contains("mu-meter__fill--ok", Meter(10, 100), StringComparison.Ordinal);
        Assert.Contains("mu-meter__fill--warn", Meter(80, 100), StringComparison.Ordinal);
        Assert.Contains("mu-meter__fill--critical", Meter(95, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void AMeterWithNoTotalSaysItIsNotMeasuredRatherThanDividingByZero()
    {
        Assert.Contains("not being measured", Meter(5, 0), StringComparison.Ordinal);
    }

    /// <summary>Pulls the `d` attribute of the path with the given class out of the SVG.</summary>
    private static string PathOf(string svg, string cssClass)
    {
        var match = Regex.Match(svg, $@"class=""{Regex.Escape(cssClass)}"" d=""([^""]*)""");
        Assert.True(match.Success, $"no path with class {cssClass} in the rendered chart");
        return match.Groups[1].Value;
    }

    private static List<Point> Sample() =>
    [
        new(Noon, 10),
        new(Noon.AddMinutes(1), 20),
        new(Noon.AddMinutes(2), 15),
        new(Noon.AddMinutes(3), 40),
    ];

    private static string Meter(double used, double total) =>
        Chart.Meter(used, total, "Disk", "detail").ToString()!;

    private static string Render(IReadOnlyList<Point> points, CultureInfo culture) =>
        (string)RenderRaw(points, Percent, culture);

    private static object RenderRaw(IReadOnlyList<Point> points, ChartFormat format, CultureInfo culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            IHtmlContent content = Chart.Line(points, Noon, Noon.AddHours(1), 60, format, "Test chart");
            return content.ToString()!;
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
