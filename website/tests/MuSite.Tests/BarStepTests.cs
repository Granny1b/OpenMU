using MuSite.Services;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Bar widths are step classes rather than inline styles, so that style-src in the CSP can stay
/// free of 'unsafe-inline'. A class the stylesheet does not define renders as a zero-width bar, so
/// the mapping has to land on one of the 21 defined steps for every input.
/// </summary>
public class BarStepTests
{
    [Theory]
    [InlineData(0, 100, "mu-bar__fill--p0")]
    [InlineData(50, 100, "mu-bar__fill--p50")]
    [InlineData(100, 100, "mu-bar__fill--p100")]
    [InlineData(1, 100, "mu-bar__fill--p0")]      // rounds to the nearest 5
    [InlineData(3, 100, "mu-bar__fill--p5")]
    [InlineData(18400, 18400, "mu-bar__fill--p100")]
    [InlineData(9850, 18400, "mu-bar__fill--p55")]
    public void MapsToADefinedStep(double value, double max, string expected)
        => Assert.Equal(expected, BarStep.For(value, max));

    [Theory]
    [InlineData(10, 0)]      // a character with no points at all
    [InlineData(-5, 100)]    // never observed, but must not produce a negative class
    [InlineData(0, 0)]
    public void DegenerateInputsAreEmptyNotBroken(double value, double max)
        => Assert.Equal("mu-bar__fill--p0", BarStep.For(value, max));

    [Fact]
    public void ValueAboveMaxClampsInsteadOfOverflowing()
        => Assert.Equal("mu-bar__fill--p100", BarStep.For(500, 100));

    [Fact]
    public void EveryStepItCanProduceIsAMultipleOfFive()
    {
        for (var i = 0; i <= 1000; i++)
        {
            var css = BarStep.For(i, 1000);
            var step = int.Parse(css["mu-bar__fill--p".Length..]);
            Assert.True(step % 5 == 0 && step is >= 0 and <= 100, $"{css} is not one of the 21 defined steps");
        }
    }
}
