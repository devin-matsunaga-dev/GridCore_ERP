using GridCore.Contracts.Events;
using GridCore.Modules.Finance.Features.ChartOfAccounts;

namespace GridCore.Modules.Finance.Features.EventSeam;

/// <summary>
/// The event→journal mapping: what each upstream fact means in double entry. Pure and static, so
/// the accounting is verified in the fast tier without a bus, a broker or a database.
/// </summary>
/// <remarks>
/// Finance is downstream of everyone (ARCHITECTURE.md): it reads these events and posts, and never
/// calls back into Billing, Payments or Inventory to ask anything. Everything an entry needs is on
/// the event — including the service account and customer it concerns, which is what lets an AR
/// view name who owes the money without Finance ever seeing the <c>billing</c> schema.
/// </remarks>
public static class FinancePostings
{
    /// <summary>Source name recorded for a posting driven by <see cref="BillIssued"/>.</summary>
    public const string BillIssuedSource = "billing.bill_issued";

    /// <summary>Source name recorded for a posting driven by <see cref="BillAdjusted"/>.</summary>
    public const string BillAdjustedSource = "billing.bill_adjusted";

    /// <summary>Source name recorded for a posting driven by <see cref="PaymentApproved"/>.</summary>
    public const string PaymentApprovedSource = "payments.payment_approved";

    /// <summary>Source name recorded for a posting driven by <see cref="GoodsReceived"/>.</summary>
    public const string GoodsReceivedSource = "inventory.goods_received";

    /// <summary>A bill raises a receivable and earns revenue.</summary>
    public static JournalPostingIntent From(BillIssued @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return JournalPostingIntent.For(
            @event.EventId,
            @event.OccurredAt,
            BillIssuedSource,
            @event.BillNumber,
            $"Bill {@event.BillNumber} issued for service account {@event.ServiceAccountId}.",
            @event.Currency,
            [
                JournalLineIntent.Debits(FinanceAccounts.AccountsReceivable, @event.Amount),
                JournalLineIntent.Credits(FinanceAccounts.Revenue, @event.Amount),
            ],
            @event.ServiceAccountId,
            @event.CustomerId);
    }

    /// <summary>
    /// A correction to an issued bill moves the receivable and the revenue with it — a second
    /// balanced entry, never a rewrite of the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is invariant 3 doing the work it exists for. The bill's own document still says what it
    /// said, the entry raised on <see cref="BillIssued"/> still says what it said, and the change is
    /// a new pair of lines — so a trial balance and an AR view agree with what Billing thinks the
    /// customer owes without anything having been edited.
    /// </para>
    /// <para>
    /// <b>The direction comes from the sign, and the lines are never signed.</b>
    /// <see cref="BillAdjusted.Amount"/> is negative for a credit, which is a debit to revenue and a
    /// credit to receivables — the exact reverse of issuing. A charge is the same way round as
    /// issuing. Posting the magnitude on the correct side rather than a negative debit is what keeps
    /// a trial balance readable: a negative debit and a credit are the same money and only one of
    /// them can be added up by eye.
    /// </para>
    /// </remarks>
    public static JournalPostingIntent From(BillAdjusted @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var magnitude = Math.Abs(@event.Amount);

        // A zero adjustment never reaches here — Bill.Adjust refuses one — and if one ever did, the
        // one-sided-line guard in JournalPostingIntent.For refuses to post an entry that moves no
        // money. Neither branch below is the place to discover that, so neither checks for it.
        JournalLineIntent[] lines = @event.Amount < 0
            ? [
                JournalLineIntent.Debits(FinanceAccounts.Revenue, magnitude),
                JournalLineIntent.Credits(FinanceAccounts.AccountsReceivable, magnitude),
            ]
            : [
                JournalLineIntent.Debits(FinanceAccounts.AccountsReceivable, magnitude),
                JournalLineIntent.Credits(FinanceAccounts.Revenue, magnitude),
            ];

        return JournalPostingIntent.For(
            @event.EventId,
            @event.OccurredAt,
            BillAdjustedSource,
            @event.BillNumber,
            $"Bill {@event.BillNumber} adjusted ({@event.Kind.ToLowerInvariant()}): {@event.Reason}",
            @event.Currency,
            lines,
            @event.ServiceAccountId,
            @event.CustomerId);
    }

    /// <summary>An approved payment turns a receivable into cash.</summary>
    public static JournalPostingIntent From(PaymentApproved @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return JournalPostingIntent.For(
            @event.EventId,
            @event.OccurredAt,
            PaymentApprovedSource,
            @event.ProviderReference,
            $"Payment {@event.ProviderReference} ({@event.Method}) approved for service account {@event.ServiceAccountId}.",
            @event.Currency,
            [
                JournalLineIntent.Debits(FinanceAccounts.Cash, @event.Amount),
                JournalLineIntent.Credits(FinanceAccounts.AccountsReceivable, @event.Amount),
            ],
            @event.ServiceAccountId,
            @event.CustomerId);
    }

    /// <summary>Received goods become inventory owed to a vendor.</summary>
    /// <remarks>
    /// No subsidiary dimension: the party here is a vendor, and an AP view keyed on one belongs with
    /// the procurement lifecycle that raises the purchase order (WP-4.1). Passing the vendor id into
    /// a field an AR view reads would put a supplier on the receivables ledger.
    /// </remarks>
    public static JournalPostingIntent From(GoodsReceived @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return JournalPostingIntent.For(
            @event.EventId,
            @event.OccurredAt,
            GoodsReceivedSource,
            @event.PurchaseOrderId.ToString(),
            $"Goods receipt {@event.ReceiptId} against purchase order {@event.PurchaseOrderId}.",
            @event.Currency,
            [
                JournalLineIntent.Debits(FinanceAccounts.Inventory, @event.TotalCost),
                JournalLineIntent.Credits(FinanceAccounts.AccountsPayable, @event.TotalCost),
            ]);
    }
}
