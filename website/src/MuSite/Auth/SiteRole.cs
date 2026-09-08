namespace MuSite.Auth;

/// <summary>What a signed-in account may do on the website.</summary>
public enum SiteRole
{
    /// <summary>An ordinary player, or an account that failed any admin condition.</summary>
    None = 0,

    /// <summary>May view the admin area, search accounts, ban, unban and write news.</summary>
    Admin = 1,

    /// <summary>Admin, plus the destructive actions - currently password reset.</summary>
    Owner = 2,
}
