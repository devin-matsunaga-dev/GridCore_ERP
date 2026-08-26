namespace GridCore.Contracts.Events;

/// <summary>
/// A customer's service moved from one premise to another as one linked act (WP-2.15) — the account
/// at the old premise closed, a new one opened at the new, and the deposit carried between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>One event, not a move-out beside a move-in.</b> The two halves of a transfer are written in
/// one transaction and mean nothing apart: a consumer that saw the closure alone would raise a final
/// bill and release a deposit for a customer who has not left. Everything a later pass needs to tell
/// the two apart is on this record — both accounts, both premises, and what was carried.
/// </para>
/// <para>
/// <b><see cref="DepositCarried"/> is a figure that moved nowhere.</b> The deposit is held against
/// the <i>customer</i>, not against an account, so a transfer takes no money and returns none; this
/// says how much rode along, which is what makes "no net money created" checkable by a reader rather
/// than only by a test. It is zero when the customer was holding nothing, and no deposit ledger
/// entry is written in that case — a movement of nothing is a row nobody can reconcile.
/// </para>
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the transfer was recorded.</param>
/// <param name="TransitionId">The row in the Customers schema that records it.</param>
/// <param name="CustomerId">Who moved.</param>
/// <param name="FromServiceAccountId">The account closed at the old premise.</param>
/// <param name="FromAccountNumber">Its number, as printed.</param>
/// <param name="FromServiceLocationId">The premise released.</param>
/// <param name="ToServiceAccountId">The account opened at the new premise.</param>
/// <param name="ToAccountNumber">Its number, as printed.</param>
/// <param name="ToServiceLocationId">The premise taken up.</param>
/// <param name="EffectiveOn">The day service moved — what the final and first bills are cut on.</param>
/// <param name="DepositCarried">How much held deposit rode along. Always positive or zero; never a movement.</param>
/// <param name="Currency">ISO 4217 code the carried deposit is expressed in.</param>
/// <param name="ReasonCode">The fixed-list code the transfer was made under, by name.</param>
/// <param name="Reason">Why, in the operator's words, where they added any.</param>
public sealed record ServiceTransferred(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TransitionId,
    Guid CustomerId,
    Guid FromServiceAccountId,
    string FromAccountNumber,
    Guid FromServiceLocationId,
    Guid ToServiceAccountId,
    string ToAccountNumber,
    Guid ToServiceLocationId,
    DateOnly EffectiveOn,
    decimal DepositCarried,
    string Currency,
    string ReasonCode,
    string? Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static ServiceTransferred For(
        DateTimeOffset occurredAt,
        Guid transitionId,
        Guid customerId,
        Guid fromServiceAccountId,
        string fromAccountNumber,
        Guid fromServiceLocationId,
        Guid toServiceAccountId,
        string toAccountNumber,
        Guid toServiceLocationId,
        DateOnly effectiveOn,
        decimal depositCarried,
        string currency,
        string reasonCode,
        string? reason) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            transitionId,
            customerId,
            fromServiceAccountId,
            fromAccountNumber,
            fromServiceLocationId,
            toServiceAccountId,
            toAccountNumber,
            toServiceLocationId,
            effectiveOn,
            depositCarried,
            currency,
            reasonCode,
            reason);
}
