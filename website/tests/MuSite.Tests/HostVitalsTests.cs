using MuSite.Pages.Admin;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The formatting behind /admin/metrics/live.
///
/// The page reloads itself every few seconds, so anything that throws here throws several times a
/// minute for as long as an admin leaves the tab open. Every reader in
/// <see cref="MuSite.Live.HostVitals"/> can return null - there is no /proc on a developer's
/// machine, MemAvailable is missing on kernels before 3.14, and the first CPU reading has no
/// previous sample to difference against - so null has to render as a dash rather than an
/// exception or a fabricated zero.
/// </summary>
public sealed class HostVitalsTests
{
    [Fact]
    public void AMissingSizeRendersAsADashRatherThanZero()
        => Assert.Equal("—", MetricsLiveModel.Gibibytes(null));

    [Theory]
    [InlineData(0L, "0.0 GiB")]
    [InlineData(1024L * 1024 * 1024, "1.0 GiB")]
    [InlineData(8L * 1024 * 1024 * 1024, "8.0 GiB")]
    [InlineData(1610612736L, "1.5 GiB")]
    public void SizesRenderAsGibibytes(long bytes, string expected)
        => Assert.Equal(expected, MetricsLiveModel.Gibibytes(bytes));

    [Fact]
    public void AReadingThatNeverHappenedSaysSoInsteadOfClaimingItWasJustNow()
        => Assert.Equal("never", MetricsLiveModel.Age(null));

    [Fact]
    public void ARecentReadingIsReportedInSeconds()
        => Assert.Equal("12s ago", MetricsLiveModel.Age(DateTimeOffset.UtcNow.AddSeconds(-12)));

    [Fact]
    public void AnOldReadingSwitchesToMinutes()
        => Assert.Equal("5m ago", MetricsLiveModel.Age(DateTimeOffset.UtcNow.AddMinutes(-5)));

    [Fact]
    public void AReadingFromTheFutureIsClampedRatherThanShownAsNegative()
    {
        // Clock adjustments happen, and "-3s ago" reads like a bug in the page rather than in the
        // clock. Anything ahead of now is reported as the present moment.
        Assert.Equal("0s ago", MetricsLiveModel.Age(DateTimeOffset.UtcNow.AddSeconds(30)));
    }
}
