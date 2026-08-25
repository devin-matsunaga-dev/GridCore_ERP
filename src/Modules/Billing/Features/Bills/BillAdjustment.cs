using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>Which way an adjustment moves what a bill is owed.</summary>
/// <remarks>
/// A kind and a positive amount rather than a signed figure the operator types. WP-1.4 made the
/// same call about a stock take — somebody states the fact and the system does the subtraction —
/// because "we are eight short" entered as <c>8</c> is how a correction goes the wrong way. The
/// stored <see cref="BillAdjustment.Amount"/> is signed; the typed one never is.
/// </remarks>
public enum BillAdjustmentKind
{
    /// <summary>Money off. The ordinary case: an estimated read corrected, a disputed charge conceded.</summary>
    Credit = 1,

    /// <summary>
    /// Money on. A correction can run either way — a bill raised on a read that was too low leaves
    /// the customer owing the difference, and re-raising the whole document would give them two.
    /// </summary>
    Charge = 2,
}

/// <summary>
/// One correction to an issued bill: which way, how much, why, and what the bill came to afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, and the bill it corrects is never rewritten.</b> That is invariant 3's habit —
/// a ledger correction is a new entry, never an edit — applied to a document rather than to the
/// general ledger. <see cref="Bill.TotalAmount"/> keeps saying what was calculated and printed;
/// what the customer now owes is that plus every adjustment since. A bill whose own total moved
/// under it is a bill nobody can reconcile against the copy in the customer's hand.
/// </para>
/// <para>
/// <b>Not a bill line.</b> Lines are what the rate engine produced, written once by
/// <see cref="Bill.Calculate"/>, and their sum is asserted to equal the total — the money guard
/// WP-2.3 built. An adjustment arriving as a line would either break that assertion or force the
/// total to move, which is the same thing twice.
/// </para>
/// <para>
/// <b>Not a replacement for the audit trail either.</b> The audit entry (invariant 1) is the
/// platform's tamper-evident record of the write; this is billing's own record of the money. Both
/// are written in the same transaction, so neither can exist without the other — the split
/// <c>StockMovement</c> and <c>assets.asset_history</c> already make.
/// </para>
/// </remarks>
public sealed class BillAdjustment
{
    private BillAdjustment()
    {
        // EF materialisation.
        Reason = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this adjustment. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The bill it corrects.</summary>
    public Guid BillId { get; private init; }

    /// <summary>
    /// Position in the bill's adjustment history, from 1. Ordered on explicitly rather than on the
    /// key: two adjustments minted inside one millisecond have no defined order by Guid v7, and the
    /// order these were applied in is what makes <see cref="AmountDueAfter"/> read down the page.
    /// </summary>
    public int Sequence { get; private init; }

    /// <summary>Which way it moves what is owed.</summary>
    public BillAdjustmentKind Kind { get; private init; }

    /// <summary>
    /// Signed: negative on a credit, positive on a charge. Signed rather than a magnitude beside the
    /// kind, so summing the history reproduces what the bill comes to without a lookup table.
    /// </summary>
    public decimal Amount { get; private init; }

    /// <summary>What the bill came to once this adjustment was applied.</summary>
    public decimal AmountDueAfter { get; private init; }

    /// <summary>Why. Never optional — this is the sensitive action invariant 5 is about.</summary>
    public string Reason { get; private init; }

    /// <summary>Subject id of whoever adjusted it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>
    /// Writes one correction onto <paramref name="billId"/>.
    /// </summary>
    /// <remarks>
    /// Internal, and takes an amount <see cref="Bill.Adjust"/> has already checked and signed. The
    /// guards live on the aggregate because they are questions about the bill — is it still owed,
    /// is the credit larger than the balance — which a line on its own cannot answer.
    /// </remarks>
    internal static BillAdjustment Record(
        Guid billId,
        int sequence,
        BillAdjustmentKind kind,
        decimal signedAmount,
        decimal amountDueAfter,
        string reason,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new BillAdjustment
        {
            Id = Guid.CreateVersion7(now),
            BillId = billId,
            Sequence = sequence,
            Kind = kind,
            Amount = signedAmount,
            AmountDueAfter = amountDueAfter,
            Reason = RegistryText.Clean(reason, Bill.ReasonLength)
                ?? throw new BillingValidationException("An adjustment must say why the bill is being corrected."),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new BillingValidationException("An adjustment must name who made it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }

    /// <summary>
    /// <paramref name="amount"/> signed the way <paramref name="kind"/> moves the money.
    /// </summary>
    /// <exception cref="BillingValidationException">The kind is not one this module knows.</exception>
    internal static decimal Signed(BillAdjustmentKind kind, decimal amount) => kind switch
    {
        BillAdjustmentKind.Credit => -amount,
        BillAdjustmentKind.Charge => amount,

        // An enum value cast in from the wire. Refused rather than defaulted to either direction:
        // guessing which way an unknown correction moves money is the one thing worse than failing.
        _ => throw new BillingValidationException($"'{kind}' is not a kind of adjustment a bill can carry."),
    };

    /// <summary>
    /// Checks an adjustment is a figure somebody could have typed on a form — positive, and exact to
    /// the cent.
    /// </summary>
    /// <remarks>
    /// Refused rather than rounded, because this is a figure a person stated rather than one GridCore
    /// computed — the rule <c>Money</c> is explicit about and the same call
    /// <see cref="Bill.RecordPayment"/> makes about a payment.
    /// </remarks>
    /// <exception cref="BillingValidationException">The amount is not positive, or is finer than a cent.</exception>
    internal static decimal RequireAmount(decimal amount, string billNumber)
    {
        if (amount <= Money.Zero)
        {
            throw new BillingValidationException(
                $"An adjustment to bill {billNumber} must be a positive amount; '{amount}' is not. "
                + "Which way it moves the money is the kind, not the sign.");
        }

        if (!Money.IsRounded(amount))
        {
            throw new BillingValidationException($"An adjustment is made to the cent; '{amount}' is finer than that.");
        }

        return amount;
    }
}
