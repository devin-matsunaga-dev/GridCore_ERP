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

    /// <summary>
    /// The projection, in one place so the two lookups cannot drift into answering the same question
    /// differently.
    /// </summary>
    /// <remarks>
    /// <c>IsSettled</c> is expressed here as the status comparison rather than by calling
    /// <c>Payment.IsSettled</c>, because EF has to translate it: the property reads
    /// <c>PaymentTransitions.IsSettled</c>, which is a method call no provider can turn into SQL.
    /// The two say the same thing, and <c>PaymentDirectoryTests</c> is what keeps them saying it.
    /// </remarks>
    private static IQueryable<PaymentSummary> Summaries(IQueryable<Payment> payments) =>
        payments
            .OrderByDescending(payment => payment.Id)
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
                payment.RequestedAt));
}
