namespace GridCore.Platform.Data;

/// <summary>
/// One transaction across every module's <see cref="Microsoft.EntityFrameworkCore.DbContext"/> and
/// the platform's own.
/// </summary>
/// <remarks>
/// <para>
/// Two invariants depend on this. Invariant 1: a write and the audit entry describing it commit
/// together, even though the write is in the module's schema and the entry is in
/// <see cref="PlatformDbContext"/>. Invariant 2: an event and the business change that caused it
/// commit together, because the outbox row lives in the platform schema too — that is what makes
/// the outbox transactional rather than merely a table.
/// </para>
/// <para>
/// Business code wraps its work and never calls <c>SaveChanges</c> itself:
/// <c>await unitOfWork.ExecuteAsync(async ct => { db.Bills.Add(bill); audit.Record(...);
/// await publisher.PublishAsync(BillIssued.For(...), ct); }, cancellationToken);</c>
/// Every enlisted context is saved and the transaction is committed once the delegate returns; if
/// it throws, nothing is written and nothing is published.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>Whether a transaction is currently open on this scope.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Runs <paramref name="work"/> inside one transaction, saves every enlisted context and
    /// commits. Nesting joins the transaction already open rather than starting a second one, so
    /// a service may safely call another service that also wraps its work.
    /// </summary>
    /// <exception cref="Exception">Whatever <paramref name="work"/> threw, after rolling back.</exception>
    Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="work"/> inside one transaction. See the generic overload.</summary>
    Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);
}
