using MuSite.Data;
using MuSite.Game;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// How an option reads on the /item builder.
///
/// The whole reason the builder exists is that "ex=44" tells a GM nothing, so the text that
/// replaces it has to be right. These need no database: they are the formatting rules, and the
/// numbers going in are the ones ExcellentOptions.cs actually seeds.
/// </summary>
public sealed class OptionTextTests
{
    private static ExcellentOptionRow Excellent(
        decimal value, int aggregateType, string? scalesWith = null, decimal? factor = null)
        => new(3, 4, "Attack Speed Any", value, aggregateType, scalesWith, factor);

    [Fact]
    public void ARawBonusReadsAsAnAddition()
    {
        Assert.Equal("+7 Attack Speed Any", OptionText.Describe(Excellent(7m, 0)));
    }

    [Fact]
    public void AMultiplierIsRestatedAsThePercentageItChanges()
    {
        // 1.02 is the excellent damage option. "x 1.02" alone is correct but hard to compare.
        Assert.Equal("Attack Speed Any × 1.02 (+2%)", OptionText.Describe(Excellent(1.02m, 1)));
    }

    [Fact]
    public void AMultiplierBelowOneReadsAsALoss()
    {
        Assert.Equal("Attack Speed Any × 0.95 (−5%)", OptionText.Describe(Excellent(0.95m, 1)));
    }

    [Fact]
    public void AnOptionThatScalesWithAnotherStatSaysSoInsteadOfPrintingZero()
    {
        // Excellent option 5 stores no constant. Printing its value would read "+0", which is the
        // one wrong answer that looks like a right one.
        var described = OptionText.Describe(Excellent(0m, 0, "Total Level", 0.05m));

        Assert.Equal("Attack Speed Any + Total Level × 0.05", described);
        Assert.DoesNotContain("+0 ", described, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionLevelReadsAsItsBonus()
    {
        var level = new OptionLevelRow(Guid.NewGuid(), "Physical Base Dmg", 3, 12m, 0);

        Assert.Equal("+12 Physical Base Dmg", OptionText.Describe(level));
    }

    [Theory]
    [InlineData(7.0, "7")]
    [InlineData(1.02, "1.02")]
    [InlineData(0.05, "0.05")]
    [InlineData(0.125, "0.125")]
    public void NumbersLoseTheirTrailingZeroes(double value, string expected)
    {
        // PostgreSQL's numeric keeps the scale it was given; "+7.0000 Attack Speed" is noise.
        Assert.Equal(expected, OptionText.Number((decimal)value));
    }

    [Fact]
    public void AMultiplierThatChangesNothingGetsNoPercentage()
    {
        Assert.Equal("Attack Speed Any × 1", OptionText.Describe(Excellent(1m, 1)));
    }
}
