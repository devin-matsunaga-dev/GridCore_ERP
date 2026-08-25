using GridCore.Modules.Billing.Data;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.Features.Shared;

/// <summary>
/// The prefix this module's registry numbers are issued under. The <i>shape</i> of a number is the
/// platform's (<see cref="RegistryNumbers"/>); what letters a bill number carries is the Billing
/// module's own business.
/// </summary>
public static class BillNumbers
{
    /// <summary>
    /// Prefix of a bill number, e.g. <c>BIL-000001</c>. Three letters, like <c>MTR-</c>,
    /// <c>AST-</c> and <c>ITM-</c> and for the same reason: it is read out over the phone and
    /// quoted against a payment, and <c>B-000001</c> would be one character away from too many
    /// other things — including the account number <c>A-000001</c> it sits beside on the document.
    /// </summary>
    public const string BillNumberPrefix = "BIL-";
}

/// <summary>
/// Issues the next bill number. A seam, so the numbering scheme is one registration away from
/// changing — a utility migrating from a legacy billing system usually has to keep its own.
/// </summary>
public interface IBillNumberGenerator
{
    /// <summary>The next unused bill number.</summary>
    Task<string> NextBillNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The next <paramref name="count"/> unused bill numbers, in order — what a billing run
    /// reserves before it starts, because bills added to the context are invisible to the query
    /// that would issue the next number. See <see cref="RegistryNumberSeries.NextManyAsync"/>.
    /// </summary>
    Task<IReadOnlyList<string>> NextBillNumbersAsync(int count, CancellationToken cancellationToken = default);
}

/// <summary>
/// Continues the bill series from the highest number already issued, inside the caller's
/// transaction.
/// </summary>
/// <remarks>
/// One <see cref="RegistryNumberSeries.NextAsync"/> over this module's own column; the race with a
/// concurrent registration and the ordering trade it depends on are documented there, because every
/// registry shares them. A billing run issues a batch, so it asks once per bill and the unique index
/// is what makes the series safe rather than a lock.
/// </remarks>
public sealed class SequentialBillNumberGenerator(BillingDbContext database) : IBillNumberGenerator
{
    /// <inheritdoc />
    public Task<string> NextBillNumberAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(BillNumbers.BillNumberPrefix, Issued(), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> NextBillNumbersAsync(int count, CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextManyAsync(BillNumbers.BillNumberPrefix, Issued(), count, cancellationToken);

    private IQueryable<string> Issued() =>
        database.Bills
            .Where(bill => bill.BillNumber.StartsWith(BillNumbers.BillNumberPrefix))
            .OrderByDescending(bill => bill.BillNumber)
            .Select(bill => bill.BillNumber);
}
