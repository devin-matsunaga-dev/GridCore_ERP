namespace GridCore.Contracts.Events;

/// <summary>
/// A customer ended service at a premise and the account was closed (WP-2.15) — the standalone
/// move-out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from <see cref="ServiceAccountClosed"/>, which fires alongside it.</b> That event
/// states what happened to the account and is the fact WP-1.2 has always published; this one carries
/// what a move-out means to the money — the day service actually ended, and the reason code it ended
/// under. Billing's deepening pass raises the final bill off <see cref="EffectiveOn"/>, and a
/// consumer working from the closure alone would have only the instant a rep typed it.
/// </para>
/// <para>
/// <b>A transfer does not publish this.</b> It publishes <see cref="ServiceTransferred"/> instead,
/// because the two are answered differently: a move-out is a customer leaving, with a deposit to
/// return and a final bill to settle, while a transfer is the same customer continuing somewhere
/// else with their deposit carried. Splitting them here is what stops a later consumer from
/// refunding a deposit that was never released.
/// </para>
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the move-out was recorded.</param>
/// <param name="TransitionId">The row in the Customers schema that records it.</param>
/// <param name="CustomerId">Who left.</param>
/// <param name="ServiceAccountId">The account that was closed.</param>
/// <param name="AccountNumber">Its number, as printed.</param>
/// <param name="ServiceLocationId">The premise released.</param>
/// <param name="EffectiveOn">The day service ended — what a final bill is raised to.</param>
/// <param name="ReasonCode">The fixed-list code the move-out was made under, by name.</param>
/// <param name="Reason">Why, in the operator's words, where they added any.</param>
public sealed record ServiceMovedOut(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TransitionId,
    Guid CustomerId,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid ServiceLocationId,
    DateOnly EffectiveOn,
    string ReasonCode,
    string? Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static ServiceMovedOut For(
        DateTimeOffset occurredAt,
        Guid transitionId,
        Guid customerId,
        Guid serviceAccountId,
        string accountNumber,
        Guid serviceLocationId,
        DateOnly effectiveOn,
        string reasonCode,
        string? reason) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            transitionId,
            customerId,
            serviceAccountId,
            accountNumber,
            serviceLocationId,
            effectiveOn,
            reasonCode,
            reason);
}
