// <copyright file="GatedAdminUserRepository.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.AdminAuth;

using System.Threading;
using MUnique.OpenMU.Persistence.AdminAuth;

/// <summary>
/// An available <see cref="IAdminUserRepository"/> with a configurable number of users, whose next
/// availability check can be held up, to test what happens while a count is running.
/// </summary>
internal class GatedAdminUserRepository : IAdminUserRepository
{
    private TaskCompletionSource _probeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _blockNextProbe;

    /// <summary>
    /// Gets or sets the number of users which is reported.
    /// </summary>
    public int UserCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether counting the users fails with an exception.
    /// </summary>
    public bool ThrowOnCount { get; set; }

    /// <summary>
    /// Gets a task which completes as soon as a blocked availability check has been entered.
    /// </summary>
    public Task ProbeStarted => this._probeStarted.Task;

    /// <summary>
    /// Lets the next availability check wait until <see cref="Release"/> is called.
    /// </summary>
    public void BlockNextProbe()
    {
        this._probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this._release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this._blockNextProbe = true;
    }

    /// <summary>
    /// Lets the blocked availability check continue.
    /// </summary>
    public void Release() => this._release.TrySetResult();

    /// <inheritdoc />
    public async ValueTask<bool> EnsureStorageAsync(CancellationToken cancellationToken = default)
    {
        if (this._blockNextProbe)
        {
            this._blockNextProbe = false;
            this._probeStarted.TrySetResult();
            await this._release.Task.ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public ValueTask<int> GetCountAsync(CancellationToken cancellationToken = default)
        => this.ThrowOnCount
            ? throw new InvalidOperationException("The storage failed.")
            : ValueTask.FromResult(this.UserCount);

    /// <inheritdoc />
    public ValueTask<IList<AdminUser>> GetAllAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IList<AdminUser>>(new List<AdminUser>());

    /// <inheritdoc />
    public ValueTask<AdminUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AdminUser?>(null);

    /// <inheritdoc />
    public ValueTask<AdminUser?> GetByNormalizedLoginNameAsync(string normalizedLoginName, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AdminUser?>(null);

    /// <inheritdoc />
    public ValueTask AddAsync(AdminUser user, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

    /// <inheritdoc />
    public ValueTask UpdateAsync(AdminUser user, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

    /// <inheritdoc />
    public ValueTask DeleteAsync(AdminUser user, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
}
