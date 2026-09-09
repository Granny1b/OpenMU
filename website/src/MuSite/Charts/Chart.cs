using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Html;

namespace MuSite.Charts;

/// <summary>How a chart should label its numbers.</summary>
/// <param name="Unit">Suffix shown after every value, e.g. "%" or " MB/s". May be empty.</param>
/// <param name="Decimals">Decimal places in labels and tooltips.</param>
/// <param name="Ceiling">
/// A fixed top of the scale. Percentages pass 100 so that a quiet hour is drawn as a low line
/// rather than being auto-scaled up into something that looks like a crisis.
/// </param>
public sealed record ChartFormat(string Unit = "", int Decimals = 0, double? Ceiling = null);

/// <summary>
/// Draws time series as inline SVG, on the server.
///
/// WHY NOT A CHARTING LIBRARY
///
/// This site's Content-Security-Policy has NO script-src at all. Not a restricted one - none - so
/// no JavaScript runs on any page here, and every chart library in existence is JavaScript. Inline
/// style attributes are dropped too (style-src 'self' with no 'unsafe-inline'), so the colours have
/// to come from classes in mu.css and the geometry from SVG presentation attributes, which are
/// attributes rather than CSS and are therefore untouched by the policy.
///
/// The upside of server-side SVG is that it needs no runtime at all: it is markup, it prints, it
/// works with JavaScript disabled, and a screen reader can read the title out of it. Hover readouts
/// come from SVG's own &lt;title&gt; elements, which browsers show as tooltips natively.
///
/// EVERY NUMBER IS FORMATTED WITH InvariantCulture. An SVG coordinate written by a machine set to a
/// locale that uses a decimal comma is not a coordinate - "12,5" parses as two numbers - and the
/// path silently renders as nonsense.
/// </summary>
public static class Chart
{
    /// <summary>How many gridlines and y-axis labels to draw.</summary>
    private const int GridLines = 4;

    /// <summary>
    /// The drawing area, in user units.
    /// </summary>
    /// <remarks>
    /// THE VIEWBOX WIDTH IS CHOSEN TO BE CLOSE TO THE RENDERED WIDTH, and that is the whole point of
    /// having two of these. An SVG with a viewBox scales uniformly to its container, TEXT INCLUDED -
    /// so one geometry shared by a full-width card and a third-width card renders the same labels at
    /// two very different sizes, and the small ones come out at about six pixels: present, correct,
    /// and unreadable. Sizing each box near its real width keeps a user unit close to a pixel, so
    /// one font size is legible in both.
    /// </remarks>
    private readonly record struct Box(
        double Width, double Height, double Left, double Right, double Top, double Bottom)
    {
        /// <summary>One of the three-across cards, around 420 pixels wide.</summary>
        public static Box Card => new(520, 250, 46, 512, 10, 196);

        /// <summary>A card spanning the whole grid, around 1030 pixels wide.</summary>
        public static Box Full => new(1160, 300, 58, 1150, 12, 244);
    }

    /// <summary>
    /// Draws one series over a fixed time window.
    /// </summary>
    /// <param name="points">The data, oldest first. May be empty.</param>
    /// <param name="from">Left edge of the x-axis.</param>
    /// <param name="to">Right edge of the x-axis.</param>
    /// <param name="bucketSeconds">
    /// Spacing the points were bucketed at. A gap wider than three buckets breaks the line instead
    /// of being spanned: a straight line across an outage claims measurements that were never taken.
    /// </param>
    /// <param name="format">Units and scale.</param>
    /// <param name="title">Accessible name, read by screen readers and shown on hover.</param>
    /// <param name="full">True for a card that spans the whole grid; see <see cref="Box"/>.</param>
    /// <returns>The SVG, or an empty-state note when there is nothing to draw.</returns>
    public static IHtmlContent Line(
        IReadOnlyList<Services.Point> points,
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds,
        ChartFormat format,
        string title,
        bool full = false)
    {
        var box = full ? Box.Full : Box.Card;

        if (points.Count == 0)
        {
            return new HtmlString(
                $"<p class=\"mu-chart__empty\">No measurements yet for {Escape(title)}.</p>");
        }

        var span = (to - from).TotalSeconds;
        if (span <= 0)
        {
            span = 1;
        }

        // A series that is all zeroes still needs a scale, or every point divides by nothing and
        // lands on the baseline with a "0" axis that says nothing.
        var peak = format.Ceiling ?? Math.Max(points.Max(point => point.Value), double.Epsilon);
        var top = format.Ceiling ?? NiceCeiling(peak);

        var svg = new StringBuilder(1024);
        // NOT preserveAspectRatio="none". Stretching the box to the card's width would stretch the
        // axis labels with it, and squashed text is the one thing that makes a chart look broken.
        // The box scales uniformly instead, and CSS gives it width:100% with height:auto.
        svg.Append(CultureInfo.InvariantCulture, $"<svg class=\"mu-chart\" viewBox=\"0 0 {N(box.Width)} {N(box.Height)}\" role=\"img\" aria-label=\"{Escape(title)}\">");
        svg.Append(CultureInfo.InvariantCulture, $"<title>{Escape(title)}</title>");

        AppendGrid(svg, box, top, format);
        AppendPlot(svg, box, points, from, span, top, bucketSeconds);
        AppendTimeAxis(svg, box, from, to);
        AppendHitAreas(svg, box, points, from, span, format);

        svg.Append("</svg>");
        return new HtmlString(svg.ToString());
    }

    /// <summary>
    /// Draws a single "right now" value as a proportion of its maximum - disk and memory use.
    /// </summary>
    /// <param name="used">The current value.</param>
    /// <param name="total">The maximum. A zero or negative total renders as unknown.</param>
    /// <param name="label">What the bar is measuring.</param>
    /// <param name="detail">Text shown beside the percentage, e.g. "180 GB of 270 GB".</param>
    /// <returns>The meter markup.</returns>
    public static IHtmlContent Meter(double used, double total, string label, string detail)
    {
        if (total <= 0)
        {
            return new HtmlString($"<p class=\"mu-chart__empty\">{Escape(label)} is not being measured yet.</p>");
        }

        var share = Math.Clamp(used / total, 0, 1);
        var percent = share * 100;

        // Three bands rather than a gradient: the point of the bar is to be readable at a glance
        // from across a room, and "amber" carries more than a slightly different shade of green.
        var band = percent >= 90 ? "mu-meter__fill--critical"
            : percent >= 75 ? "mu-meter__fill--warn"
            : "mu-meter__fill--ok";

        var svg = new StringBuilder(512);
        svg.Append("<div class=\"mu-meter\">");
        svg.Append(CultureInfo.InvariantCulture, $"<div class=\"mu-meter__head\"><span>{Escape(label)}</span><span class=\"mu-meter__value\">{N(percent, 1)}%</span></div>");
        svg.Append(CultureInfo.InvariantCulture, $"<svg class=\"mu-meter__bar\" viewBox=\"0 0 100 8\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"{Escape(label)} {N(percent, 1)} percent\">");
        svg.Append(CultureInfo.InvariantCulture, $"<title>{Escape(label)}: {N(percent, 1)}% - {Escape(detail)}</title>");
        svg.Append("<rect class=\"mu-meter__track\" x=\"0\" y=\"0\" width=\"100\" height=\"8\" rx=\"2\" />");
        svg.Append(CultureInfo.InvariantCulture, $"<rect class=\"mu-meter__fill {band}\" x=\"0\" y=\"0\" width=\"{N(share * 100, 3)}\" height=\"8\" rx=\"2\" />");
        svg.Append("</svg>");
        svg.Append(CultureInfo.InvariantCulture, $"<p class=\"mu-meter__detail\">{Escape(detail)}</p>");
        svg.Append("</div>");
        return new HtmlString(svg.ToString());
    }

    /// <summary>Horizontal gridlines with their values down the left.</summary>
    private static void AppendGrid(StringBuilder svg, Box box, double top, ChartFormat format)
    {
        for (var i = 0; i <= GridLines; i++)
        {
            var value = top * i / GridLines;
            var y = box.Bottom - ((box.Bottom - box.Top) * i / GridLines);

            svg.Append(CultureInfo.InvariantCulture,
                $"<line class=\"mu-chart__grid\" x1=\"{N(box.Left)}\" y1=\"{N(y)}\" x2=\"{N(box.Right)}\" y2=\"{N(y)}\" />");
            svg.Append(CultureInfo.InvariantCulture,
                $"<text class=\"mu-chart__ylabel\" x=\"{N(box.Left - 8)}\" y=\"{N(y + 4)}\" text-anchor=\"end\">{Escape(Label(value, format))}</text>");
        }
    }

    /// <summary>The filled area and the line on top of it, broken across gaps.</summary>
    private static void AppendPlot(
        StringBuilder svg,
        Box box,
        IReadOnlyList<Services.Point> points,
        DateTimeOffset from,
        double span,
        double top,
        int bucketSeconds)
    {
        // Three buckets of silence is a gap. One missed scrape is noise and is bridged; a restart,
        // a full disk or a stopped shipper is a hole and is shown as one.
        var maxStep = Math.Max(bucketSeconds * 3, 1);

        var line = new StringBuilder(512);
        var area = new StringBuilder(512);
        var open = false;
        double lastX = 0;
        double startX = 0;

        for (var i = 0; i < points.Count; i++)
        {
            var x = X(box, points[i].At, from, span);
            var y = Y(box, points[i].Value, top);
            var broken = i > 0 && (points[i].At - points[i - 1].At).TotalSeconds > maxStep;

            if (!open || broken)
            {
                if (open)
                {
                    CloseArea(area, box, lastX, startX);
                }

                line.Append(CultureInfo.InvariantCulture, $"M{N(x)},{N(y)}");
                area.Append(CultureInfo.InvariantCulture, $"M{N(x)},{N(box.Bottom)}L{N(x)},{N(y)}");
                startX = x;
                open = true;
            }
            else
            {
                line.Append(CultureInfo.InvariantCulture, $"L{N(x)},{N(y)}");
                area.Append(CultureInfo.InvariantCulture, $"L{N(x)},{N(y)}");
            }

            lastX = x;
        }

        if (open)
        {
            CloseArea(area, box, lastX, startX);
        }

        svg.Append(CultureInfo.InvariantCulture, $"<path class=\"mu-chart__area\" d=\"{area}\" />");
        svg.Append(CultureInfo.InvariantCulture, $"<path class=\"mu-chart__line\" d=\"{line}\" fill=\"none\" vector-effect=\"non-scaling-stroke\" />");

        // A single point has no line to draw, and would otherwise render as nothing at all.
        if (points.Count == 1)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<circle class=\"mu-chart__dot\" cx=\"{N(X(box, points[0].At, from, span))}\" cy=\"{N(Y(box, points[0].Value, top))}\" r=\"3\" />");
        }
    }

    /// <summary>Drops the area back to the baseline and closes it.</summary>
    private static void CloseArea(StringBuilder area, Box box, double lastX, double startX)
    {
        area.Append(CultureInfo.InvariantCulture, $"L{N(lastX)},{N(box.Bottom)}L{N(startX)},{N(box.Bottom)}Z");
    }

    /// <summary>Five clock labels along the bottom.</summary>
    private static void AppendTimeAxis(StringBuilder svg, Box box, DateTimeOffset from, DateTimeOffset to)
    {
        const int Ticks = 4;
        var window = to - from;

        // Over a day the hour alone is ambiguous, so the date joins it.
        var pattern = window.TotalHours > 24 ? "dd MMM" : "HH:mm";

        for (var i = 0; i <= Ticks; i++)
        {
            var at = from + (window * i / Ticks);
            var x = box.Left + ((box.Right - box.Left) * i / Ticks);
            var anchor = i == 0 ? "start" : i == Ticks ? "end" : "middle";

            svg.Append(CultureInfo.InvariantCulture,
                $"<text class=\"mu-chart__xlabel\" x=\"{N(x)}\" y=\"{N(box.Height - 14)}\" text-anchor=\"{anchor}\">{Escape(at.ToLocalTime().ToString(pattern, CultureInfo.InvariantCulture))}</text>");
        }
    }

    /// <summary>
    /// One transparent column per point, carrying a &lt;title&gt;.
    /// </summary>
    /// <remarks>
    /// This is the whole hover story on a site that cannot run script. The browser shows the title
    /// of whatever the pointer is over, so a column per point gives a readout at every x position
    /// with no event handlers, no library and nothing for the CSP to block.
    /// </remarks>
    private static void AppendHitAreas(
        StringBuilder svg,
        Box box,
        IReadOnlyList<Services.Point> points,
        DateTimeOffset from,
        double span,
        ChartFormat format)
    {
        var columnWidth = (box.Right - box.Left) / Math.Max(points.Count, 1);

        foreach (var point in points)
        {
            var x = X(box, point.At, from, span) - (columnWidth / 2);
            var when = point.At.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);

            svg.Append(CultureInfo.InvariantCulture,
                $"<rect class=\"mu-chart__hit\" x=\"{N(x)}\" y=\"{N(box.Top)}\" width=\"{N(columnWidth)}\" height=\"{N(box.Bottom - box.Top)}\">");
            svg.Append(CultureInfo.InvariantCulture,
                $"<title>{Escape(when)} - {Escape(Label(point.Value, format))}</title></rect>");
        }
    }

    /// <summary>Where a timestamp sits horizontally.</summary>
    private static double X(Box box, DateTimeOffset at, DateTimeOffset from, double span)
    {
        var share = Math.Clamp((at - from).TotalSeconds / span, 0, 1);
        return box.Left + ((box.Right - box.Left) * share);
    }

    /// <summary>Where a value sits vertically. SVG y grows downwards, so the scale is inverted.</summary>
    private static double Y(Box box, double value, double top)
    {
        var share = top <= 0 ? 0 : Math.Clamp(value / top, 0, 1);
        return box.Bottom - ((box.Bottom - box.Top) * share);
    }

    /// <summary>
    /// Rounds a scale up to something a person would choose - 1, 2 or 5 times a power of ten.
    /// </summary>
    /// <remarks>
    /// An axis topping out at 3.7194 is technically correct and unreadable. This is why a chart of
    /// a value that peaks at 37 is drawn against 50 rather than against itself.
    /// </remarks>
    private static double NiceCeiling(double peak)
    {
        if (peak <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        var normalised = peak / magnitude;
        var step = normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 5 ? 5 : 10;
        return step * magnitude;
    }

    /// <summary>Formats a value with its unit.</summary>
    private static string Label(double value, ChartFormat format) =>
        N(value, format.Decimals) + format.Unit;

    /// <summary>A number safe to put in an SVG attribute, in any locale.</summary>
    private static string N(double value, int decimals = 2) =>
        Math.Round(value, decimals).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Escapes text for HTML, since chart titles carry device and level names.</summary>
    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);
}
