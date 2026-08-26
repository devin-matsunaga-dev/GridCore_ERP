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

    /// <summary>Source name recorded for a posting driven by <see cref="CustomerDepositCollected"/>.</summary>
    public const string DepositCollectedSource = "customers.deposit_collected";

    /// <summary>Source name recorded for a posting driven by <see cref="CustomerDepositApplied"/>.</summary>
    public const string DepositAppliedSource = "customers.deposit_applied";

    /// <summary>Source name recorded for a posting driven by <see cref="CustomerDepositRefunded"/>.</summary>
    public const string DepositRefundedSource = "customers.deposit_refunded";

    /// <summary>
    /// A bill raises a receivable and earns revenue — utility revenue for the supply, fee revenue
    /// for the published fees on it (WP-2.16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One receivable, two revenue accounts.</b> A fee is not electricity: crediting a
    /// reconnection charge to <see cref="FinanceAccounts.Revenue"/> would inflate what the utility
    /// appears to have earned from selling power, and the chart has carried
    /// <see cref="FinanceAccounts.ServiceFeeRevenue"/> since WP-0.8 waiting for exactly this. The
    /// debit is the total either way, because what the customer owes is one figure on one document.
    /// </para>
    /// <para>
    /// <b>Neither credit is posted when it is zero.</b> An ordinary cycle bill carries no fee and a
    /// counter bill is fees alone, so most entries have two lines and only a mixed bill has three —
    /// and a zero line would be refused anyway, by the guard in
    /// <see cref="JournalPostingIntent.For"/> that insists a line carries exactly one side.
    /// </para>
    /// </remarks>
    public static JournalPostingIntent From(BillIssued @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // Not read off the event as a second figure: what is left after the fees IS the supply, and
        // deriving it here is what makes the two credits add up to the debit by construction rather
        // than by an upstream promise.
        var supply = @event.Amount - @event.FeeAmount;

        List<JournalLineIntent> lines = [JournalLineIntent.Debits(FinanceAccounts.AccountsReceivable, @event.Amount)];

        if (supply != 0m)
        {
            lines.Add(JournalLineIntent.Credits(FinanceAccounts.Revenue, supply));
        }

        if (@event.FeeAmount != 0m)
        {
            lines.Add(JournalLineIntent.Credits(FinanceAccounts.ServiceFeeRevenue, @event.FeeAmount));
        }

        return JournalPostingIntent.For(
            @event.EventId,
            @event.OccurredAt,
            BillIssuedSource,
            @event.BillNumber,
            $"Bill {@event.BillNumber} issued for service account {@event.ServiceAccountId}.",
            @event.Currency,
            lines,
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

    /// <summary>
    /// A deposit taken is cash the utility holds and owes back.
    /// </summary>
    /// <remarks>
    /// <b>A liability, not revenue.</b> The money is in the bank, so cash is debited — but it was
    /// never earned, and crediting revenue would inflate what the utility has made by every deposit
    /// on its books. <see cref="FinanceAccounts.CustomerDeposits"/> has been in the chart since
    /// WP-0.8 waiting for exactly this.
    /// </remarks>
    public static JournalPostingIntent From(CustomerDepositCollected @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return JournalPostingIntent.For(
            @event.EventId,
            @event.OccurredAt,
            DepositCollectedSource,
            @event.AccountNumber,
            $"Security deposit of {@event.Amount:0.00} collected from customer {@event.AccountNumber}.",
            @event.Currency,
            [
                JournalLineIntent.Debits(FinanceAccounts.Cash, @event.Amount),
                JournalLineIntent.Credits(FinanceAccounts.CustomerDeposits, @event.Amount),
            ],

            // No service account: a deposit is held against the customer, not against one of the
            // premises they are served at. Passing an account here would attribute the liability to
            // whichever supply happened to be opened first.
            serviceAccountId: null,
            @event.CustomerId);
    }

    /// <summary>
    /// A deposit put against a bill settles the receivable out of money already held.
    /// </summary>
    /// <remarks>
    /// <b>No cash line, on either side.</b> The money entered the utility when the deposit was
    /// taken; what changes here is what it is held for — a liability owed back becomes a receivable
    /// no longer to be collected. An entry that touched cash would be recording the same money
    /// arriving twice.
    /// </remarks>
    public static JournalPostingIntent From(CustomerDepositApplied @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return JournalPostingIntent.For(
            @event.EventId,
            @event.OccurredAt,
            DepositAppliedSource,
            @event.BillNumber,
            $"Security deposit of {@event.Amount:0.00} applied to bill {@event.BillNumber}.",
            @event.Currency,
            [
                JournalLineIntent.Debits(FinanceAccounts.CustomerDeposits, @event.Amount),
                JournalLineIntent.Credits(FinanceAccounts.AccountsReceivable, @event.Amount),
            ],

            // The service account IS carried here, unlike a collection: this relieves a receivable,
            // and an AR view keyed on the account is what says whose debt went down.
            @event.ServiceAccountId,
            @event.CustomerId);
    }

    /// <summary>A deposit refunded is the collection run backwards: the liability is discharged in cash.</summary>
    /// <remarks>
    /// A new entry, never an unwinding of the collection — invariant 3. The debit and credit are the
    /// exact reverse of <see cref="From(CustomerDepositCollected)"/>, posted the right way round
    /// rather than as negative amounts, which is the same rule a bill credit follows.
    /// </remarks>
    public static JournalPostingIntent From(CustomerDepositRefunded @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return JournalPostingIntent.For(
            @event.EventId,
            @event.OccurredAt,
            DepositRefundedSource,
            @event.AccountNumber,
            $"Security deposit of {@event.Amount:0.00} refunded to customer {@event.AccountNumber}.",
            @event.Currency,
            [
                JournalLineIntent.Debits(FinanceAccounts.CustomerDeposits, @event.Amount),
                JournalLineIntent.Credits(FinanceAccounts.Cash, @event.Amount),
            ],
            serviceAccountId: null,
            @event.CustomerId);
    }
}
