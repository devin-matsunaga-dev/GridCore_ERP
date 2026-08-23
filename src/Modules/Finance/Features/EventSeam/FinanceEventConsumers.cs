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
