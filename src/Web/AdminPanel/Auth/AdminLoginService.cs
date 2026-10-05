// <copyright file="AdminLoginService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Auth;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Persistence.AdminAuth;

/// <summary>
/// The result status of a login attempt.
/// </summary>
public enum AdminLoginStatus
{
    /// <summary>
    /// The credentials were wrong or the user is not allowed to log in.
    /// </summary>
    Failed,

    /// <summary>
    /// The user is locked out because of too many failed attempts.
    /// </summary>
    LockedOut,

    /// <summary>
    /// The password was correct, but a second factor is required now.
    /// </summary>
    TwoFactorRequired,

    /// <summary>
    /// The login succeeded.
    /// </summary>
    Succeeded,
}

/// <summary>
/// The result of a login attempt.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Ticket">The sign in ticket, in case the login succeeded.</param>
/// <param name="Claims">The claims of the authenticated user, in case the login succeeded.</param>
public record AdminLoginResult(AdminLoginStatus Status, string? Ticket = null, IReadOnlyList<Claim>? Claims = null);

/// <summary>
/// Validates the credentials of an admin panel user.
/// </summary>
/// <remarks>
/// This service runs inside the blazor circuit, so the whole login - including the second factor -
/// happens without leaving the page. Only when everything checked out, a sign in ticket is issued
/// which the browser exchanges for the authentication cookie.
/// </remarks>
public class AdminLoginService
{
    private const int TotpTimeStepSeconds = 30;

    /// <summary>
    /// The number of time steps before and after the current one, in which the step of a code is searched.
    /// </summary>
    private const int TotpStepTolerance = 3;

    private readonly UserManager<AdminUser> _userManager;
    private readonly SignInTicketService _ticketService;
    private readonly ILogger<AdminLoginService> _logger;

    private AdminUser? _pendingTwoFactorUser;
    private bool _pendingIsPersistent;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminLoginService"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="ticketService">The sign in ticket service.</param>
    /// <param name="logger">The logger.</param>
    public AdminLoginService(UserManager<AdminUser> userManager, SignInTicketService ticketService, ILogger<AdminLoginService> logger)
    {
        this._userManager = userManager;
        this._ticketService = ticketService;
        this._logger = logger;
    }

    /// <summary>
    /// Gets the login name of the user which has to provide its second factor now.
    /// </summary>
    public string? PendingTwoFactorLoginName => this._pendingTwoFactorUser?.LoginName;

    /// <summary>
    /// Checks the specified password and either finishes the login or asks for the second factor.
    /// </summary>
    /// <param name="loginName">The login name.</param>
    /// <param name="password">The password.</param>
    /// <param name="isPersistent">If set to <c>true</c>, the session survives a browser restart.</param>
    /// <returns>The result of the attempt.</returns>
    public async Task<AdminLoginResult> CheckPasswordAsync(string loginName, string password, bool isPersistent)
    {
        this._pendingTwoFactorUser = null;

        var user = await this._userManager.FindByNameAsync(loginName).ConfigureAwait(false);
        if (user is null || user.IsDisabled)
        {
            this._logger.LogWarning("Failed admin panel login attempt for unknown or disabled user '{LoginName}'.", loginName);
            return new AdminLoginResult(AdminLoginStatus.Failed);
        }

        if (await this._userManager.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            this._logger.LogWarning("Admin panel login attempt for locked out user '{LoginName}'.", user.LoginName);
            return new AdminLoginResult(AdminLoginStatus.LockedOut);
        }

        if (!await this._userManager.CheckPasswordAsync(user, password).ConfigureAwait(false))
        {
            await this._userManager.AccessFailedAsync(user).ConfigureAwait(false);
            this._logger.LogWarning("Failed admin panel login attempt for user '{LoginName}' (wrong password).", user.LoginName);
            return await this.GetFailedResultAsync(user).ConfigureAwait(false);
        }

        if (user.IsTwoFactorEnabled)
        {
            this._pendingTwoFactorUser = user;
            this._pendingIsPersistent = isPersistent;
            return new AdminLoginResult(AdminLoginStatus.TwoFactorRequired);
        }

        return await this.CompleteLoginAsync(user, usedSecondFactor: false, isPersistent).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the second factor of the user which passed the password check before.
    /// </summary>
    /// <param name="code">The authenticator code or recovery code.</param>
    /// <param name="isRecoveryCode">If set to <c>true</c>, the code is treated as a recovery code.</param>
    /// <returns>The result of the attempt.</returns>
    public async Task<AdminLoginResult> CheckTwoFactorAsync(string code, bool isRecoveryCode)
    {
        if (this._pendingTwoFactorUser is not { } user)
        {
            return new AdminLoginResult(AdminLoginStatus.Failed);
        }

        if (await this._userManager.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return new AdminLoginResult(AdminLoginStatus.LockedOut);
        }

        var normalizedCode = code.Replace(" ", string.Empty).Replace("-", string.Empty);
        bool isValid;
        if (isRecoveryCode)
        {
            var result = await this._userManager.RedeemTwoFactorRecoveryCodeAsync(user, normalizedCode).ConfigureAwait(false);
            isValid = result.Succeeded;
        }
        else
        {
            isValid = await this._userManager
                .VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, normalizedCode)
                .ConfigureAwait(false)
                && await this.TryConsumeTimeStepAsync(user, normalizedCode).ConfigureAwait(false);
        }

        if (!isValid)
        {
            await this._userManager.AccessFailedAsync(user).ConfigureAwait(false);
            this._logger.LogWarning("Failed second factor for admin panel user '{LoginName}'.", user.LoginName);
            return await this.GetFailedResultAsync(user).ConfigureAwait(false);
        }

        this._pendingTwoFactorUser = null;
        return await this.CompleteLoginAsync(user, usedSecondFactor: true, this._pendingIsPersistent).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues a new sign in ticket for an already authenticated user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="usedSecondFactor">If set to <c>true</c>, the user authenticated with a second factor.</param>
    /// <returns>The ticket and the claims it carries.</returns>
    /// <remarks>
    /// This is needed after a security relevant change of the own user: such a change rotates the
    /// security stamp, which would invalidate the running session. Re-issuing the cookie keeps the
    /// user signed in without a reload.
    /// </remarks>
    public (string Ticket, IReadOnlyList<Claim> Claims) IssueSessionTicket(AdminUser user, bool usedSecondFactor)
    {
        var claims = CreateClaims(user, usedSecondFactor);
        return (this._ticketService.Issue(claims, false), claims);
    }

    /// <summary>
    /// Builds the claims which describe the specified authenticated user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="usedSecondFactor">If set to <c>true</c>, the user authenticated with a second factor.</param>
    /// <returns>The claims of the user.</returns>
    public static IReadOnlyList<Claim> CreateClaims(AdminUser user, bool usedSecondFactor)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.LoginName),
            new(AdminAuthenticationDefaults.SecurityStampClaimType, user.SecurityStamp),
            new(
                AdminAuthenticationDefaults.AuthenticationMethodClaimType,
                usedSecondFactor
                    ? AdminAuthenticationDefaults.MultiFactorAuthenticationMethod
                    : AdminAuthenticationDefaults.PasswordAuthenticationMethod),
        };

        var assignedRoles = (user.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var effectiveRoles = assignedRoles
            .SelectMany(AdminRoles.GetEffectiveRoles)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        claims.AddRange(effectiveRoles.Select(role => new Claim(ClaimTypes.Role, role)));

        return claims;
    }

    /// <summary>
    /// Finds the latest time step around the current time, for which the specified code is valid.
    /// </summary>
    /// <param name="base32Key">The base32 encoded authenticator key.</param>
    /// <param name="code">The code.</param>
    /// <returns>The time step, or <c>null</c>, if the code doesn't match any time step of the window.</returns>
    /// <remarks>
    /// The window is one step wider than the one of the token provider (two steps in each direction),
    /// so that a code which it accepted is still found when the time step changed in between.
    /// </remarks>
    private static long? FindMatchingTimeStep(string? base32Key, string code)
    {
        if (string.IsNullOrEmpty(base32Key)
            || !int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedCode)
            || !TryDecodeBase32(base32Key, out var key))
        {
            return null;
        }

        var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TotpTimeStepSeconds;
        for (var step = currentStep + TotpStepTolerance; step >= currentStep - TotpStepTolerance; step--)
        {
            if (ComputeTotp(key, step) == expectedCode)
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>
    /// Computes the time based one time password of RFC 6238 for the specified time step,
    /// the same way as the token provider of ASP.NET Core Identity does.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="timeStep">The time step.</param>
    /// <returns>The six digit code, as number.</returns>
    private static int ComputeTotp(byte[] key, long timeStep)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binaryCode = ((hash[offset] & 0x7F) << 24)
                         | (hash[offset + 1] << 16)
                         | (hash[offset + 2] << 8)
                         | hash[offset + 3];
        return binaryCode % 1_000_000;
    }

    private static bool TryDecodeBase32(string input, out byte[] result)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(input.Length * 5 / 8);
        var buffer = 0;
        var bitCount = 0;
        foreach (var character in input.TrimEnd('='))
        {
            if (character == ' ')
            {
                continue;
            }

            var index = alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);
            if (index < 0)
            {
                result = Array.Empty<byte>();
                return false;
            }

            buffer = (buffer << 5) | index;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                bytes.Add((byte)((buffer >> bitCount) & 0xFF));
            }
        }

        result = bytes.ToArray();
        return true;
    }

    private async Task<AdminLoginResult> GetFailedResultAsync(AdminUser user)
    {
        return await this._userManager.IsLockedOutAsync(user).ConfigureAwait(false)
            ? new AdminLoginResult(AdminLoginStatus.LockedOut)
            : new AdminLoginResult(AdminLoginStatus.Failed);
    }

    private async Task<AdminLoginResult> CompleteLoginAsync(AdminUser user, bool usedSecondFactor, bool isPersistent)
    {
        await this._userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        user.LastLoginAt = DateTime.UtcNow;
        await this._userManager.UpdateAsync(user).ConfigureAwait(false);

        this._logger.LogInformation(
            "Admin panel user '{LoginName}' logged in (second factor: {UsedSecondFactor}).",
            user.LoginName,
            usedSecondFactor);

        var claims = CreateClaims(user, usedSecondFactor);
        var ticket = this._ticketService.Issue(claims, isPersistent);
        return new AdminLoginResult(AdminLoginStatus.Succeeded, ticket, claims);
    }

    /// <summary>
    /// Makes sure that an observed authenticator code can't be used a second time within its validation window.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="code">The code, which has already been verified by the token provider.</param>
    /// <returns><c>true</c>, if no code of the same or a later time step was accepted before; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// The token provider of ASP.NET Core Identity accepts a code of the current and of the adjacent
    /// time steps, but it doesn't tell which step matched and it doesn't remember used codes.
    /// So the time step which the code belongs to is determined here, and only codes of a later time
    /// step than the last accepted one are accepted. Remembering the current time step instead would
    /// allow to replay a code of an earlier step as long as it's within the validation window.
    /// </remarks>
    private async Task<bool> TryConsumeTimeStepAsync(AdminUser user, string code)
    {
        var key = await this._userManager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        var matchedStep = FindMatchingTimeStep(key, code);
        if (matchedStep is null)
        {
            this._logger.LogWarning(
                "Rejected an authenticator code of admin panel user '{LoginName}', because its time step could not be determined.",
                user.LoginName);
            return false;
        }

        if (matchedStep.Value <= user.LastAcceptedTotpStep)
        {
            this._logger.LogWarning(
                "Rejected an authenticator code of admin panel user '{LoginName}', because a code of the same or a later time step was already used.",
                user.LoginName);
            return false;
        }

        user.LastAcceptedTotpStep = matchedStep.Value;
        await this._userManager.UpdateAsync(user).ConfigureAwait(false);
        return true;
    }
}
