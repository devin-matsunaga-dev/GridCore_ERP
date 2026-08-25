using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>
/// Which tariff a service account is billed on. Billing's own row about somebody else's account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Billing owns this, and Customers is not touched</b> (owner's call). A
/// <c>rate_plan_code</c> column on <c>customers.service_accounts</c> would put a billing concern in
/// another module's schema and another module's migrations, which ARCHITECTURE.md's boundary rule
/// forbids outright — and it would make the Customers module the place a tariff change is recorded,
/// audited and argued about. The account is a plain <see cref="ServiceAccountId"/> here with no
/// foreign key, exactly as a meter reading holds a premise: the id comes from a directory lookup
/// that has already proved the account exists.
/// </para>
/// <para>
/// <b>It stores a code, never a plan version id.</b> An account is on "the residential tariff", not
/// on "the residential tariff as published in January" — the whole point of effective dating is that
/// a repricing reaches every account on that tariff without anybody reassigning them. Which version
/// applies is decided per bill, from the period being billed
/// (<see cref="RatePlanSelector.InForceOn(IEnumerable{RatePlan}, DateOnly)"/>).
/// </para>
/// <para>
/// <b>No row is also an answer.</b> An account with no assignment bills on the default tariff, so
/// nothing has to be assigned before the utility can bill — which is invariant 8's spirit applied to
/// a running system: a migrated database with no billing setup at all still produces correct bills.
/// </para>
/// </remarks>
public sealed class AccountRatePlan
{
    private AccountRatePlan()
    {
        // EF materialisation.
        RatePlanCode = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this assignment. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The service account, in the Customers schema. One assignment per account.</summary>
    public Guid ServiceAccountId { get; private init; }

    /// <summary>The tariff code it bills on, e.g. <c>COM-STD</c>.</summary>
    public string RatePlanCode { get; private set; }

    /// <summary>When the account was first put on a tariff of its own.</summary>
    public DateTimeOffset AssignedAt { get; private init; }

    /// <summary>When the tariff last changed, or <see langword="null"/> if it never has.</summary>
    public DateTimeOffset? ChangedAt { get; private set; }

    /// <summary>Subject id of whoever last set it.</summary>
    public string ActorId { get; private set; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private set; }

    /// <summary>Puts <paramref name="serviceAccountId"/> on <paramref name="ratePlanCode"/>.</summary>
    /// <exception cref="BillingValidationException">The account or the code is missing.</exception>
    public static AccountRatePlan Assign(
        Guid serviceAccountId,
        string ratePlanCode,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (serviceAccountId == Guid.Empty)
        {
            throw new BillingValidationException("A rate plan assignment must name a service account.");
        }

        return new AccountRatePlan
        {
            Id = Guid.CreateVersion7(now),
            ServiceAccountId = serviceAccountId,
            RatePlanCode = Clean(ratePlanCode),
            AssignedAt = now,
            ActorId = ActorIdOf(actor),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
        };
    }

    /// <summary>
    /// Moves the account onto another tariff.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the tariff changed. <see langword="false"/> when it was already
    /// that one — assigning the tariff an account is already on is a no-op rather than a conflict,
    /// deliberately unlike WP-1.4's stock adjustment that agrees with the system: there the ledger
    /// would gain a line explaining nothing, here there is nothing to write at all.
    /// </returns>
    /// <exception cref="BillingValidationException">The code is missing.</exception>
    public bool ChangeTo(string ratePlanCode, RegistryActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var code = Clean(ratePlanCode);

        if (string.Equals(code, RatePlanCode, StringComparison.Ordinal))
        {
            return false;
        }

        RatePlanCode = code;
        ChangedAt = now;
        ActorId = ActorIdOf(actor);
        ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength);

        return true;
    }

    private static string Clean(string ratePlanCode) =>
        RegistryText.Clean(ratePlanCode, RatePlan.CodeLength)
        ?? throw new BillingValidationException("A rate plan assignment must name a rate plan.");

    private static string ActorIdOf(RegistryActor actor) =>
        RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
        ?? throw new BillingValidationException("A rate plan assignment must name who set it.");
}
