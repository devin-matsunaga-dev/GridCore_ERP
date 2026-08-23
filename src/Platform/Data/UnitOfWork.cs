using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.Data;

/// <summary>
/// A context that takes part in the shared transaction. One is registered per context type by
/// <see cref="DataRegistration.AddGridCoreDbContext{TContext}"/>, so the unit of work can enlist
/// every module's context without Platform referencing any module.
/// </summary>
/// <param name="Context">The context to enlist.</param>
public sealed record UnitOfWorkParticipant(DbContext Context);

/// <summary>
/// The shared-transaction implementation over <see cref="GridCoreDbConnection"/>.
/// </summary>
/// <remarks>
/// Participants are resolved from the scope only when a transaction actually starts — resolving a
/// context constructs it but opens nothing, and a request that never writes should not pay for
/// eight of them. They are enlisted <i>before</i> the delegate runs, because a context first
/// touched inside the delegate would otherwise quietly open its own transaction and break the
/// atomicity the whole type exists to provide.
/// </remarks>
public sealed class UnitOfWork(IServiceProvider services, GridCoreDbConnection connection) : IUnitOfWork
{
    private DbTransaction? _transaction;

    /// <inheritdoc />
    public bool IsActive => _transaction is not null;

    /// <inheritdoc />
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Nested call: the outer transaction is the transaction. Committing here would publish the
        // outer scope's half-finished work.
        if (_transaction is not null)
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }

        var participants = services.GetServices<UnitOfWorkParticipant>().Select(p => p.Context).ToList();

        if (connection.Connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        _transaction = await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var participant in participants)
            {
                await participant.Database.UseTransactionAsync(_transaction, cancellationToken).ConfigureAwait(false);
            }

            var result = await work(cancellationToken).ConfigureAwait(false);

            foreach (var participant in participants)
            {
                await participant.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }
        catch
        {
            await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
        finally
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;

            // Detach the contexts from a transaction that no longer exists, and drop the changes a
            // rolled-back attempt left tracked — the scope may still be used to serve an error path.
            foreach (var participant in participants)
            {
                await participant.Database.UseTransactionAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await ExecuteAsync<object?>(
            async ct =>
            {
                await work(ct).ConfigureAwait(false);

                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
