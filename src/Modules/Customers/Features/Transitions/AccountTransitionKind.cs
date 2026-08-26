namespace GridCore.Modules.Customers.Features.Transitions;

/// <summary>
/// What one recorded transition was: a change to what the customer <i>is</i>, or a change to where
/// they are served.
/// </summary>
/// <remarks>
/// <para>
/// The five members are WORK_PACKAGES.md WP-2.15's two changes, spelled out. The first two move the
/// customer record; the last three move service between premises, and
/// <see cref="Transferred"/> is the pair of them done as one act.
/// </para>
/// <para>
/// Stored by name, so the numbering here is never load-bearing. Adding a member means giving it a
/// reason list in <see cref="TransitionReasons"/> — which throws rather than defaulting, so a kind
/// added without one cannot be recorded at all.
/// </para>
/// </remarks>
public enum AccountTransitionKind
{
    /// <summary>Residential became commercial, or the reverse. Picks a different tariff from the effective date forward.</summary>
    ClassChanged,

    /// <summary>The customer record moved status — activated, suspended, closed.</summary>
    StatusChanged,

    /// <summary>An existing customer took service at a premise they were not being served at.</summary>
    MovedIn,

    /// <summary>Service ended at a premise and the account was closed. What triggers a final bill.</summary>
    MovedOut,

    /// <summary>
    /// Service moved from one premise to another for the <b>same</b> customer, as one linked act:
    /// the old account closed, a new one opened, and the deposit carried between them.
    /// </summary>
    Transferred,
}

/// <summary>
/// Why a transition was made — the fixed list WORK_PACKAGES.md WP-2.15 requires beside the free
/// text.
/// </summary>
/// <remarks>
/// <para>
/// <b>One list, not one per kind, with <see cref="TransitionReasons"/> saying which codes fit
/// which.</b> Several codes genuinely belong to more than one kind — a customer's own request moves
/// a status, moves them in, moves them out and transfers them — and a code per kind would have four
/// spellings of it that a report would then have to add up. The map is the one place the pairing
/// lives, and it is what both the API and the browser's select read.
/// </para>
/// <para>
/// <b><see cref="Other"/> is the escape hatch and it is the one code that must explain itself.</b> A
/// fixed list without one is a list somebody defeats by picking the nearest wrong code; a fixed list
/// whose escape hatch may be silent is a fixed list in name only. See
/// <see cref="TransitionReasons.RequiresNotes"/>.
/// </para>
/// </remarks>
public enum TransitionReasonCode
{
    /// <summary>None of the codes below fits. Free text is required with it, and only with it.</summary>
    Other,

    /// <summary>The customer asked for it. Legal on every kind but a class change, which is a fact rather than a request.</summary>
    CustomerRequest,

    /// <summary>A household started trading from the premise. Moves them to the commercial tariff.</summary>
    PremiseNowTrading,

    /// <summary>A business closed and the premise is a home again. Moves them back to the residential tariff.</summary>
    PremiseNowResidential,

    /// <summary>The class was wrong from the day of intake — a correction, not a change in circumstances.</summary>
    MisclassifiedAtIntake,

    /// <summary>Money owed and not paid. What a suspension is normally made under.</summary>
    UnpaidBalance,

    /// <summary>What was owed has been settled. What a reinstatement is made under.</summary>
    BalanceSettled,

    /// <summary>The utility is not satisfied the customer is who they say they are.</summary>
    IdentityDisputed,

    /// <summary>The customer has died. Legal on a status move and on a move-out.</summary>
    Deceased,

    /// <summary>Somebody has taken up residence at a premise that was not being served.</summary>
    NewOccupancy,

    /// <summary>A tenancy ended. The ordinary reason a rented premise is closed.</summary>
    EndOfTenancy,

    /// <summary>The premise has been left empty and nobody is taking over the supply.</summary>
    PropertyVacated,

    /// <summary>The structure is gone. The premise cannot be served again as it stands.</summary>
    PropertyDemolished,

    /// <summary>The customer is moving house. The reason a transfer normally carries.</summary>
    Relocation,
}

/// <summary>
/// Which reason codes are legal against which transition, and which of them has to say more.
/// </summary>
/// <remarks>
/// Pure and static, so a UI can render the right select without holding an entity — the call
/// <c>CustomerTransitions</c> and <c>ServiceAccountTransitions</c> already made for the state
/// machines this package sits on top of.
/// </remarks>
public static class TransitionReasons
{
    private static readonly Dictionary<AccountTransitionKind, TransitionReasonCode[]> Allowed = new()
    {
        // No CustomerRequest: a class is what the premise is used for, not what its occupant would
        // prefer to be billed as. A customer who asks to be re-classified is telling the utility
        // that one of the three below has happened, and the record should say which.
        [AccountTransitionKind.ClassChanged] =
        [
            TransitionReasonCode.PremiseNowTrading,
            TransitionReasonCode.PremiseNowResidential,
            TransitionReasonCode.MisclassifiedAtIntake,
            TransitionReasonCode.Other,
        ],

        [AccountTransitionKind.StatusChanged] =
        [
            TransitionReasonCode.CustomerRequest,
            TransitionReasonCode.UnpaidBalance,
            TransitionReasonCode.BalanceSettled,
            TransitionReasonCode.IdentityDisputed,
            TransitionReasonCode.Deceased,
            TransitionReasonCode.Other,
        ],

        [AccountTransitionKind.MovedIn] =
        [
            TransitionReasonCode.NewOccupancy,
            TransitionReasonCode.Relocation,
            TransitionReasonCode.CustomerRequest,
            TransitionReasonCode.Other,
        ],

        [AccountTransitionKind.MovedOut] =
        [
            TransitionReasonCode.EndOfTenancy,
            TransitionReasonCode.PropertyVacated,
            TransitionReasonCode.PropertyDemolished,
            TransitionReasonCode.Deceased,
            TransitionReasonCode.CustomerRequest,
            TransitionReasonCode.Other,
        ],

        // A transfer is a customer moving house, so the list is short on purpose: the codes that
        // would end a supply for good — a demolition, a death — describe a move-OUT, and offering
        // them here would let a rep record a customer as having left while opening them an account
        // somewhere else.
        [AccountTransitionKind.Transferred] =
        [
            TransitionReasonCode.Relocation,
            TransitionReasonCode.CustomerRequest,
            TransitionReasonCode.Other,
        ],
    };

    /// <summary>The reason codes a transition of <paramref name="kind"/> may be recorded under.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The kind is not one GridCore declares. Not an empty list: a kind added without a reason list
    /// would silently become a transition nobody could ever record, and the failure would surface as
    /// a 400 on a legal request rather than as the missing line it is.
    /// </exception>
    public static IReadOnlyList<TransitionReasonCode> For(AccountTransitionKind kind) =>
        Allowed.TryGetValue(kind, out var codes)
            ? codes
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a transition kind GridCore declares.");

    /// <summary>Whether <paramref name="code"/> may be recorded against a transition of <paramref name="kind"/>.</summary>
    public static bool IsAllowed(AccountTransitionKind kind, TransitionReasonCode code) => For(kind).Contains(code);

    /// <summary>
    /// Whether <paramref name="code"/> obliges the operator to write something as well.
    /// </summary>
    /// <remarks>
    /// True for <see cref="TransitionReasonCode.Other"/> and nothing else. Every other code already
    /// says what happened, and demanding a sentence beside "End of tenancy" would train a desk to
    /// type a full stop — which is worse than nothing, because it reads as an explanation.
    /// </remarks>
    public static bool RequiresNotes(TransitionReasonCode code) => code is TransitionReasonCode.Other;
}
