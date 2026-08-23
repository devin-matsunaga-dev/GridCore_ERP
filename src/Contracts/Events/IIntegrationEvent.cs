namespace GridCore.Contracts.Events;

/// <summary>
/// A fact one module publishes for the others to react to. Past tense, always: an event says what
/// happened, never what should happen next.
/// </summary>
/// <remarks>
/// Every event carries its own identity so a consumer can be idempotent without inspecting the
/// transport — a redelivered message has the same <see cref="EventId"/>, which is exactly what
/// <c>IMessageDeduplicator</c> keys on. Ids are Guid v7 stamped from <see cref="OccurredAt"/>,
/// so the event stream sorts chronologically by key.
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>Identity of this event. Stable across redelivery; the idempotency key.</summary>
    Guid EventId { get; }

    /// <summary>When the fact became true in the publishing module — not when it was delivered.</summary>
    DateTimeOffset OccurredAt { get; }
}
