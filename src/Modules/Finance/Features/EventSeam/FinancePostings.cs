using GridCore.Contracts.Events;
using GridCore.Modules.Finance.Features.ChartOfAccounts;

namespace GridCore.Modules.Finance.Features.EventSeam;

/// <summary>
/// The event→journal mapping: what each upstream fact means in double entry. Pure and static, so
/// the accounting is verified in the fast tier without a bus, a broker or a database.
/// </summary>
/// <remarks>
/// Finance is downstream of everyone (ARCHITECTURE.md): it reads these events and posts, and never
/// calls back into Billing, Payments or Inventory to ask anything.
/// </remarks>
public static class FinancePostings
{
    /// <summary>Source name recorded for a posting driven by <see cref="BillIssued"/>.</summary>
    public const string BillIssuedSource = "billing.bill_issued";

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
            ]);
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
            ]);
    }

    /// <summary>Received goods become inventory owed to a vendor.</summary>
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
