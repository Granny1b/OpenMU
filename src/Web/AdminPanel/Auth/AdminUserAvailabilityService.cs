// <copyright file="AdminUserAvailabilityService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Auth;

using System.Threading;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Persistence.AdminAuth;

/// <summary>
/// Keeps track of whether the admin panel has any user at all.
/// </summary>
/// <remarks>
/// On a fresh installation there is no user, and the admin panel is the tool which creates it.
/// Until the first user exists, the panel has to stay reachable - it then runs in an unprotected
/// initial setup mode and says so. Configuring a bootstrap user avoids that state.
/// That mode is only entered when the storage could actually be asked and confirmed that there is
/// no user. When the storage is unavailable or the answer is not known (yet), the service assumes
/// that users exist, so the panel fails closed instead of opening up to everybody.
/// </remarks>
public class AdminUserAvailabilityService
{
    /// <summary>
    /// The storage couldn't be asked (yet), e.g. because it's unavailable.
    /// </summary>
    private const int UsersUnknown = 0;

    /// <summary>
    /// The storage confirmed that no user exists.
    /// </summary>
    private const int NoUsers = 1;

    /// <summary>
    /// The storage confirmed that at least one user exists.
    /// </summary>
    private const int UsersExist = 2;

    /// <summary>
    /// The time for which the answer is reused before the storage is asked again.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    private readonly IAdminUserRepository _repository;
    private readonly BootstrapAdminUserProvider _bootstrapUserProvider;
    private readonly ILogger<AdminUserAvailabilityService>? _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private DateTime _nextCheck = DateTime.MinValue;

    /// <summary>
    /// The last answer of the storage, as one of the <c>Users*</c> constants. It's an
    /// <see cref="int"/>, because it's read and written by concurrent requests.
    /// </summary>
    private int _state = UsersUnknown;

    /// <summary>
    /// Incremented by <see cref="Invalidate"/>, so that a count which was running at the same time
    /// isn't remembered as the current answer.
    /// </summary>
    private int _generation;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminUserAvailabilityService"/> class.
    /// </summary>
    /// <param name="repository">The repository of the stored users.</param>
    /// <param name="bootstrapUserProvider">The provider of the bootstrap user.</param>
    /// <param name="logger">The logger.</param>
    public AdminUserAvailabilityService(IAdminUserRepository repository, BootstrapAdminUserProvider bootstrapUserProvider, ILogger<AdminUserAvailabilityService>? logger = null)
    {
        this._repository = repository;
        this._bootstrapUserProvider = bootstrapUserProvider;
        this._logger = logger;
    }

    /// <summary>
    /// Determines whether at least one user exists which could log in.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// <c>false</c>, if the storage confirmed that no user exists; otherwise, <c>true</c> - also when
    /// the storage is unavailable or the answer is not known yet.
    /// </returns>
    /// <remarks>
    /// This is called by the authorization of every request, so it must never wait for the
    /// database: when another caller is already asking, or when the last answer is still fresh,
    /// the known value is returned right away.
    /// </remarks>
    public async ValueTask<bool> AnyUserExistsAsync(CancellationToken cancellationToken = default)
    {
        if (this._bootstrapUserProvider.User is not null || Volatile.Read(ref this._state) == UsersExist)
        {
            return true;
        }

        if (DateTime.UtcNow < this._nextCheck || !await this._semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Only a confirmed "no user" opens the initial setup mode. While the answer is unknown,
            // e.g. because another caller is still asking the storage, the panel stays closed.
            return Volatile.Read(ref this._state) != NoUsers;
        }

        try
        {
            if (Volatile.Read(ref this._state) == UsersExist || DateTime.UtcNow < this._nextCheck)
            {
                return Volatile.Read(ref this._state) != NoUsers;
            }

            var generation = Volatile.Read(ref this._generation);
            var result = await this.CountUsersAsync(cancellationToken).ConfigureAwait(false);
            var isCurrent = generation == Volatile.Read(ref this._generation);
            if (isCurrent)
            {
                Volatile.Write(ref this._state, result);
                this._nextCheck = DateTime.UtcNow.Add(CheckInterval);
                if (generation != Volatile.Read(ref this._generation))
                {
                    // Invalidated while the result was stored - undo it like the invalidation would have.
                    Interlocked.CompareExchange(ref this._state, UsersUnknown, NoUsers);
                    this._nextCheck = DateTime.MinValue;
                    isCurrent = false;
                }
            }

            // A user may have been created or deleted while counting; the outdated result is
            // neither remembered nor allowed to open the initial setup mode.
            return !(isCurrent && result == NoUsers);
        }
        finally
        {
            this._semaphore.Release();
        }
    }

    /// <summary>
    /// Invalidates the cached result, e.g. after a user has been created or deleted.
    /// </summary>
    /// <remarks>
    /// Until the storage has been asked again, the service doesn't report that no user exists:
    /// a known user is kept, and a previous "no user" becomes unknown, which keeps the panel closed.
    /// </remarks>
    public void Invalidate()
    {
        Interlocked.Increment(ref this._generation);
        Interlocked.CompareExchange(ref this._state, UsersUnknown, NoUsers);
        this._nextCheck = DateTime.MinValue;
    }

    /// <summary>
    /// Asks the storage whether any user exists.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The answer of the storage, as one of the <c>Users*</c> constants.</returns>
    private async ValueTask<int> CountUsersAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await this._repository.EnsureStorageAsync(cancellationToken).ConfigureAwait(false))
            {
                return UsersUnknown;
            }

            if (await this._repository.GetCountAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                return UsersExist;
            }

            // A repository may report zero users when it lost its storage in the meantime, so a
            // "no user" is only trusted when the storage is still available afterwards.
            return await this._repository.EnsureStorageAsync(cancellationToken).ConfigureAwait(false) ? NoUsers : UsersUnknown;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this._logger?.LogWarning(ex, "Could not determine whether an admin panel user exists; access is denied until it can be determined.");
            return UsersUnknown;
        }
    }
}
