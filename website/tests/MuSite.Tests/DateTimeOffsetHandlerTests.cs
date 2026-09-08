using MuSite.Data;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The conversion the handler performs, without a database. SiteQueriesTests proves it is wired up;
/// this proves it is correct, including the branch a timestamptz column will never produce but a
/// later `timestamp without time zone` column would.
/// </summary>
public sealed class DateTimeOffsetHandlerTests
{
    private static readonly DateTimeOffsetHandler Handler = new();

    [Fact]
    public void AUtcDateTimeKeepsItsInstantAndGainsAZeroOffset()
    {
        var utc = new DateTime(2026, 9, 8, 11, 30, 15, DateTimeKind.Utc);

        var converted = Handler.Parse(utc);

        Assert.Equal(TimeSpan.Zero, converted.Offset);
        Assert.Equal(utc, converted.UtcDateTime);
    }

    [Fact]
    public void AnUnspecifiedDateTimeIsReadAsUtcRatherThanShifted()
    {
        // Reinterpreting rather than converting: the schema stores UTC throughout, so the wall-clock
        // reading is already UTC and must not be moved by the machine's zone.
        var unspecified = new DateTime(2026, 9, 8, 11, 30, 15, DateTimeKind.Unspecified);

        var converted = Handler.Parse(unspecified);

        Assert.Equal(TimeSpan.Zero, converted.Offset);
        Assert.Equal(unspecified, converted.UtcDateTime);
    }

    [Fact]
    public void ALocalDateTimeIsConvertedRatherThanReinterpreted()
    {
        // The opposite rule: a local reading names a different instant, so it is converted. Asserted
        // against the same conversion the framework performs, so the test holds in any time zone -
        // including UTC, where the two are identical.
        var local = new DateTime(2026, 9, 8, 11, 30, 15, DateTimeKind.Local);

        var converted = Handler.Parse(local);

        Assert.Equal(TimeSpan.Zero, converted.Offset);
        Assert.Equal(local.ToUniversalTime(), converted.UtcDateTime);
    }

    [Fact]
    public void ADateTimeOffsetPassesThroughUnchanged()
    {
        var original = new DateTimeOffset(2026, 9, 8, 11, 30, 15, TimeSpan.FromHours(2));

        Assert.Equal(original, Handler.Parse(original));
    }

    [Fact]
    public void AnythingElseThrowsRatherThanGuessing()
    {
        Assert.Throws<InvalidCastException>(() => Handler.Parse("2026-09-08"));
    }
}
