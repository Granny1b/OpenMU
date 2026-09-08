using Dapper;
using MuSite.Data;

namespace MuSite.Services;

/// <summary>
/// The handful of switches an owner flips at runtime, in the site's own database so they survive a
/// restart and are visible to every instance.
/// </summary>
public sealed class SiteSettings(SiteDataSources sources)
{
    /// <summary>Whether /register accepts new accounts.</summary>
    public const string RegistrationOpen = "registration_open";

    /// <summary>Whether the public pages show a maintenance notice.</summary>
    public const string Maintenance = "maintenance";

    /// <summary>Reads a boolean setting, defaulting when the row is missing or unreadable.</summary>
    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var value = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT value FROM site_setting WHERE key = @key",
                new { key },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return value is null ? fallback : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A settings read must never take a page down; the caller's default stands.
            return fallback;
        }
    }

    /// <summary>Writes a boolean setting.</summary>
    public async Task SetBoolAsync(string key, bool value, CancellationToken cancellationToken)
    {
        await using var connection = await sources.Site.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO site_setting (key, value, updated_at) VALUES (@key, @value, now())
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = now()
            """,
            new { key, value = value ? "true" : "false" },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
