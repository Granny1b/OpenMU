// <copyright file="AdminAuthenticationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.AdminAuth;

using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MUnique.OpenMU.Persistence.AdminAuth;
using MUnique.OpenMU.Web.AdminPanel.Auth;

/// <summary>
/// Tests for the authentication of the admin panel.
/// </summary>
[TestFixture]
public class AdminAuthenticationTests
{
    private const string TestPassword = "a-very-long-test-password";

    private ServiceProvider _serviceProvider = null!;
    private InMemoryAdminUserRepository _repository = null!;

    /// <summary>
    /// Sets a fresh service provider up for each test.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        this._repository = new InMemoryAdminUserRepository();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddDataProtection();
        services.AddSingleton<IAdminUserRepository>(this._repository);
        services.AddSingleton(Options.Create(new AdminPanelAuthOptions()));
        services.AddSingleton<AdminUserSecretProtector>();
        services.AddSingleton<IPasswordHasher<AdminUser>, BCryptPasswordHasher>();
        services.AddSingleton<BootstrapAdminUserProvider>();
        services.AddSingleton<SignInTicketService>();
        services.AddScoped<IUserStore<AdminUser>, AdminUserStore>();
        services.AddScoped<AdminLoginService>();
        services.AddScoped<AuthenticatorSetupService>();
        services.AddIdentityCore<AdminUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 3;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddDefaultTokenProviders();

        this._serviceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Disposes the service provider.
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        this._serviceProvider.Dispose();
    }

    /// <summary>
    /// Tests that a user without a second factor can log in with its password.
    /// </summary>
    [Test]
    public async Task LoginWithoutSecondFactorSucceedsAsync()
    {
        await this.CreateUserAsync("tester").ConfigureAwait(false);

        var result = await this.GetLoginService().CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);

        Assert.That(result.Status, Is.EqualTo(AdminLoginStatus.Succeeded));
        Assert.That(result.Ticket, Is.Not.Null);
        Assert.That(result.Claims, Is.Not.Null);
    }

    /// <summary>
    /// Tests that a wrong password is rejected.
    /// </summary>
    [Test]
    public async Task LoginWithWrongPasswordFailsAsync()
    {
        await this.CreateUserAsync("tester").ConfigureAwait(false);

        var result = await this.GetLoginService().CheckPasswordAsync("tester", "wrong-password-here", false).ConfigureAwait(false);

        Assert.That(result.Status, Is.EqualTo(AdminLoginStatus.Failed));
        Assert.That(result.Ticket, Is.Null);
    }

    /// <summary>
    /// Tests that an unknown login name is rejected.
    /// </summary>
    [Test]
    public async Task LoginWithUnknownUserFailsAsync()
    {
        var result = await this.GetLoginService().CheckPasswordAsync("nobody", TestPassword, false).ConfigureAwait(false);

        Assert.That(result.Status, Is.EqualTo(AdminLoginStatus.Failed));
    }

    /// <summary>
    /// Tests that a disabled user can't log in, even with the correct password.
    /// </summary>
    [Test]
    public async Task LoginOfDisabledUserFailsAsync()
    {
        var user = await this.CreateUserAsync("tester").ConfigureAwait(false);
        user.IsDisabled = true;
        await this._repository.UpdateAsync(user).ConfigureAwait(false);

        var result = await this.GetLoginService().CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);

        Assert.That(result.Status, Is.EqualTo(AdminLoginStatus.Failed));
    }

    /// <summary>
    /// Tests that the user gets locked out after too many failed attempts.
    /// </summary>
    [Test]
    public async Task RepeatedFailedAttemptsCauseLockoutAsync()
    {
        await this.CreateUserAsync("tester").ConfigureAwait(false);
        var loginService = this.GetLoginService();

        for (var i = 0; i < 3; i++)
        {
            await loginService.CheckPasswordAsync("tester", "wrong-password-here", false).ConfigureAwait(false);
        }

        var result = await loginService.CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);

        Assert.That(result.Status, Is.EqualTo(AdminLoginStatus.LockedOut));
    }

    /// <summary>
    /// Tests that the second factor is asked for and that a valid authenticator code completes the login.
    /// </summary>
    [Test]
    public async Task LoginWithSecondFactorSucceedsAsync()
    {
        var (_, key) = await this.CreateUserWithAuthenticatorAsync("tester").ConfigureAwait(false);
        var loginService = this.GetLoginService();

        var passwordResult = await loginService.CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);
        Assert.That(passwordResult.Status, Is.EqualTo(AdminLoginStatus.TwoFactorRequired));
        Assert.That(passwordResult.Ticket, Is.Null, "No ticket may be issued before the second factor was checked.");

        var codeResult = await loginService.CheckTwoFactorAsync(TestTotpGenerator.Generate(key), false).ConfigureAwait(false);

        Assert.That(codeResult.Status, Is.EqualTo(AdminLoginStatus.Succeeded));
        Assert.That(codeResult.Ticket, Is.Not.Null);
    }

    /// <summary>
    /// Tests that a wrong authenticator code is rejected.
    /// </summary>
    [Test]
    public async Task LoginWithWrongSecondFactorFailsAsync()
    {
        await this.CreateUserWithAuthenticatorAsync("tester").ConfigureAwait(false);
        var loginService = this.GetLoginService();
        await loginService.CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);

        var codeResult = await loginService.CheckTwoFactorAsync("000000", false).ConfigureAwait(false);

        Assert.That(codeResult.Status, Is.EqualTo(AdminLoginStatus.Failed));
    }

    /// <summary>
    /// Tests that the same authenticator code can't be used a second time within its time step.
    /// </summary>
    [Test]
    public async Task ReplayedAuthenticatorCodeIsRejectedAsync()
    {
        var (_, key) = await this.CreateUserWithAuthenticatorAsync("tester").ConfigureAwait(false);
        var code = TestTotpGenerator.Generate(key);

        var firstLogin = this.GetLoginService();
        await firstLogin.CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);
        var firstResult = await firstLogin.CheckTwoFactorAsync(code, false).ConfigureAwait(false);
        Assert.That(firstResult.Status, Is.EqualTo(AdminLoginStatus.Succeeded));

        var secondLogin = this.GetLoginService();
        await secondLogin.CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);
        var secondResult = await secondLogin.CheckTwoFactorAsync(code, false).ConfigureAwait(false);

        Assert.That(secondResult.Status, Is.EqualTo(AdminLoginStatus.Failed));
    }

    /// <summary>
    /// Tests that an authenticator code can't be replayed in a later time step, as long as it's
    /// still within the validation window of the token provider.
    /// </summary>
    /// <remarks>
    /// The time step of the last accepted code is set up as if the code of the previous time step
    /// had been used during that step. Remembering the current time step instead of the matched
    /// one would accept that code a second time now.
    /// </remarks>
    [Test]
    public async Task AuthenticatorCodeOfAnEarlierStepCanNotBeReplayedAsync()
    {
        var (user, key) = await this.CreateUserWithAuthenticatorAsync("tester").ConfigureAwait(false);
        var previousStepCode = TestTotpGenerator.Generate(key, -1);
        var previousStep = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30) - 1;
        await this.SetLastAcceptedTotpStepAsync(user, previousStep).ConfigureAwait(false);

        var loginService = this.GetLoginService();
        await loginService.CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);
        var result = await loginService.CheckTwoFactorAsync(previousStepCode, false).ConfigureAwait(false);

        Assert.That(result.Status, Is.EqualTo(AdminLoginStatus.Failed));
    }

    /// <summary>
    /// Tests that an authenticator code of a later time step than the last accepted one is accepted,
    /// and that its own time step is remembered.
    /// </summary>
    [Test]
    public async Task AuthenticatorCodeOfALaterStepIsAcceptedAsync()
    {
        var (user, key) = await this.CreateUserWithAuthenticatorAsync("tester").ConfigureAwait(false);
        var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        await this.SetLastAcceptedTotpStepAsync(user, currentStep - 2).ConfigureAwait(false);

        var loginService = this.GetLoginService();
        await loginService.CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);
        var result = await loginService.CheckTwoFactorAsync(TestTotpGenerator.Generate(key, 1), false).ConfigureAwait(false);

        Assert.That(result.Status, Is.EqualTo(AdminLoginStatus.Succeeded));
        var storedUser = await this._repository.GetByIdAsync(user.Id).ConfigureAwait(false);
        Assert.That(storedUser!.LastAcceptedTotpStep, Is.GreaterThan(currentStep));
    }

    /// <summary>
    /// Tests that a recovery code works exactly once.
    /// </summary>
    [Test]
    public async Task RecoveryCodeCanBeUsedOnceAsync()
    {
        var (user, key) = await this.CreateUserWithAuthenticatorAsync("tester").ConfigureAwait(false);
        using var scope = this._serviceProvider.CreateScope();
        var setupService = scope.ServiceProvider.GetRequiredService<AuthenticatorSetupService>();
        var recoveryCodes = await setupService.GenerateRecoveryCodesAsync(user).ConfigureAwait(false);
        Assert.That(recoveryCodes, Is.Not.Empty);
        var recoveryCode = recoveryCodes[0];

        var firstLogin = this.GetLoginService();
        await firstLogin.CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);
        var firstResult = await firstLogin.CheckTwoFactorAsync(recoveryCode, true).ConfigureAwait(false);
        Assert.That(firstResult.Status, Is.EqualTo(AdminLoginStatus.Succeeded));

        var secondLogin = this.GetLoginService();
        await secondLogin.CheckPasswordAsync("tester", TestPassword, false).ConfigureAwait(false);
        var secondResult = await secondLogin.CheckTwoFactorAsync(recoveryCode, true).ConfigureAwait(false);
        Assert.That(secondResult.Status, Is.EqualTo(AdminLoginStatus.Failed));

        // Suppress the unused variable warning for the key, which is only needed for the setup.
        Assert.That(key, Is.Not.Empty);
    }

    /// <summary>
    /// Tests that the second factor is only enabled after the user proved that its app produces valid codes.
    /// </summary>
    [Test]
    public async Task SecondFactorIsOnlyEnabledAfterConfirmationAsync()
    {
        var user = await this.CreateUserAsync("tester").ConfigureAwait(false);
        using var scope = this._serviceProvider.CreateScope();
        var setupService = scope.ServiceProvider.GetRequiredService<AuthenticatorSetupService>();

        var setup = await setupService.BeginSetupAsync(user).ConfigureAwait(false);
        Assert.That(user.IsTwoFactorEnabled, Is.False, "The second factor must not be active before it was confirmed.");
        Assert.That(setup.QrCodeSvg, Does.Contain("<svg"));
        Assert.That(setup.AuthenticatorUri, Does.StartWith("otpauth://totp/"));
        Assert.That(setup.AuthenticatorUri, Does.Contain("digits=6"));
        Assert.That(setup.AuthenticatorUri, Does.Contain("period=30"));
        Assert.That(setup.AuthenticatorUri, Does.Contain("algorithm=SHA1"));

        var wrongResult = await setupService.ConfirmSetupAsync(user, "000000").ConfigureAwait(false);
        Assert.That(wrongResult, Is.Null);
        Assert.That(user.IsTwoFactorEnabled, Is.False);

        var key = setup.SharedKey.Replace(" ", string.Empty);
        var recoveryCodes = await setupService.ConfirmSetupAsync(user, TestTotpGenerator.Generate(key)).ConfigureAwait(false);
        Assert.That(recoveryCodes, Is.Not.Null);
        Assert.That(user.IsTwoFactorEnabled, Is.True);
    }

    /// <summary>
    /// Tests that the authenticator key is not stored in plain text.
    /// </summary>
    [Test]
    public async Task AuthenticatorKeyIsProtectedAsync()
    {
        var (user, key) = await this.CreateUserWithAuthenticatorAsync("tester").ConfigureAwait(false);

        Assert.That(user.ProtectedAuthenticatorKey, Is.Not.Null);
        Assert.That(user.ProtectedAuthenticatorKey, Does.Not.Contain(key));
    }

    /// <summary>
    /// Tests that a sign in ticket can only be redeemed once.
    /// </summary>
    [Test]
    public void SignInTicketIsSingleUse()
    {
        var ticketService = new SignInTicketService();
        var ticketValue = ticketService.Issue(Array.Empty<System.Security.Claims.Claim>(), false);

        Assert.That(ticketService.TryRedeem(ticketValue, out var ticket), Is.True);
        Assert.That(ticket, Is.Not.Null);
        Assert.That(ticketService.TryRedeem(ticketValue, out _), Is.False);
        Assert.That(ticketService.TryRedeem("some-other-value", out _), Is.False);
    }

    /// <summary>
    /// Tests that the roles build up on each other.
    /// </summary>
    [Test]
    public void EffectiveRolesIncludeTheLessPrivilegedOnes()
    {
        Assert.That(AdminRoles.GetEffectiveRoles(AdminRoles.Administrator), Is.EquivalentTo(new[] { AdminRoles.Viewer, AdminRoles.Operator, AdminRoles.Administrator }));
        Assert.That(AdminRoles.GetEffectiveRoles(AdminRoles.Operator), Is.EquivalentTo(new[] { AdminRoles.Viewer, AdminRoles.Operator }));
        Assert.That(AdminRoles.GetEffectiveRoles(AdminRoles.Viewer), Is.EquivalentTo(new[] { AdminRoles.Viewer }));
        Assert.That(AdminRoles.GetEffectiveRoles("Unknown"), Is.Empty);
    }

    /// <summary>
    /// Tests that an unreachable storage is not asked again on every authorization check,
    /// and that the panel stays closed while it's unreachable.
    /// </summary>
    /// <remarks>
    /// The authorization of every request asks whether a user exists. When that answer required a
    /// database round trip each time, an unreachable database made the whole admin panel wait for
    /// connection attempts which were going to time out anyway - it never finished loading.
    /// </remarks>
    [Test]
    public async Task UnavailableStorageIsNotProbedOnEveryCheckAsync()
    {
        var repository = new UnavailableAdminUserRepository();
        var service = new AdminUserAvailabilityService(
            repository,
            this._serviceProvider.GetRequiredService<BootstrapAdminUserProvider>());

        for (var i = 0; i < 20; i++)
        {
            // An unreachable storage can't confirm that no user exists, so the initial setup mode
            // must not be entered - otherwise a database outage opens the panel to everybody.
            Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.True);
        }

        Assert.That(repository.EnsureStorageCallCount, Is.EqualTo(1));
    }

    /// <summary>
    /// Tests that an unusable directory for the data protection keys is reported instead of throwing.
    /// </summary>
    /// <remarks>
    /// The docker images run as a non-root user while /app belongs to root, so a directory which
    /// wasn't prepared in the image can't be created. That used to surface as an error page on
    /// every page of the panel, as soon as a key was needed for an antiforgery token or a cookie.
    /// </remarks>
    [Test]
    public void UnusableDataProtectionDirectoryIsReportedAndDoesNotThrow()
    {
        // A file at the place of the directory is a portable way to make its creation fail.
        var blockingFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(blockingFilePath, string.Empty);
        try
        {
            var provider = BuildAuthServices(blockingFilePath);
            var status = provider.GetRequiredService<DataProtectionKeyStorageStatus>();

            Assert.That(status.Error, Is.Not.Null);
            Assert.That(status.Path, Is.EqualTo(blockingFilePath));

            // The keys are what an antiforgery token and the authentication cookie are protected
            // with, so this is what used to fail on every page of the panel.
            var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
            Assert.That(() => protector.Protect("payload"), Throws.Nothing);
        }
        finally
        {
            File.Delete(blockingFilePath);
        }
    }

    /// <summary>
    /// Tests that a usable directory for the data protection keys is accepted.
    /// </summary>
    [Test]
    public void UsableDataProtectionDirectoryIsAccepted()
    {
        var keyDirectoryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var status = BuildAuthServices(keyDirectoryPath).GetRequiredService<DataProtectionKeyStorageStatus>();

            Assert.That(status.Error, Is.Null);
            Assert.That(Directory.Exists(keyDirectoryPath), Is.True);
        }
        finally
        {
            if (Directory.Exists(keyDirectoryPath))
            {
                Directory.Delete(keyDirectoryPath, true);
            }
        }
    }

    /// <summary>
    /// Tests that an authorization check doesn't wait for an availability probe which is already running.
    /// </summary>
    /// <remarks>
    /// This is the regression test for an admin panel which never finished loading: the
    /// authorization of every request asks whether a user exists, and while the first request was
    /// stuck in the connection timeout of an unreachable database, every other request queued up
    /// behind it - including the ones which render the page.
    /// </remarks>
    [Test]
    public async Task AvailabilityCheckDoesNotWaitForARunningProbeAsync()
    {
        var repository = new BlockingAdminUserRepository();
        var service = new AdminUserAvailabilityService(
            repository,
            this._serviceProvider.GetRequiredService<BootstrapAdminUserProvider>());

        var blockedCall = Task.Run(async () => await service.AnyUserExistsAsync().ConfigureAwait(false));
        await repository.ProbeStarted.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var concurrentCall = service.AnyUserExistsAsync();
        Assert.That(concurrentCall.IsCompleted, Is.True, "A check must not wait for a probe which is already running.");
        Assert.That(await concurrentCall.ConfigureAwait(false), Is.True, "While the answer is unknown, the panel must stay closed.");

        repository.Release();
        Assert.That(await blockedCall.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false), Is.True, "An unavailable storage must not open the panel.");
    }

    /// <summary>
    /// Tests that the initial setup mode is entered when the storage is available and confirms that no user exists.
    /// </summary>
    [Test]
    public async Task ConfirmedEmptyStorageAllowsTheInitialSetupAsync()
    {
        var service = new AdminUserAvailabilityService(
            this._repository,
            this._serviceProvider.GetRequiredService<BootstrapAdminUserProvider>());

        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.False);
        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.False);
    }

    /// <summary>
    /// Tests that a storage which fails to count the users doesn't open the panel.
    /// </summary>
    [Test]
    public async Task FailingUserCountKeepsThePanelClosedAsync()
    {
        var repository = new GatedAdminUserRepository { ThrowOnCount = true };
        var service = new AdminUserAvailabilityService(
            repository,
            this._serviceProvider.GetRequiredService<BootstrapAdminUserProvider>());

        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.True);
    }

    /// <summary>
    /// Tests that an invalidation doesn't make the service report that no user exists while it's counting again.
    /// </summary>
    /// <remarks>
    /// The invalidation used to reset the answer to "no user", so every request which arrived while the
    /// recount was running was let in without a login.
    /// </remarks>
    [Test]
    public async Task InvalidationDoesNotOpenThePanelWhileCountingAsync()
    {
        var repository = new GatedAdminUserRepository();
        var service = new AdminUserAvailabilityService(
            repository,
            this._serviceProvider.GetRequiredService<BootstrapAdminUserProvider>());
        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.False);

        // The first user gets created; the recount which follows the invalidation is held up.
        repository.UserCount = 1;
        repository.BlockNextProbe();
        service.Invalidate();
        var recount = Task.Run(async () => await service.AnyUserExistsAsync().ConfigureAwait(false));
        await repository.ProbeStarted.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.True, "A pending recount must not report that no user exists.");

        repository.Release();
        Assert.That(await recount.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false), Is.True);
        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.True);
    }

    /// <summary>
    /// Tests that an existing user is still known after an invalidation, e.g. after another user was deleted.
    /// </summary>
    [Test]
    public async Task InvalidationKeepsAKnownUserAsync()
    {
        await this.CreateUserAsync("tester").ConfigureAwait(false);
        var service = new AdminUserAvailabilityService(
            this._repository,
            this._serviceProvider.GetRequiredService<BootstrapAdminUserProvider>());
        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.True);

        service.Invalidate();

        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.True);
    }

    /// <summary>
    /// Tests that the answer is cached once a user exists, so the database isn't queried again.
    /// </summary>
    [Test]
    public async Task ExistingUserIsRememberedAsync()
    {
        await this.CreateUserAsync("tester").ConfigureAwait(false);
        var service = new AdminUserAvailabilityService(
            this._repository,
            this._serviceProvider.GetRequiredService<BootstrapAdminUserProvider>());

        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.True);
        Assert.That(await service.AnyUserExistsAsync().ConfigureAwait(false), Is.True);
    }

    /// <summary>
    /// Tests that the role names and the role enum stay in sync, since the roles are stored
    /// and compared as strings, but selected as an enum in the user interface.
    /// </summary>
    [Test]
    public void RoleNamesMatchTheRoleEnum()
    {
        Assert.That(AdminRoles.All, Is.EqualTo(Enum.GetNames<AdminRole>()).AsCollection);
        Assert.That(AdminRoles.Viewer, Is.EqualTo(AdminRole.Viewer.ToString()));
        Assert.That(AdminRoles.Operator, Is.EqualTo(AdminRole.Operator.ToString()));
        Assert.That(AdminRoles.Administrator, Is.EqualTo(AdminRole.Administrator.ToString()));
    }

    /// <summary>
    /// Tests that the claims of an administrator contain the implied roles.
    /// </summary>
    [Test]
    public async Task ClaimsContainTheEffectiveRolesAsync()
    {
        var user = await this.CreateUserAsync("tester").ConfigureAwait(false);

        var claims = AdminLoginService.CreateClaims(user, true);
        var roles = claims
            .Where(c => c.Type == System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList();

        Assert.That(roles, Is.EquivalentTo(new[] { AdminRoles.Viewer, AdminRoles.Operator, AdminRoles.Administrator }));
        Assert.That(
            claims.Any(c => c.Type == AdminAuthenticationDefaults.AuthenticationMethodClaimType
                            && c.Value == AdminAuthenticationDefaults.MultiFactorAuthenticationMethod),
            Is.True);
    }

    private static ServiceProvider BuildAuthServices(string dataProtectionKeyPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminPanel:Auth:DataProtectionKeyPath"] = dataProtectionKeyPath,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAdminPanelAuth(configuration);
        return services.BuildServiceProvider();
    }

    private async Task SetLastAcceptedTotpStepAsync(AdminUser user, long step)
    {
        using var scope = this._serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        var storedUser = await userManager.FindByIdAsync(user.Id.ToString()).ConfigureAwait(false);
        Assert.That(storedUser, Is.Not.Null);
        storedUser!.LastAcceptedTotpStep = step;
        var result = await userManager.UpdateAsync(storedUser).ConfigureAwait(false);
        Assert.That(result.Succeeded, Is.True);
    }

    private AdminLoginService GetLoginService()
    {
        return this._serviceProvider.CreateScope().ServiceProvider.GetRequiredService<AdminLoginService>();
    }

    private async Task<AdminUser> CreateUserAsync(string loginName)
    {
        using var scope = this._serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        var user = new AdminUser
        {
            LoginName = loginName,
            Roles = AdminRoles.Administrator,
        };

        var result = await userManager.CreateAsync(user, TestPassword).ConfigureAwait(false);
        Assert.That(result.Succeeded, Is.True, string.Join(' ', result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task<(AdminUser User, string Key)> CreateUserWithAuthenticatorAsync(string loginName)
    {
        var user = await this.CreateUserAsync(loginName).ConfigureAwait(false);
        using var scope = this._serviceProvider.CreateScope();
        var setupService = scope.ServiceProvider.GetRequiredService<AuthenticatorSetupService>();
        var setup = await setupService.BeginSetupAsync(user).ConfigureAwait(false);
        var key = setup.SharedKey.Replace(" ", string.Empty);
        var recoveryCodes = await setupService.ConfirmSetupAsync(user, TestTotpGenerator.Generate(key)).ConfigureAwait(false);
        Assert.That(recoveryCodes, Is.Not.Null, "The generated code should have been accepted.");
        return (user, key);
    }
}
