using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.Features.ServiceAccounts;

/// <summary>
/// One line of an account's service history: what it moved from, what it moved to, why, and who
/// did it. Append-only — a history is a record of what happened, so a mistake is corrected by the
/// next transition rather than by editing the last one.
/// </summary>
/// <remarks>
/// Deliberately not a replacement for the audit trail. The audit entry (invariant 1) is the
/// tamper-evident administrative record of a write, held in the platform schema and filtered by
/// action; this is the customer-facing service record, read back on the account page and quoted to
/// a caller asking when their supply was cut. They answer different questions and are written in
/// the same transaction, so neither can exist without the other.
/// </remarks>
public sealed class ServiceAccountHistoryEntry
{
    /// <summary>Longest reason recorded against a transition.</summary>
    public const int ReasonLength = 1024;

    private ServiceAccountHistoryEntry()
    {
        // EF materialisation.
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this entry. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The account this line belongs to.</summary>
    public Guid ServiceAccountId { get; private init; }

    /// <summary>Where the account was. <see langword="null"/> on the opening line — it came from nowhere.</summary>
    public ServiceAccountStatus? FromStatus { get; private init; }

    /// <summary>Where the account went.</summary>
    public ServiceAccountStatus ToStatus { get; private init; }

    /// <summary>Why, in the operator's words.</summary>
    public string? Reason { get; private init; }

    /// <summary>Subject id of whoever did it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>Records a transition. Called only by <see cref="ServiceAccount"/>, which is what stops a status moving without a line.</summary>
    internal static ServiceAccountHistoryEntry For(
        Guid serviceAccountId,
        ServiceAccountStatus? from,
        ServiceAccountStatus to,
        string? reason,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new ServiceAccountHistoryEntry
        {
            Id = Guid.CreateVersion7(now),
            ServiceAccountId = serviceAccountId,
            FromStatus = from,
            ToStatus = to,
            Reason = RegistryText.Clean(reason, ReasonLength),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength) ?? throw new RegistryValidationException("An account history entry must name who made the change."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }
}
