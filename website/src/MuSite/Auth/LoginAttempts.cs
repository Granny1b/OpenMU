using Microsoft.Extensions.Caching.Memory;

namespace MuSite.Auth;

/// <summary>
/// Per-account throttling of failed sign-ins, on top of the per-IP limiter.
///
/// The counter increments on FAILURE ONLY and is cleared by a success. A counter that also counted
/// successes would let anyone lock a named player out of their own account by typing a wrong
/// password a few times - the lockout becomes the attack rather than the defence.
///
/// In memory on purpose: a restart clearing the counters is an acceptable cost, and the alternative
/// is a database write on every failed sign-in, which is a denial-of-service amplifier of its own.
/// </summary>
public sealed class LoginAttempts(IMemoryCache cache)
{
    private const int MaxFailures = 8;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>True when this login name has failed too often recently.</summary>
    public bool IsLockedOut(string loginName)
        => cache.TryGetValue<int>(Key(loginName), out var failures) && failures >= MaxFailures;

    /// <summary>Records a failed attempt.</summary>
    public void RecordFailure(string loginName)
    {
        var failures = cache.TryGetValue<int>(Key(loginName), out var existing) ? existing : 0;
        cache.Set(Key(loginName), failures + 1, Window);
    }

    /// <summary>Clears the counter after a correct password.</summary>
    public void RecordSuccess(string loginName) => cache.Remove(Key(loginName));

    // Case-insensitive: an attacker must not get a fresh budget by varying capitalisation.
    private static string Key(string loginName) => $"login-failures:{loginName.ToLowerInvariant()}";
}
