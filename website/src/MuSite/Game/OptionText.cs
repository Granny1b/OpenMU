using System.Globalization;
using MuSite.Data;

namespace MuSite.Game;

/// <summary>
/// Turns a configured item option into a sentence a GM can read.
///
/// The rule here is that nothing is invented. An option in the database is a target attribute, a
/// number and an aggregate type, and that is all this prints - "+7 Attack Speed Any", "Physical
/// Base Dmg × 1.02". The percentage in brackets after a multiplier is the same number restated, not
/// an interpretation. Anything that would need knowledge the configuration does not carry - whether
/// a raw 0.1 means ten percent or ten points, say - is left as the number, because a confident
/// wrong label on a GM tool is worse than a plain one.
/// </summary>
public static class OptionText
{
    /// <summary>AggregateType, from src/AttributeSystem/AggregateType.cs.</summary>
    private const int Multiplicate = 1;
    private const int AddFinal = 2;
    private const int Maximum = 3;

    /// <summary>What one excellent option does.</summary>
    public static string Describe(ExcellentOptionRow option)
    {
        var attribute = option.Attribute ?? "an unnamed attribute";

        // Excellent option 5 of every set is a relationship rather than a constant: the bonus is
        // computed from another stat, so the stored Value is 0 and printing it would read "+0".
        if (option.ScalesWith is { } source)
        {
            return $"{attribute} + {source} × {Number(option.ScalesWithFactor ?? 0m)}";
        }

        return Describe(attribute, option.Value, option.AggregateType);
    }

    /// <summary>What one level of an ordinary option does.</summary>
    public static string Describe(OptionLevelRow level)
        => Describe(level.Attribute, level.Value, level.AggregateType);

    /// <summary>What an attribute, a value and an aggregate type mean together.</summary>
    public static string Describe(string attribute, decimal value, int aggregateType)
        => aggregateType switch
        {
            Multiplicate => $"{attribute} × {Number(value)}{Percentage(value)}",
            AddFinal => $"+{Number(value)} {attribute} (applied last)",
            Maximum => $"{attribute} capped at {Number(value)}",
            _ => $"{(value < 0 ? "−" : "+")}{Number(Math.Abs(value))} {attribute}",
        };

    /// <summary>A number without trailing zeroes: 1.02, 7, 0.05.</summary>
    public static string Number(decimal value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// A multiplier restated as the change it makes: 1.02 is +2%, 0.95 is -5%. Only for multipliers
    /// near 1, where that restatement is arithmetic rather than interpretation.
    /// </summary>
    private static string Percentage(decimal multiplier)
    {
        if (multiplier <= 0m || multiplier > 2m)
        {
            return string.Empty;
        }

        var change = Math.Round((multiplier - 1m) * 100m, 2);
        if (change == 0m)
        {
            return string.Empty;
        }

        return $" ({(change > 0 ? "+" : "−")}{Number(Math.Abs(change))}%)";
    }
}
