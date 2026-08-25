namespace GridCore.Contracts.Providers;

/// <summary>
/// What a payment provider answered. The five outcomes SPEC.md and WORK_PACKAGES.md name, and no
/// others — a provider that could invent a sixth would be a provider whose answers the domain had
/// to interpret rather than act on.
/// </summary>
/// <remarks>
/// <para>
/// <b>An outcome is not a status.</b> This is the provider's verbatim answer; what it means for a
/// payment is the Payments module's own business — <c>InsufficientFunds</c> and <c>Declined</c>
/// both leave a payment declined, while <c>Timeout</c> leaves one that failed and may or may not
/// have moved money. Storing both is what lets somebody on the phone say <i>why</i> rather than
/// only <i>whether</i>.
/// </para>
/// <para>
/// <b><see cref="Refunded"/> is not an answer to <see cref="IPaymentProvider.AuthorizeAsync"/>.</b>
/// It is declared here because it is one of the outcomes this seam has to be able to carry, and
/// because widening the set later would mean revisiting every consumer that switched over it; the
/// act that produces it is a refund, which nothing in GridCore performs yet. The simulator asserts
/// its own charge path never returns it.
/// </para>
/// </remarks>
public enum PaymentOutcome
{
    /// <summary>The money moved. The only outcome that reduces what a customer owes.</summary>
    Approved = 1,

    /// <summary>Refused by the issuer, without a reason the utility is entitled to.</summary>
    Declined = 2,

    /// <summary>
    /// Refused because the account was short. Told apart from <see cref="Declined"/> deliberately:
    /// it is the one refusal a customer can do something about, and the one a clerk can explain.
    /// </summary>
    InsufficientFunds = 3,

    /// <summary>
    /// The provider did not answer in time. <b>Not a refusal</b> — the money may have moved and the
    /// answer been lost, which is why it must never be treated as a decline and retried blindly.
    /// </summary>
    Timeout = 4,

    /// <summary>Money returned to the customer. The answer to a refund, never to a charge.</summary>
    Refunded = 5,
}

/// <summary>One payment the utility wants taken, as the payment provider is told about it.</summary>
/// <remarks>
/// Everything a provider needs to charge and nothing it does not: no bill, no premise, no meter,
/// no customer name. A real gateway is handed an amount, a currency, an instrument and an
/// idempotency key, and that is exactly what this carries — which is what lets the sandbox be
/// swapped for one by DI config alone (ARCHITECTURE.md's provider rule).
/// </remarks>
/// <param name="PaymentId">
/// GridCore's own identifier for the attempt, which a real gateway takes as its idempotency key —
/// so a retried request charges once rather than twice.
/// </param>
/// <param name="Reference">
/// The number the utility knows the attempt by, e.g. <c>PAY-000001</c>, for the provider's own
/// logs and for reconciliation. Stable across machines, unlike the id.
/// </param>
/// <param name="Amount">How much to take. Money is <see langword="decimal"/>, never a float.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="Method">How it is being paid, e.g. <c>card</c>, <c>bank-transfer</c> or <c>cash</c>.</param>
/// <param name="Instrument">
/// The instrument being charged, as the utility is allowed to hold it — a masked card tail, a
/// mandate reference, or <see langword="null"/> where the method needs none. <b>Never a full card
/// number:</b> GridCore does not take one, does not store one, and is not in scope to.
/// </param>
public sealed record PaymentAuthorizationRequest(
    Guid PaymentId,
    string Reference,
    decimal Amount,
    string Currency,
    string Method,
    string? Instrument);

/// <summary>What a provider came back with for one payment.</summary>
/// <param name="Outcome">The provider's answer.</param>
/// <param name="ProviderReference">
/// The provider's own reference, which is what a bank statement is reconciled against. Present on
/// every outcome, including the refusals — "which attempt was this" is asked of failures more often
/// than of successes.
/// </param>
/// <param name="ProcessedAt">When the provider decided.</param>
/// <param name="Message">What the provider wants recorded against it, where anything is.</param>
public sealed record PaymentAuthorizationResult(
    PaymentOutcome Outcome,
    string ProviderReference,
    DateTimeOffset ProcessedAt,
    string? Message);

/// <summary>
/// Where money comes from — the simulation seam for payments, and the reason no domain code in
/// GridCore ever calls a payment sandbox by name (ARCHITECTURE.md invariant 6).
/// </summary>
/// <remarks>
/// <para>
/// The MVP's implementation is a sandbox that approves most attempts and produces the refusals a
/// demonstration needs. Production swaps it for a real gateway through DI configuration, with
/// nothing in the Payments module changed: the module knows what an approval means to a bill and a
/// ledger, and nothing about how the money was actually moved.
/// </para>
/// <para>
/// A provider <b>charges an instrument and reports what happened</b>. It never decides what its own
/// answer means: whether a payment is declined or failed, whether the balance moves, and whether
/// Finance hears about it are all worked out inside Payments from what came back. The same split
/// <see cref="IMeterReadingProvider"/> draws, and for the same reason — a real provider cannot be
/// trusted to classify its own output.
/// </para>
/// </remarks>
public interface IPaymentProvider
{
    /// <summary>
    /// What this provider is, stamped on every payment it answers. A record of where money came
    /// from outlives whichever implementation was configured at the time.
    /// </summary>
    string Name { get; }

    /// <summary>Charges <paramref name="request"/> and reports the outcome.</summary>
    /// <remarks>
    /// Never throws for a refusal: a decline is an answer, not a failure. It throws only when the
    /// provider could not be asked at all, which is a different thing from
    /// <see cref="PaymentOutcome.Timeout"/> — that one means the provider was asked and may have
    /// acted.
    /// </remarks>
    Task<PaymentAuthorizationResult> AuthorizeAsync(
        PaymentAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}
