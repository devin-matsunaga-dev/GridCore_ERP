using GridCore.Contracts.Directories;
using GridCore.Modules.Payments.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Payments.Features.Payments;

/// <summary>
/// Payments' answer to <see cref="IPaymentDirectory"/>: the payment register as the rest of GridCore
/// is allowed to see it.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of Billing's <c>BillDirectory</c> and Customers' <c>ServiceAccountDirectory</c>,
/// registered by <see cref="PaymentsModule"/> — the only place that knows both halves. Customers
/// (WP-2.13) has to confirm that a note filed against a payment names a real payment of that
/// customer's, may not reference this module, and may not read <c>payments.payments</c>; so it takes
/// the interface from <c>Contracts</c> and this answers it.
/// </para>
/// <para>
/// Every query is <c>AsNoTracking</c> and every one projects to <see cref="PaymentSummary"/> in the
/// database rather than materialising a <see cref="Payment"/> first. That is not a micro-optimisation:
/// the entity carries the provider's reference and the instrument charged, and a projection built in
/// memory is one refactor away from handing those across a module boundary.
/// </para>
/// </remarks>
public sealed class PaymentDirectory(PaymentsDbContext database) : IPaymentDirectory
{
    /// <summary>The largest batch a lookup will answer, whatever the caller asks for.</summary>
    public const int MaxBatchSize = 200;

    /// <summary>
    /// The most payments one customer's whole history will answer with (WP-2.14).
    /// </summary>
    /// <remarks>
    /// Far larger than <see cref="MaxBatchSize"/> for the reason <c>BillDirectory.MaxHistorySize</c>
    /// gives: this is everything one customer has ever paid, and a statement whose opening balance
    /// was built from a truncated history would prove out against itself and still be wrong. The cap
    /// bounds a runaway read; nobody is expected to reach it.
    /// </remarks>
    public const int MaxHistorySize = 1_000;

    /// <inheritdoc />
    public Task<PaymentSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Summaries(database.Payments.AsNoTracking().Where(payment => payment.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, PaymentSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // Distinct before the query, as every other directory does: a page of notes about one
        // payment would otherwise send the same id a dozen times, and the answer is keyed by id
        // anyway.
        var wanted = ids.Distinct().Take(MaxBatchSize).ToArray();

        if (wanted.Length is 0)
        {
            return new Dictionary<Guid, PaymentSummary>();
        }

        var found = await Summaries(database.Payments.AsNoTracking().Where(payment => wanted.Contains(payment.Id)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return found.ToDictionary(payment => payment.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentSummary>> ForCustomerAsync(
        Guid customerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Ordered and capped on the ENTITY, then projected — never the other way round. Ordering a
        // query of records by the record is not something EF can translate, and the projection is
        // built in the database precisely so the entity never reaches this module's caller.
        var found = await Summaries(
                database.Payments
                    .AsNoTracking()
                    .Where(payment => payment.CustomerId == customerId)

                    // OLDEST first, unlike every other list in this module, because the caller reads
                    // a statement downwards from an opening balance.
                    .OrderBy(payment => payment.Id)
                    .Take(Math.Clamp(limit, 1, MaxHistorySize)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return found;
    }

    /// <summary>
    /// The projection, in one place so the two lookups cannot drift into answering the same question
    /// differently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IsSettled</c> is expressed here as the status comparison rather than by calling
    /// <c>Payment.IsSettled</c>, because EF has to translate it: the property reads
    /// <c>PaymentTransitions.IsSettled</c>, which is a method call no provider can turn into SQL.
    /// The two say the same thing, and <c>PaymentDirectoryTests</c> is what keeps them saying it.
    /// </para>
    /// <para>
    /// <b>It orders nothing</b> (WP-2.14). Ordering belongs to the caller's entity query, because
    /// the two lookups answer by id and by dictionary — where order means nothing — while the
    /// history answers oldest first, and a sort applied to the projection is one EF cannot translate
    /// at all.
    /// </para>
    /// </remarks>
    private static IQueryable<PaymentSummary> Summaries(IQueryable<Payment> payments) =>
        payments
            .Select(payment => new PaymentSummary(
                payment.Id,
                payment.PaymentNumber,
                payment.CustomerId,
                payment.ServiceAccountId,
                payment.BillId,
                payment.Amount,
                payment.Currency,

                // By name, never the enum: Contracts takes no dependency on this module's types.
                payment.Status.ToString(),
                payment.Status == PaymentStatus.Approved,

                // The method, never the instrument. WP-2.14's export prints how somebody paid; the
                // masked card stays in this schema with the provider's reference, which is the line
                // this projection has drawn since it was written.
                payment.Method,
                payment.RequestedAt,
                payment.SettledAt));
}
