using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Features.Documents;

/// <summary>Reproducing an issued bill as the document it was sent as (WP-2.14).</summary>
public interface IBillDocumentService
{
    /// <summary>
    /// The bill as issued, with its corrections listed separately — and an audit entry saying who
    /// produced it.
    /// </summary>
    /// <exception cref="BillNotFoundException">There is no bill with that id.</exception>
    /// <exception cref="BillingPermissionException">The caller may not produce customer documents.</exception>
    /// <exception cref="BillingWorkflowException">The bill was never issued, so there is no document.</exception>
    Task<BillDocument> ReprintAsync(Guid billId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The reprint, over the billing schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>A read that writes exactly one row: the audit entry.</b> Nothing about the bill moves — that
/// is what makes it a reprint — so this is the first caller in GridCore of
/// <see cref="IAuditLog.RecordAsync"/>, which is the form documented for an audited act with no
/// unit of work of its own to join. Wrapping a transaction around a single insert would say there is
/// something for it to be atomic with, and there is not.
/// </para>
/// <para>
/// <b>The entry is written after the document is built, never before.</b> A draft, or a bill whose
/// stored figures no longer add up, must not leave behind a trail entry claiming a copy went out —
/// <see cref="BillDocument.Of"/> throws first, and nothing has been recorded.
/// </para>
/// <para>
/// <b>The gate is demanded here as well as on the route</b>, per CONVENTIONS.md: a service enforces
/// its own permissions rather than trusting the endpoint that called it. The permission is
/// <c>customers.documents</c> rather than a billing one — see <see cref="Permissions.Customers.Documents"/>,
/// where the reason is written down: from the desk this is the same act as the statement beside it.
/// </para>
/// </remarks>
public sealed class BillDocumentService(
    BillingDbContext database,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : IBillDocumentService
{
    /// <inheritdoc />
    public async Task<BillDocument> ReprintAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(Permissions.Customers.Documents))
        {
            throw new BillingPermissionException(
                $"Producing a copy of a bill requires the '{Permissions.Customers.Documents}' permission. "
                + "Reading a bill on screen and sending a customer a copy of it are different acts.");
        }

        // With the lines AND the whole adjustment history — BillDocument.Of refuses to print without
        // them, and this is the one caller that has to supply them.
        var bill = await database.Bills
            .AsNoTracking()
            .Include(bill => bill.Lines.OrderBy(line => line.Sequence))
            .Include(bill => bill.Adjustments.OrderBy(adjustment => adjustment.Sequence))
            .FirstOrDefaultAsync(bill => bill.Id == billId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new BillNotFoundException(billId);

        var document = BillDocument.Of(bill, RegistryActor.Of(currentUser), clock.GetUtcNow());

        // No before and no after: nothing changed. What the trail records is that a document with
        // this customer's consumption and address on it was produced, by whom, and when — the
        // question asked of a reprint months later, and the reason a read is audited at all.
        await audit
            .RecordAsync(
                AuditActions.BillReprinted,
                AuditEntityTypes.Bill,
                bill.Id.ToString(),
                before: null,
                after: BillReprintSnapshot.Of(document),
                cancellationToken)
            .ConfigureAwait(false);

        return document;
    }
}

/// <summary>
/// The shape a reprint is audited as: which document went out, for whom, and what it said.
/// </summary>
/// <remarks>
/// The figures are here so the entry can be read on its own years later — "we sent them a copy
/// saying they owed 214.60" is the sentence somebody needs, and following the bill id to a row that
/// has since been credited twice more does not produce it.
/// </remarks>
/// <param name="BillNumber">The number on the document.</param>
/// <param name="CustomerId">Who it is about.</param>
/// <param name="CustomerName">Their name as the bill was raised in.</param>
/// <param name="AccountNumber">The account billed.</param>
/// <param name="PeriodStart">First day of the billed period.</param>
/// <param name="PeriodEnd">Last day of it.</param>
/// <param name="PrintedTotal">What the document said.</param>
/// <param name="AmountDue">What was owed on it when the copy was produced.</param>
/// <param name="Balance">What was still outstanding then.</param>
/// <param name="Corrections">How many corrections it carried.</param>
public sealed record BillReprintSnapshot(
    string BillNumber,
    Guid CustomerId,
    string CustomerName,
    string AccountNumber,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal PrintedTotal,
    decimal AmountDue,
    decimal Balance,
    int Corrections)
{
    /// <summary>Takes a snapshot of the document that was produced.</summary>
    public static BillReprintSnapshot Of(BillDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new BillReprintSnapshot(
            document.BillNumber,
            document.CustomerId,
            document.CustomerName,
            document.AccountNumber,
            document.PeriodStart,
            document.PeriodEnd,
            document.PrintedTotal,
            document.AmountDue,
            document.Balance,
            document.Corrections.Count);
    }
}
