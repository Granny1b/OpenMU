namespace MuSite.Auth;

/// <summary>Names of the rate limiting policies, so the page attributes and the setup cannot drift.</summary>
public static class RateLimitPolicies
{
    /// <summary>Account creation, per client address.</summary>
    public const string Register = "register";

    /// <summary>Sign-in attempts, per client address.</summary>
    public const string Login = "login";
}
