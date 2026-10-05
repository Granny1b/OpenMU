using Microsoft.Extensions.Options;
using MuSite.Game;

namespace MuSite.Auth;

/// <summary>
/// The one place the admin gating rule lives.
///
/// The owner asked for "admin panel based on the account status", and data."Account"."State" is the
/// only role-shaped field a game account has - admin."AdminUser" is the panel's own table, in a
/// schema this site's database roles cannot reach.
///
/// State alone is NOT sufficient, and the allowlist is not a nicety. In the OpenMU admin panel,
/// Accounts.razor and EditAccount.razor carry no [Authorize(Policy = AdminPolicies.Administrator)]
/// - only AdminUsers.razor, ApiKeys.razor, LogFiles.razor, Plugins.razor, Setup.razor and
/// Updates.razor do. They fall back to the blanket [Authorize] at _Imports.razor:27, whose default
/// policy is satisfied by any authenticated panel user (AdminAccessRequirement handler). So a panel
/// user holding the Viewer role - documented as unable to change anything - can set any account to
/// State = 2. Gating on State alone would let that Viewer mint website administrators who reset
/// player passwords.
///
/// Fails closed: an empty allowlist means zero admins and /admin returns 404 for everybody.
/// </summary>
public sealed class RoleResolver(IOptionsMonitor<SiteOptions> options)
{
    /// <summary>
    /// Resolves the website role for an account. <paramref name="accountState"/> is the raw integer
    /// from data."Account"."State".
    /// </summary>
    public SiteRole Resolve(string? loginName, int accountState)
    {
        if (string.IsNullOrWhiteSpace(loginName))
        {
            return SiteRole.None;
        }

        // Hard deny, before anything else: three of the seeded accounts ship as GameMaster with a
        // password equal to their name.
        if (SeedAccounts.IsSeedName(loginName))
        {
            return SiteRole.None;
        }

        if (GameEnums.AccountState.IsBanned(accountState))
        {
            return SiteRole.None;
        }

        if (!GameEnums.AccountState.IsGameMaster(accountState))
        {
            return SiteRole.None;
        }

        var current = options.CurrentValue;

        // Ordinal, not OrdinalIgnoreCase: data."Account"."LoginName" carries a case-sensitive unique
        // index, so 'Owner' and 'owner' can be two different accounts. Matching loosely here would
        // hand the allowlist entry to whichever of them registered the case variant.
        // Empty entries are filtered: the compose file sets MUSITE_ADMINS__0 and __1 unconditionally,
        // so an unconfigured slot arrives as "". It could never match a real login name, but an
        // allowlist that silently contains a blank is the kind of thing that gets "simplified" into
        // a real hole later.
        var allowed = current.Admins.Where(name => !string.IsNullOrWhiteSpace(name));
        if (!allowed.Contains(loginName, StringComparer.Ordinal))
        {
            return SiteRole.None;
        }

        return string.Equals(loginName, current.Owner, StringComparison.Ordinal)
            ? SiteRole.Owner
            : SiteRole.Admin;
    }

    /// <summary>
    /// The role an account is PROTECTED as when it is the TARGET of an administrative action, from
    /// the configuration alone - the owner name, or a name on the allowlist.
    ///
    /// Deliberately independent of the account state, unlike <see cref="Resolve"/>. A banned
    /// administrator (or the owner) resolves to <see cref="SiteRole.None"/> there, so a protection
    /// based on it would let any administrator lift the owner's ban on a rogue colleague, or act on
    /// the owner whenever the owner's account is not a GameMaster at that moment.
    /// Matched with Ordinal for the same reason as <see cref="Resolve"/>: a case variant is another
    /// account. Seeded accounts are never protected - they are never administrators either.
    /// </summary>
    public SiteRole ResolveProtection(string? loginName)
    {
        if (string.IsNullOrWhiteSpace(loginName) || SeedAccounts.IsSeedName(loginName))
        {
            return SiteRole.None;
        }

        var current = options.CurrentValue;
        if (!string.IsNullOrWhiteSpace(current.Owner) && string.Equals(loginName, current.Owner, StringComparison.Ordinal))
        {
            return SiteRole.Owner;
        }

        return current.Admins.Where(name => !string.IsNullOrWhiteSpace(name)).Contains(loginName, StringComparer.Ordinal)
            ? SiteRole.Admin
            : SiteRole.None;
    }
}
