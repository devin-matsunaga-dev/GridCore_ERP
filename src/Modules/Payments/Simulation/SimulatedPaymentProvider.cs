using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Platform.Simulation;

namespace GridCore.Modules.Payments.Simulation;

/// <summary>
/// The MVP's payment provider: a sandbox gateway, standing in for the card acquirer or bank
/// integration a production deployment would configure in its place.
/// </summary>
/// <remarks>
/// <para>
/// This is the only thing in GridCore that decides whether money moved, and it lives behind
/// <see cref="IPaymentProvider"/> so nothing in the domain ever calls it by name (ARCHITECTURE.md
/// invariant 6). It answers with an outcome and a reference and nothing else: it does not know what
/// a bill is, does not reduce a balance, and never publishes anything. The Payments module works
/// out what the answer means, and Billing and Finance hear about it only as a fact that money
/// arrived — which is exactly what they will have to do with a real gateway.
/// </para>
/// <para>
/// <b>Reproducibility is the requirement, not a nicety.</b> The same payment number always gets the
/// same answer, on every machine, because the stream is keyed on the <i>number</i> rather than on
/// the id — a Guid v7 carries random bits, so keying on one would make a demonstration's outcomes
/// different on every database. That is WP-2.2's rule, restated for the second simulator.
/// </para>
/// <para>
/// <b>A retry can succeed.</b> A refused payment is retried as a new attempt with a new number, so
/// it draws a new stream and may well be approved — which is what a customer trying a second card
/// actually experiences, and what makes the failure path demonstrable rather than a dead end.
/// </para>
/// <para>
/// <b>Forced outcomes, the way every real sandbox does it.</b> An instrument ending in one of the
/// suffixes below always produces the same refusal, so a demonstration can show a decline on
/// purpose instead of taking payments until one happens. Everything else falls to the stream.
/// </para>
/// <para>
/// The clock is injected rather than reached for: the <i>outcome</i> is what has to be reproducible
/// — that is the demonstration and the tests — while the instant a gateway answered is genuinely
/// the wall clock's to say.
/// </para>
/// </remarks>
public sealed class SimulatedPaymentProvider(TimeProvider clock) : IPaymentProvider
{
    /// <summary>
    /// The sandbox's fixed seed. <b>Never changed</b> — it decides which payment numbers are
    /// refused, so moving it silently rewrites every demonstration and every test that pins one.
    /// </summary>
    public const int Seed = 8317;

    /// <summary>An instrument ending in this is always refused by the issuer.</summary>
    public const string DeclinedInstrumentSuffix = "0002";

    /// <summary>An instrument ending in this always comes back short of funds.</summary>
    public const string InsufficientFundsInstrumentSuffix = "9995";

    /// <summary>An instrument ending in this never answers in time.</summary>
    public const string TimeoutInstrumentSuffix = "0000";

    /// <summary>How often an ordinary attempt is refused by the issuer.</summary>
    public const decimal DeclinedChance = 0.04m;

    /// <summary>How often an ordinary attempt comes back short of funds.</summary>
    public const decimal InsufficientFundsChance = 0.03m;

    /// <summary>How often the gateway does not answer in time.</summary>
    public const decimal TimeoutChance = 0.02m;

    /// <summary>The scope the sandbox's streams are drawn under. See <see cref="DeterministicRandom.For"/>.</summary>
    private const string Scope = "payment-authorization";

    /// <inheritdoc />
    public string Name => "Simulated payment gateway";

    /// <inheritdoc />
    public Task<PaymentAuthorizationResult> AuthorizeAsync(
        PaymentAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = Decide(request);

        return Task.FromResult(new PaymentAuthorizationResult(
            outcome,
            ReferenceFor(request),
            clock.GetUtcNow(),
            MessageFor(outcome)));
    }

    /// <summary>
    /// What the sandbox answers. Never <see cref="PaymentOutcome.Refunded"/> — a refund is a
    /// different act, and this method is only ever asked to take money.
    /// </summary>
    private static PaymentOutcome Decide(PaymentAuthorizationRequest request)
    {
        // Cash is already in the drawer. A gateway that could decline notes and coins would be a
        // gateway modelling something that does not happen, and a demonstration in which the till
        // refuses a customer standing at the counter reads as a bug.
        if (string.Equals(request.Method, PaymentMethods.Cash, StringComparison.Ordinal))
        {
            return PaymentOutcome.Approved;
        }

        if (Forced(request.Instrument) is { } forced)
        {
            return forced;
        }

        // Keyed on the payment NUMBER, not the id — see DeterministicRandom.For.
        var stream = DeterministicRandom.For(Seed, Scope, request.Reference);

        // Drawn once and compared against cumulative bands, so the outcome does not depend on how
        // many values an earlier branch happened to consume. WP-2.2's shape.
        var draw = stream.NextUnit();

        if (draw < DeclinedChance)
        {
            return PaymentOutcome.Declined;
        }

        if (draw < DeclinedChance + InsufficientFundsChance)
        {
            return PaymentOutcome.InsufficientFunds;
        }

        if (draw < DeclinedChance + InsufficientFundsChance + TimeoutChance)
        {
            return PaymentOutcome.Timeout;
        }

        return PaymentOutcome.Approved;
    }

    /// <summary>
    /// The outcome an instrument is pinned to, or <see langword="null"/> where it is not pinned to
    /// one.
    /// </summary>
    private static PaymentOutcome? Forced(string? instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument))
        {
            return null;
        }

        var trimmed = instrument.Trim();

        return trimmed switch
        {
            _ when trimmed.EndsWith(DeclinedInstrumentSuffix, StringComparison.Ordinal) => PaymentOutcome.Declined,
            _ when trimmed.EndsWith(InsufficientFundsInstrumentSuffix, StringComparison.Ordinal) => PaymentOutcome.InsufficientFunds,
            _ when trimmed.EndsWith(TimeoutInstrumentSuffix, StringComparison.Ordinal) => PaymentOutcome.Timeout,
            _ => null,
        };
    }

    /// <summary>
    /// The reference the sandbox reconciles under. Derived from the payment number rather than
    /// invented, so a demonstration's references are the same on every machine — and prefixed, so
    /// nobody mistakes a sandbox reference for a real acquirer's on a bank statement.
    /// </summary>
    private static string ReferenceFor(PaymentAuthorizationRequest request) =>
        $"SIM-{request.Reference}";

    /// <summary>
    /// What the provider says about it. Deliberately the sort of thing a real gateway returns:
    /// enough for a clerk to explain the refusal, never enough to identify the instrument.
    /// </summary>
    private static string? MessageFor(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Approved => null,
        PaymentOutcome.Declined => "Refused by the issuing bank.",
        PaymentOutcome.InsufficientFunds => "Refused: the account does not hold the funds.",
        PaymentOutcome.Timeout => "The gateway did not answer in time; the payment may or may not have been taken.",
        _ => null,
    };
}

