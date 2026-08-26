using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Transitions;

/// <summary>
/// One recorded transition: a customer re-classified, moved status, moved in, moved out, or moved
/// between premises. Append-only — a transition that turned out to be wrong is a new transition
/// back, never an edited row.
/// </summary>
/// <remarks>
/// <para>
/// <b>This register does not perform the move; it records why and from when.</b> The moves
/// themselves are WP-1.1's and WP-1.2's state machines, untouched by this package —
/// <c>CustomerTransitions</c>, <c>ServiceAccountTransitions</c> and the guards in
/// <c>ServiceAccountService.OpenAsync</c> still decide what is legal, and an illegal move is still a
/// 409 from the aggregate. What WP-2.15 adds is the <see cref="ReasonCode"/> the fixed list
/// requires, the <see cref="EffectiveOn"/> date the billing pass will price from, and — for a
/// transfer — the linkage that makes two account moves one act.
/// </para>
/// <para>
/// <b>A transfer is ONE row naming both accounts, not two rows sharing a link.</b> "Linked" is
/// precisely the property a pair of rows can lose: one written and the other not, one corrected and
/// the other not, one read without the other. A single row cannot half-exist, so the linkage needs
/// no consistency rule to hold it together. It is the reason
/// <see cref="FromServiceAccountId"/> and <see cref="ToServiceAccountId"/> are both nullable and
/// both set only here.
/// </para>
/// <para>
/// <b>An aggregate root, not a child of <see cref="Customer"/></b> — it names a customer by id, the
/// way <c>ServiceAccount</c>, <c>CustomerContact</c>, <c>DepositEntry</c> and <c>CustomerNote</c>
/// all do, so no list that renders a customer row has to load their history to draw it.
/// </para>
/// </remarks>
public sealed class AccountTransition
{
    /// <summary>Longest stored form of a kind or reason-code name.</summary>
    public const int EnumNameLength = 64;

    /// <summary>Longest value stored on either side of the move — a class name, a status name, an account number.</summary>
    public const int ValueLength = 64;

    /// <summary>Longest ISO 4217 code stored. Three letters, with room to spare.</summary>
    public const int CurrencyLength = 8;

    /// <summary>Longest free text recorded beside the reason code.</summary>
    public const int NotesLength = 1024;

    private AccountTransition()
    {
        // EF materialisation.
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this transition. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The customer it happened to. A transfer is one customer's, which is what makes it a transfer.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>What kind of move it was.</summary>
    public AccountTransitionKind Kind { get; private init; }

    /// <summary>The fixed-list code it was recorded under.</summary>
    public TransitionReasonCode ReasonCode { get; private init; }

    /// <summary>What the operator wrote beside the code. Required with <see cref="TransitionReasonCode.Other"/> and optional otherwise.</summary>
    public string? Notes { get; private init; }

    /// <summary>
    /// The day the change applies from — <b>not</b> the day it was typed.
    /// </summary>
    /// <remarks>
    /// The two are different on purpose and the difference is what the billing pass consumes: a rep
    /// records on the 3rd that a customer became commercial on the 1st, or will on the 1st of next
    /// month. <see cref="RecordedAt"/> is when it was typed; this is when the utility says it
    /// happened.
    /// </remarks>
    public DateOnly EffectiveOn { get; private init; }

    /// <summary>
    /// What it was before — a class name, a status name, or the number of the account released.
    /// <see cref="Kind"/> says which. <see langword="null"/> on a move-in, which had no before.
    /// </summary>
    public string? FromValue { get; private init; }

    /// <summary>
    /// What it became — a class name, a status name, or the number of the account opened.
    /// <see langword="null"/> on a move-out, which has no after.
    /// </summary>
    public string? ToValue { get; private init; }

    /// <summary>The account closed, on a move-out or a transfer. <see langword="null"/> otherwise.</summary>
    public Guid? FromServiceAccountId { get; private init; }

    /// <summary>The account opened, on a move-in or a transfer. <see langword="null"/> otherwise.</summary>
    public Guid? ToServiceAccountId { get; private init; }

    /// <summary>
    /// How much held deposit rode along. Positive on a transfer that carried one, and
    /// <see cref="Money.Zero"/> on everything else.
    /// </summary>
    /// <remarks>
    /// <b>A figure that moved nowhere, which is the point.</b> The deposit is held against the
    /// customer rather than against an account, so a transfer between two of one customer's accounts
    /// takes no money and returns none. This records what was carried so a reader can see that "no
    /// net money created" holds, without adding up a ledger to check.
    /// </remarks>
    public decimal DepositCarried { get; private init; }

    /// <summary>
    /// ISO 4217 code <see cref="DepositCarried"/> is expressed in, or <see langword="null"/> where
    /// no money was involved — which is every kind but a transfer, and a transfer of a customer
    /// holding nothing. A currency stamped on a row with no figure would read as a claim.
    /// </summary>
    public string? Currency { get; private init; }

    /// <summary>The deposit ledger entry that carried it, where there was anything to carry.</summary>
    public Guid? DepositEntryId { get; private init; }

    /// <summary>Subject id of whoever did it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it was recorded. See <see cref="EffectiveOn"/> for when it applies.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>Records a customer's move between classes.</summary>
    /// <exception cref="RegistryValidationException">The reason code does not fit a class change, or needs notes it was not given.</exception>
    public static AccountTransition ClassChanged(
        Customer customer,
        CustomerClass from,
        CustomerClass to,
        TransitionReasonCode reasonCode,
        string? notes,
        DateOnly effectiveOn,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return Record(
            customer,
            AccountTransitionKind.ClassChanged,
            reasonCode,
            notes,
            effectiveOn,
            actor,
            now,
            fromValue: from.ToString(),
            toValue: to.ToString());
    }

    /// <summary>Records a customer's move between statuses.</summary>
    /// <exception cref="RegistryValidationException">The reason code does not fit a status change, or needs notes it was not given.</exception>
    public static AccountTransition StatusChanged(
        Customer customer,
        CustomerStatus from,
        CustomerStatus to,
        TransitionReasonCode reasonCode,
        string? notes,
        DateOnly effectiveOn,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return Record(
            customer,
            AccountTransitionKind.StatusChanged,
            reasonCode,
            notes,
            effectiveOn,
            actor,
            now,
            fromValue: from.ToString(),
            toValue: to.ToString());
    }

    /// <summary>Records service being taken up at a premise the customer was not being served at.</summary>
    /// <exception cref="RegistryValidationException">The reason code does not fit a move-in, or needs notes it was not given.</exception>
    public static AccountTransition MovedIn(
        Customer customer,
        ServiceAccount opened,
        TransitionReasonCode reasonCode,
        string? notes,
        DateOnly effectiveOn,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(opened);

        return Record(
            customer,
            AccountTransitionKind.MovedIn,
            reasonCode,
            notes,
            effectiveOn,
            actor,
            now,
            toValue: opened.AccountNumber,
            toServiceAccountId: opened.Id);
    }

    /// <summary>Records service ending at a premise, with the account closed behind it.</summary>
    /// <exception cref="RegistryValidationException">The reason code does not fit a move-out, or needs notes it was not given.</exception>
    public static AccountTransition MovedOut(
        Customer customer,
        ServiceAccount closed,
        TransitionReasonCode reasonCode,
        string? notes,
        DateOnly effectiveOn,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(closed);

        return Record(
            customer,
            AccountTransitionKind.MovedOut,
            reasonCode,
            notes,
            effectiveOn,
            actor,
            now,
            fromValue: closed.AccountNumber,
            fromServiceAccountId: closed.Id);
    }

    /// <summary>
    /// Records one customer's service moving from one premise to another, with whatever deposit rode
    /// along.
    /// </summary>
    /// <remarks>
    /// <paramref name="depositCarried"/> is a magnitude, never a movement: the deposit is the
    /// customer's and both accounts are the customer's, so nothing left the utility and nothing
    /// arrived. Zero is ordinary — a customer holding no deposit transfers perfectly well, and no
    /// ledger entry is written for them.
    /// </remarks>
    /// <exception cref="RegistryValidationException">
    /// The reason code does not fit a transfer, needs notes it was not given, the two accounts are
    /// the same, or the carried figure is negative or finer than a cent.
    /// </exception>
    public static AccountTransition Transferred(
        Customer customer,
        ServiceAccount closed,
        ServiceAccount opened,
        decimal depositCarried,
        string currency,
        Guid? depositEntryId,
        TransitionReasonCode reasonCode,
        string? notes,
        DateOnly effectiveOn,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(closed);
        ArgumentNullException.ThrowIfNull(opened);

        if (closed.Id == opened.Id)
        {
            throw new RegistryValidationException(
                $"Account {closed.AccountNumber} cannot be transferred to itself; a transfer moves service between two premises.");
        }

        if (depositCarried < Money.Zero)
        {
            throw new RegistryValidationException(
                $"A transfer carries a deposit of '{depositCarried}', which is not a balance anybody can hold. "
                + "A carry is a magnitude, not a movement — nothing leaves the utility on a transfer.");
        }

        if (!Money.IsRounded(depositCarried))
        {
            throw new RegistryValidationException($"A carried deposit is stated to the cent; '{depositCarried}' is not.");
        }

        return Record(
            customer,
            AccountTransitionKind.Transferred,
            reasonCode,
            notes,
            effectiveOn,
            actor,
            now,
            fromValue: closed.AccountNumber,
            toValue: opened.AccountNumber,
            fromServiceAccountId: closed.Id,
            toServiceAccountId: opened.Id,
            depositCarried: depositCarried,
            currency: currency,
            depositEntryId: depositEntryId);
    }

    /// <summary>
    /// Builds the row, having checked the two things every kind shares: that the code fits the kind,
    /// and that a code obliged to explain itself did.
    /// </summary>
    private static AccountTransition Record(
        Customer customer,
        AccountTransitionKind kind,
        TransitionReasonCode reasonCode,
        string? notes,
        DateOnly effectiveOn,
        RegistryActor actor,
        DateTimeOffset now,
        string? fromValue = null,
        string? toValue = null,
        Guid? fromServiceAccountId = null,
        Guid? toServiceAccountId = null,
        decimal depositCarried = 0m,
        string? currency = null,
        Guid? depositEntryId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // A value cast from an unmapped integer would be stored by name as a number and read back as
        // nothing anyone can act on — the guard Customer.Register already makes for a class.
        if (!Enum.IsDefined(reasonCode))
        {
            throw new RegistryValidationException($"'{reasonCode}' is not a {nameof(TransitionReasonCode)} GridCore declares.");
        }

        // The fixed list, enforced in the aggregate rather than only at the edge — so a seeder, a
        // later module and the service all meet it, which is the rule CONVENTIONS.md asks for and the
        // reason `Customer.ChangeStatus` takes a code at all.
        if (!TransitionReasons.IsAllowed(kind, reasonCode))
        {
            throw new RegistryValidationException(
                $"'{reasonCode}' is not a reason a {kind} may be recorded under. "
                + $"Allowed: {string.Join(", ", TransitionReasons.For(kind))}.");
        }

        var written = RegistryText.Clean(notes, NotesLength);

        if (TransitionReasons.RequiresNotes(reasonCode) && written is null)
        {
            throw new RegistryValidationException(
                $"A {kind} recorded as '{reasonCode}' has to say what actually happened. "
                + "The fixed list is only fixed if its escape hatch explains itself.");
        }

        return new AccountTransition
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customer.Id,
            Kind = kind,
            ReasonCode = reasonCode,
            Notes = written,
            EffectiveOn = effectiveOn,
            FromValue = RegistryText.Clean(fromValue, ValueLength),
            ToValue = RegistryText.Clean(toValue, ValueLength),
            FromServiceAccountId = fromServiceAccountId,
            ToServiceAccountId = toServiceAccountId,
            DepositCarried = depositCarried,
            Currency = RegistryText.Clean(currency, CurrencyLength),
            DepositEntryId = depositEntryId,
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new RegistryValidationException("A transition must name who made it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }
}
