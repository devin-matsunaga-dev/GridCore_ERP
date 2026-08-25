using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>
/// Billing's answer to <see cref="IBillDirectory"/>: the billing register as the rest of GridCore is
/// allowed to see it.
/// </summary>
/// <remarks>
/// <para>
/// Registered by <see cref="BillingModule"/> — the only place that knows both halves — and shaped
/// exactly like the reading register Metering exposes and the premise directory Customers does.
/// Payments (WP-2.5) takes money against bills and may neither reference this module nor read
/// <c>billing.bills</c>.
/// </para>
/// <para>
/// <b>The balance is computed here, not by the caller.</b> Since WP-2.4 what a customer owes is the
/// printed total plus every correction since, less what has been paid — three columns and a sign
/// convention. A caller handed the parts would be a caller one refactor away from checking a
/// payment against the wrong figure, which is exactly the mistake this directory exists to prevent.
/// </para>
/// <para>
/// Read-only, for the reason every other directory is: raising, issuing, adjusting and paying a
/// bill stay behind <see cref="IBillService"/> inside Billing. A second module that could reduce a
/// balance is a second module that owns the document.
/// </para>
/// </remarks>
public sealed class BillDirectory(BillingDbContext database) : IBillDirectory
{
    /// <summary>The largest page a lookup will answer, whatever the caller asks for.</summary>
    public const int MaxPageSize = BillService.MaxPageSize;

    /// <inheritdoc />
    public async Task<BillSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var bill = await Bills()
            .FirstOrDefaultAsync(bill => bill.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return bill is null ? null : Summarise(bill);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, BillSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count is 0)
        {
            return new Dictionary<Guid, BillSummary>();
        }

        var wanted = ids.Distinct().ToArray();

        var found = await Bills()
            .Where(bill => wanted.Contains(bill.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return found.ToDictionary(bill => bill.Id, Summarise);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BillSummary>> OutstandingForAccountAsync(
        Guid serviceAccountId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var found = await Bills()
            .Where(bill => bill.ServiceAccountId == serviceAccountId)

            // Spelled out rather than calling BillTransitions.IsOutstanding: EF has to translate
            // this into SQL, and a method call over an enum is not something it can.
            .Where(bill =>
                bill.Status == BillStatus.Issued
                || bill.Status == BillStatus.PartiallyPaid
                || bill.Status == BillStatus.Overdue)

            // Newest first, like every other GridCore list: a clerk taking a payment is usually
            // looking at the bill that just arrived, and the arrears below it.
            .OrderByDescending(bill => bill.Id)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return found.ConvertAll(Summarise);
    }

    /// <summary>
    /// The register as this seam reads it. Untracked, and without lines or adjustments: a caller
    /// outside Billing has no business with either, and loading a decade of corrections to answer
    /// "how much is owed" would be loading them to compute a column that is already stored.
    /// </summary>
    private IQueryable<Bill> Bills() => database.Bills.AsNoTracking();

    /// <summary>
    /// Projects the entity after the query has run, never inside it.
    /// </summary>
    /// <remarks>
    /// WP-2.3's lesson, restated: EF cannot translate a <c>Where</c> applied to a projection into a
    /// record, and projecting first compiles fine and throws at run time. Filter entities, then
    /// project.
    /// </remarks>
    private static BillSummary Summarise(Bill bill) =>
        new(
            bill.Id,
            bill.BillNumber,
            bill.ServiceAccountId,
            bill.AccountNumber,
            bill.CustomerId,
            bill.CustomerName,
            bill.Currency,
            bill.TotalAmount,
            bill.AmountDue,
            bill.AmountPaid,
            bill.Balance,
            bill.Status.ToString(),
            bill.IsOutstanding,
            bill.DueDate);
}
