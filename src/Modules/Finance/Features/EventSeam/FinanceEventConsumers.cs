using GridCore.Contracts.Events;
using GridCore.Platform.Messaging;

namespace GridCore.Modules.Finance.Features.EventSeam;

/// <summary>
/// Posts the receivable when Billing issues a bill.
/// </summary>
/// <remarks>
/// A pure adapter: <see cref="IdempotentConsumer{TEvent}"/> owns the transport, the transaction and
/// the deduplication, <see cref="FinancePostings"/> owns the accounting. There is nothing left here
/// to get wrong, which is the point.
/// </remarks>
public sealed class BillIssuedConsumer(IdempotentEventHandler handler, IJournalPostingSeam journal)
    : IdempotentConsumer<BillIssued>(handler)
{
    /// <summary>Stable dedupe identity. Never rename: a new name replays every past bill.</summary>
    public const string Name = "finance.bill-issued";

    /// <inheritdoc />
    protected override string ConsumerName => Name;

    /// <inheritdoc />
    protected override Task ConsumeAsync(BillIssued message, CancellationToken cancellationToken) =>
        journal.PostAsync(FinancePostings.From(message), cancellationToken);
}

/// <summary>
/// Posts the correction when Billing adjusts an already-issued bill.
/// </summary>
/// <remarks>
/// <b>A second entry, never a rewrite of the first.</b> The receivable raised on
/// <see cref="BillIssued"/> stays exactly as it was posted — invariant 3 makes a ledger correction
/// a new entry — and this puts the change beside it, so a trial balance and an AR view agree with
/// what Billing says the customer owes without anything having been edited. Without this consumer
/// the ledger would keep saying the original figure and AR would diverge from Billing the first
/// time a disputed bill was credited.
/// </remarks>
public sealed class BillAdjustedConsumer(IdempotentEventHandler handler, IJournalPostingSeam journal)
    : IdempotentConsumer<BillAdjusted>(handler)
{
    /// <summary>Stable dedupe identity. Never rename: a new name replays every past correction.</summary>
    public const string Name = "finance.bill-adjusted";

    /// <inheritdoc />
    protected override string ConsumerName => Name;

    /// <inheritdoc />
    protected override Task ConsumeAsync(BillAdjusted message, CancellationToken cancellationToken) =>
        journal.PostAsync(FinancePostings.From(message), cancellationToken);
}

/// <summary>Posts the cash receipt when a payment is approved.</summary>
public sealed class PaymentApprovedConsumer(IdempotentEventHandler handler, IJournalPostingSeam journal)
    : IdempotentConsumer<PaymentApproved>(handler)
{
    /// <summary>Stable dedupe identity. Never rename: a new name replays every past payment.</summary>
    public const string Name = "finance.payment-approved";

    /// <inheritdoc />
    protected override string ConsumerName => Name;

    /// <inheritdoc />
    protected override Task ConsumeAsync(PaymentApproved message, CancellationToken cancellationToken) =>
        journal.PostAsync(FinancePostings.From(message), cancellationToken);
}

/// <summary>Posts the payable when Inventory receives goods.</summary>
public sealed class GoodsReceivedConsumer(IdempotentEventHandler handler, IJournalPostingSeam journal)
    : IdempotentConsumer<GoodsReceived>(handler)
{
    /// <summary>Stable dedupe identity. Never rename: a new name replays every past receipt.</summary>
    public const string Name = "finance.goods-received";

    /// <inheritdoc />
    protected override string ConsumerName => Name;

    /// <inheritdoc />
    protected override Task ConsumeAsync(GoodsReceived message, CancellationToken cancellationToken) =>
        journal.PostAsync(FinancePostings.From(message), cancellationToken);
}
