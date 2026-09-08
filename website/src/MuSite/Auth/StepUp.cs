using Microsoft.Extensions.Caching.Memory;
using MuSite.Game;
using MuSite.Services;

namespace MuSite.Auth;

/// <summary>
/// Re-asks an administrator for their own password before a destructive action.
///
/// The session cookie proves who signed in, possibly two weeks ago on a laptop now sitting unlocked
/// in a room full of people. Banning a player and resetting a password are the two actions where
/// that gap matters, so both re-verify the acting administrator's game password against
/// mu_web_auth - the same hash the game checks.
///
/// Failures are counted in memory and always written to the audit log. The in-memory counter is
/// cleared by a restart; the audit rows are not, and they are the record that actually matters when
/// working out whether somebody was trying doors.
/// </summary>
public sealed class StepUp(GameAccount accounts, AuditLog audit, IMemoryCache cache, ILogger<StepUp> logger)
{
    private const int MaxFailures = 5;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>True when this administrator has failed confirmation too often to try again yet.</summary>
    public bool IsLockedOut(string loginName)
        => cache.TryGetValue<int>(Key(loginName), out var failures) && failures >= MaxFailures;

    /// <summary>
    /// Verifies the administrator's own password. Records every failure, in memory and permanently.
    /// </summary>
    public async Task<bool> ConfirmAsync(string loginName, string password, string? ip, string action, CancellationToken cancellationToken)
    {
        if (this.IsLockedOut(loginName))
        {
            return false;
        }

        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var result = await accounts.LoginAsync(loginName, password, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            cache.Remove(Key(loginName));
            return true;
        }

        var failures = (cache.TryGetValue<int>(Key(loginName), out var existing) ? existing : 0) + 1;
        cache.Set(Key(loginName), failures, Window);

        await audit.WriteAsync("stepup.failed", loginName, null, null, ip,
            $"wrong password confirming {action} (attempt {failures})", cancellationToken).ConfigureAwait(false);

        logger.LogWarning("Step-up confirmation failed for {Admin} on {Action} (attempt {Failures}).", loginName, action, failures);
        return false;
    }

    private static string Key(string loginName) => $"stepup-failures:{loginName.ToLowerInvariant()}";
}
