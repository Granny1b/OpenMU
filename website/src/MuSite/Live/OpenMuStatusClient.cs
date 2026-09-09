using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MuSite.Live;

/// <summary>
/// Asks OpenMU's public API how many players are online.
///
/// WHY THIS EXISTS ALONGSIDE THE CONNECT SERVER PROBE
///
/// The connect server reports load as whole percent of the player cap, so at OpenMU's default cap
/// of 1000 it moves ten players at a time and reads zero below ten. This endpoint returns the exact
/// figure. It costs an API key, which the connect server probe does not, so it is optional: with no
/// key configured the site still charts load and availability, just not an exact count.
///
/// WHAT THE KEY CAN DO
///
/// The endpoint requires the Viewer policy. A Viewer key can read server status and ask whether a
/// named account is online; it CANNOT send global messages, which needs Operator. The response also
/// carries the names of everyone online - this client reads the count and deliberately drops the
/// list, so the names are never stored or logged by the website.
/// </summary>
public sealed class OpenMuStatusClient(
    HttpClient http, IOptionsMonitor<SiteOptions> options, ILogger<OpenMuStatusClient> logger)
{
    /// <summary>Whether a key and URL are configured at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.CurrentValue.StatusUrl)
        && !string.IsNullOrWhiteSpace(options.CurrentValue.StatusApiKey);

    /// <summary>
    /// Gets the number of players currently online, or null when that could not be established.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exact player count, or null.</returns>
    public async Task<int?> GetPlayerCountAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!this.IsConfigured)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.StatusUrl);
            request.Headers.TryAddWithoutValidation("X-Api-Key", settings.StatusApiKey);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The status code is safe to log; the key never is.
                logger.LogWarning(
                    "OpenMU status API answered {Status}. A 401 means the API key is not accepted, a 404 means this build has no /api/status.",
                    (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReadPlayerCount(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Could not read the player count from OpenMU's status API.");
            return null;
        }
    }

    /// <summary>
    /// Pulls the player count out of the response body.
    /// </summary>
    /// <remarks>
    /// The controller returns <c>Ok(JsonSerializer.Serialize(item))</c> - an already serialised
    /// string handed to a formatter that may serialise it AGAIN. So the body is either the object
    /// or a JSON string containing the object, depending on the negotiated formatter, and which one
    /// arrives is not something this side can pin down. Both are accepted.
    /// </remarks>
    /// <param name="body">The response body.</param>
    /// <returns>The player count, or null when the body does not carry one.</returns>
    /// <remarks>Public so the double-encoding cases can be tested without a live game server.</remarks>
    public static int? ReadPlayerCount(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.String)
        {
            // Double encoded: the payload is inside the string.
            var inner = root.GetString();
            return string.IsNullOrWhiteSpace(inner) ? null : ReadPlayerCount(inner);
        }

        // The ValueKind check is not redundant: TryGetInt32 THROWS InvalidOperationException when
        // the element is not a number, rather than returning false as its name suggests. Without
        // it, a null or a string in that field escapes the JsonException the caller catches.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("players", out var players)
            && players.ValueKind == JsonValueKind.Number
            && players.TryGetInt32(out var count))
        {
            return count;
        }

        return null;
    }
}
