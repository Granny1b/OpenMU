using Microsoft.Extensions.Caching.Memory;
using MuSite.Game;

namespace MuSite.Auth;

/// <summary>What revalidation resolved for a session.</summary>
/// <param name="IsValid">False when the session is gone, revoked, expired, or the account is banned.</param>
/// <param name="AccountId">The account.</param>
/// <param name="LoginName">The login name in its stored capitalisation.</param>
/// <param name="AccountState">The raw data."Account"."State".</param>
/// <param name="Role">The website role the account resolves to right now.</param>
public sealed record ResolvedSession(bool IsValid, Guid AccountId, string LoginName, int AccountState, SiteRole Role);

/// <summary>
/// Resolves a cookie's session id to a live identity on every request, with a short cache.
///
/// Session validity, account state and role are resolved and cached TOGETHER. Caching them apart
/// would let a request see a valid session with a stale role, which is the combination that matters:
/// the role is what opens /admin.
///
/// The 60-second window is the lag on a ban or a role change taking effect. Every administrative
/// action that should be immediate calls <see cref="Evict"/> itself, so the window only ever applies
/// to changes made outside the website - in the OpenMU admin panel, or straight in the database.
/// </summary>
public sealed class SessionState(SessionStore sessions, GameAccount accounts, RoleResolver roles, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static readonly ResolvedSession Invalid = new(false, Guid.Empty, string.Empty, 0, SiteRole.None);

    /// <summary>Resolves a session id, from cache when it is fresh.</summary>
    public async Task<ResolvedSession> ResolveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<ResolvedSession>(Key(sessionId), out var hit) && hit is not null)
        {
            return hit;
        }

        var resolved = await this.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // A negative result is cached too, briefly: after a ban, the browser of the banned player
        // keeps sending the same dead cookie on every request, and each one would otherwise be two
        // database round trips.
        cache.Set(Key(sessionId), resolved, Ttl);
        return resolved;
    }

    /// <summary>
    /// Drops a session from the cache so the next request re-reads it. Called synchronously by every
    /// ban, unban and password reset, which is what makes those take effect at once rather than
    /// within a minute.
    /// </summary>
    public void Evict(Guid sessionId) => cache.Remove(Key(sessionId));

    private static string Key(Guid sessionId) => $"session:{sessionId}";

    private async Task<ResolvedSession> ReadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.ValidateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Invalid;
        }

        var state = await accounts.GetStateAsync(session.AccountId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            // The account was deleted out from under a live session.
            return Invalid;
        }

        if (GameEnums.AccountState.IsBanned(state.Value))
        {
            return Invalid;
        }

        return new ResolvedSession(
            true,
            session.AccountId,
            session.LoginName,
            state.Value,
            roles.Resolve(session.LoginName, state.Value));
    }
}
