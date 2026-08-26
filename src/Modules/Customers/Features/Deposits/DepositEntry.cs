using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Deposits;

/// <summary>
/// One movement of a customer's security deposit: money taken, money put against a bill, or money
/// given back. Append-only — the deposit is a <i>balance made of entries</i>, never an amount
/// somebody edits.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the work package's central rule.</b> WORK_PACKAGES.md WP-2.12: "every transition is
/// an immutable entry — never an edited amount". A stored balance that a form could set is a
/// balance that disagrees with the ledger the first time somebody types over it, and the deposit is
/// the one figure on a customer's record that has a matching entry in the general ledger. So
/// <see cref="Customer.DepositHeld"/> stopped being editable in WP-2.12 and became the projection
/// of these rows.
/// </para>
/// <para>
/// <b>Every factory here moves the customer's balance itself, and that is the only way it moves.</b>
/// <see cref="Customer.RecordDepositMovement"/> is <see langword="internal"/> and this type is its
/// only caller — so an entry without a balance change, or a balance change without an entry, is not
/// a state the module can reach. Whoever adds a second writer takes the invariant with it; that is
/// the same warning WP-2.11 left on <c>CustomerContact</c> and the primary-method flag, and it is
/// load-bearing for the same reason: the guarantee lives in the aggregate because no database
/// constraint can express it.
/// </para>
/// <para>
/// <b>An aggregate root, not a child of <see cref="Customer"/>.</b> It names a customer by id, the
/// way <c>ServiceAccount</c> and <c>CustomerContact</c> do, so the registry list, the WP-2.9 search
/// and the 360's customer query are untouched by this work package — none of them wants to load a
/// customer's deposit history to render a row.
/// </para>
/// </remarks>
public sealed class DepositEntry
{
    /// <summary>Longest stored form of a kind name.</summary>
    public const int KindNameLength = 32;

    /// <summary>Longest ISO 4217 code stored. Three letters, with room to spare.</summary>
    public const int CurrencyLength = 8;

    /// <summary>Longest reason recorded against a movement.</summary>
    public const int ReasonLength = 1024;

    /// <summary>Longest bill number stored against an application.</summary>
    public const int BillNumberLength = RegistryNumbers.MaxLength;

    private DepositEntry()
    {
        // EF materialisation.
        Currency = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this entry. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The customer whose deposit moved.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>Which way it moved.</summary>
    public DepositEntryKind Kind { get; private init; }

    /// <summary>
    /// How much moved. <b>Always positive</b> — <see cref="Kind"/> carries the direction, and
    /// <see cref="SignedAmount"/> is where the two are put together.
    /// </summary>
    public decimal Amount { get; private init; }

    /// <summary>
    /// What the utility held once this entry was applied.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed, the call <c>BillAdjustment</c> already made for a bill's
    /// resulting amount due. A ledger that cannot show its own running balance is a ledger somebody
    /// has to re-add by hand to check, and the figure a customer was quoted at the counter is the
    /// one that was true at the time — not the one a later replay of the rows would produce.
    /// </remarks>
    public decimal BalanceAfter { get; private init; }

    /// <summary>ISO 4217 code the amount is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>
    /// Whether the holding earns interest. Set on a collection and stored; <b>nothing accrues on it
    /// in the MVP</b>, which is exactly what WORK_PACKAGES.md asks for — the terms are recorded so a
    /// later package has them rather than having to invent them retrospectively.
    /// </summary>
    public bool IsInterestBearing { get; private init; }

    /// <summary>The bill an application settled. <see langword="null"/> on every other kind.</summary>
    public Guid? BillId { get; private init; }

    /// <summary>The number printed on that bill, kept so the ledger reads without a cross-module lookup.</summary>
    public string? BillNumber { get; private init; }

    /// <summary>The service account that bill was raised against. <see langword="null"/> on every other kind.</summary>
    public Guid? ServiceAccountId { get; private init; }

    /// <summary>Why, in the operator's words.</summary>
    public string? Reason { get; private init; }

    /// <summary>Subject id of whoever did it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>The effect on the balance: the magnitude with its direction applied.</summary>
    public decimal SignedAmount => DepositEntryKinds.DirectionOf(Kind) * Amount;

    /// <summary>
    /// Takes a deposit from <paramref name="customer"/> and holds it against their account.
    /// </summary>
    /// <remarks>
    /// <b>No cap here.</b> WP-2.8 refuses an <i>intake</i> that collects more than the schedule asks
    /// for, and that rule stays where it is — it is about assessing a new customer. A later
    /// collection has no such ceiling: a deposit applied to a bill leaves a balance to rebuild, and
    /// a customer asked for more after a run of arrears is ordinary utility business. Inventing a
    /// ceiling here would refuse both.
    /// </remarks>
    /// <exception cref="RegistryValidationException">The amount is not positive, or is finer than a cent.</exception>
    public static DepositEntry Collect(
        Customer customer,
        decimal amount,
        string currency,
        bool isInterestBearing,
        string? reason,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return Record(
            customer,
            DepositEntryKind.Collected,
            amount,
            currency,
            reason,
            actor,
            now,
            isInterestBearing: isInterestBearing);
    }

    /// <summary>
    /// Puts <paramref name="amount"/> of the held deposit against a bill the customer owes.
    /// </summary>
    /// <remarks>
    /// <b>Whether the bill can take it is not this type's question.</b> How much is outstanding on a
    /// bill lives in Billing and reaches this module through <c>IBillDirectory</c>, so the service
    /// asks before it calls — and the bill itself refuses an overpayment again when it consumes the
    /// event. What is enforced here is the half this aggregate owns: the deposit cannot go below
    /// zero, because the utility cannot hand over money it is not holding.
    /// </remarks>
    /// <exception cref="RegistryValidationException">The amount is not positive, or is finer than a cent.</exception>
    /// <exception cref="RegistryWorkflowException">More was applied than the customer's deposit holds.</exception>
    public static DepositEntry Apply(
        Customer customer,
        Guid billId,
        string billNumber,
        Guid serviceAccountId,
        decimal amount,
        string currency,
        string? reason,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);

        if (billId == Guid.Empty)
        {
            throw new RegistryValidationException("Applying a deposit needs the bill it is applied to.");
        }

        return Record(
            customer,
            DepositEntryKind.Applied,
            amount,
            currency,
            reason,
            actor,
            now,
            billId: billId,
            billNumber: RegistryText.Clean(billNumber, BillNumberLength),
            serviceAccountId: serviceAccountId);
    }

    /// <summary>Gives <paramref name="amount"/> of the held deposit back to the customer.</summary>
    /// <exception cref="RegistryValidationException">The amount is not positive, or is finer than a cent.</exception>
    /// <exception cref="RegistryWorkflowException">More was refunded than the customer's deposit holds.</exception>
    public static DepositEntry Refund(
        Customer customer,
        decimal amount,
        string currency,
        string? reason,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return Record(customer, DepositEntryKind.Refunded, amount, currency, reason, actor, now);
    }

    /// <summary>
    /// Builds the entry and moves the customer's balance in one act.
    /// </summary>
    /// <remarks>
    /// Every guard runs before either mutation, so a refused movement leaves the customer exactly as
    /// it was rather than half-applied — the rule <c>Customer.UpdateDetails</c> already follows.
    /// </remarks>
    private static DepositEntry Record(
        Customer customer,
        DepositEntryKind kind,
        decimal amount,
        string currency,
        string? reason,
        RegistryActor actor,
        DateTimeOffset now,
        bool isInterestBearing = false,
        Guid? billId = null,
        string? billNumber = null,
        Guid? serviceAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (amount <= Money.Zero)
        {
            throw new RegistryValidationException(
                $"A deposit {kind.ToString().ToLowerInvariant()} movement must be positive; '{amount}' is not. "
                + "The kind carries the direction, so money going out is still a positive amount.");
        }

        // Refused rather than rounded: this is a figure somebody typed at a counter, not one
        // GridCore computed. The call WP-1.1 made for a deposit finer than a cent, and Money.IsRounded
        // is how a guard asks (DECISIONS.md, WP-2.3).
        if (!Money.IsRounded(amount))
        {
            throw new RegistryValidationException($"A deposit movement must be a whole number of cents; '{amount}' is not.");
        }

        var code = RegistryText.Clean(currency, CurrencyLength)
            ?? throw new RegistryValidationException("A deposit movement must name the currency it is in.");

        var signed = DepositEntryKinds.DirectionOf(kind) * amount;

        // Moves the balance FIRST and reads it back, so BalanceAfter is what the customer actually
        // holds rather than a figure computed alongside it. RecordDepositMovement refuses to go
        // negative, which is what turns "a refund cannot exceed the held balance" into one guard
        // covering every kind that takes money out.
        customer.RecordDepositMovement(signed, kind);

        return new DepositEntry
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customer.Id,
            Kind = kind,
            Amount = amount,
            BalanceAfter = customer.DepositHeld,
            Currency = code,
            IsInterestBearing = isInterestBearing,
            BillId = billId,
            BillNumber = billNumber,
            ServiceAccountId = serviceAccountId,
            Reason = RegistryText.Clean(reason, ReasonLength),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new RegistryValidationException("A deposit entry must name who moved the money."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }
}
