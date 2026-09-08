namespace MuSite.Services;

/// <summary>
/// Maps a proportion onto one of the 21 width classes in mu.css.
///
/// The site never writes a style attribute for a bar width. That is what lets the CSP ship without
/// 'unsafe-inline' in style-src: with inline styles allowed, a stored-XSS payload gains the ability
/// to restyle the page, and the whole point of the strict policy is lost for a cosmetic convenience.
/// </summary>
public static class BarStep
{
    /// <summary>Returns the fill class for <paramref name="value"/> out of <paramref name="max"/>.</summary>
    public static string For(double value, double max)
    {
        if (max <= 0 || value <= 0)
        {
            return "mu-bar__fill--p0";
        }

        var percent = Math.Clamp(value / max * 100d, 0d, 100d);
        var step = (int)Math.Round(percent / 5d, MidpointRounding.AwayFromZero) * 5;
        return $"mu-bar__fill--p{step}";
    }
}
