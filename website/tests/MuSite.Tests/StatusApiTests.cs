using MuSite.Live;
using Xunit;

namespace MuSite.Tests;

/// <summary>
/// Pins how the player count is read out of OpenMU's /api/status response.
///
/// The controller does <c>Ok(JsonSerializer.Serialize(item))</c> - it hands an ALREADY SERIALISED
/// string to a formatter that is entitled to serialise it again. Which of the two shapes actually
/// arrives depends on content negotiation, and is not something the website can pin down from its
/// side, so both are accepted. These tests are what stop a future simplification from quietly
/// dropping one of them and turning the players graph into a flat line of nulls.
/// </summary>
public sealed class StatusApiTests
{
    [Fact]
    public void ItReadsAPlainObject()
    {
        Assert.Equal(7, OpenMuStatusClient.ReadPlayerCount(
            """{"state":"Online","players":7,"playersList":["a","b"]}"""));
    }

    [Fact]
    public void ItReadsAnObjectThatWasSerialisedTwice()
    {
        // The body is a JSON string whose CONTENT is the JSON object.
        Assert.Equal(7, OpenMuStatusClient.ReadPlayerCount(
            "\"{\\\"state\\\":\\\"Online\\\",\\\"players\\\":7,\\\"playersList\\\":[]}\""));
    }

    [Fact]
    public void ZeroPlayersIsAValueAndNotAnAbsence()
    {
        // Must be 0, not null: an empty server is a fact worth drawing, and null would leave a gap
        // in the graph that reads as "the site could not tell".
        Assert.Equal(0, OpenMuStatusClient.ReadPlayerCount("""{"state":"Online","players":0}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"state":"Online"}""")]
    [InlineData("""{"players":"lots"}""")]
    [InlineData("""{"players":null}""")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    public void AnythingElseIsUnknownRatherThanZero(string body)
    {
        Assert.Null(OpenMuStatusClient.ReadPlayerCount(body));
    }

    [Fact]
    public void MalformedJsonThrowsSoTheCallerCanLogIt()
    {
        // The caller catches JsonException and logs it. Swallowing it here would hide a server that
        // started answering with an HTML error page.
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => OpenMuStatusClient.ReadPlayerCount("<html>502 Bad Gateway</html>"));
    }
}
