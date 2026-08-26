namespace GridCore.Contracts.Events;

/// <summary>
/// A customer moved between classes — residential to commercial, or back (WP-2.15).
/// </summary>
/// <remarks>
/// <para>
/// <b>The event Billing's deepening pass consumes to pick a different rate.</b> A class decides
/// which tariff applies, so this is the fact that says "from <see cref="EffectiveOn"/>, price this
/// customer differently". Nothing consumes it yet, deliberately: WORK_PACKAGES.md makes the billing
/// half of WP-2.15 a stub, and the package's job is to record the effective dates and reasons that
/// pass will read rather than to guess at how it will read them.
/// </para>
/// <para>
/// <b><see cref="EffectiveOn"/> is not <see cref="IIntegrationEvent.OccurredAt"/>, and the
/// difference is the whole point.</b> The instant is when a rep typed it; the effective date is when
/// the utility says the customer became commercial, which may be the first of next month. A consumer
/// that priced from the instant would bill a fortnight of business use at the household rate and
/// have no record that it had.
/// </para>
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the change was recorded.</param>
/// <param name="TransitionId">The row in the Customers schema that records it.</param>
/// <param name="CustomerId">Whose class moved.</param>
/// <param name="AccountNumber">The number they quote, so a consumer reads as something a person recognises.</param>
/// <param name="FromClass">What they were, by name — Contracts takes no dependency on the module's enum.</param>
/// <param name="ToClass">What they became.</param>
/// <param name="EffectiveOn">The day the new class applies from.</param>
/// <param name="ReasonCode">The fixed-list code the change was made under, by name.</param>
/// <param name="Reason">Why, in the operator's words, where they added any.</param>
public sealed record CustomerClassChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TransitionId,
    Guid CustomerId,
    string AccountNumber,
    string FromClass,
    string ToClass,
    DateOnly EffectiveOn,
    string ReasonCode,
    string? Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static CustomerClassChanged For(
        DateTimeOffset occurredAt,
        Guid transitionId,
        Guid customerId,
        string accountNumber,
        string fromClass,
        string toClass,
        DateOnly effectiveOn,
        string reasonCode,
        string? reason) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            transitionId,
            customerId,
            accountNumber,
            fromClass,
            toClass,
            effectiveOn,
            reasonCode,
            reason);
}
