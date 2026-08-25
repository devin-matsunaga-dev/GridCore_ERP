using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>
/// What a bill was raised from: the reading that closed the period, and the dials either side of it.
/// </summary>
/// <remarks>
/// Stamped onto the bill rather than resolved through the reading register whenever somebody looks.
/// The register is append-only so the reading will still be there — but the meter's register width
/// may since have been corrected, and the device may by then be on somebody else's wall, so
/// re-deriving the figures would not reliably reproduce the bill. WP-2.2 made the same call about
/// consumption for the same reason.
/// </remarks>
/// <param name="ReadingId">The reading in Metering's register that closed the period.</param>
/// <param name="MeterId">The meter it came off.</param>
/// <param name="MeterNumber">That meter's number, as printed.</param>
/// <param name="PreviousReading">The dials at the start of the period.</param>
/// <param name="CurrentReading">The dials at the end of it.</param>
public sealed record BilledReading(
    Guid ReadingId,
    Guid MeterId,
    string MeterNumber,
    decimal? PreviousReading,
    decimal? CurrentReading);

/// <summary>
/// A bill: one billing period for one service account, priced by one version of one tariff.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything a bill needs to be read is on the bill.</b> The account number, the customer's
/// name, the premise, the meter, the dials, the tariff's code and name and effective date, the
/// currency, every line and its rate. That is not denormalisation for speed — a bill is a document
/// the utility has to be able to reproduce and defend years later, and every one of those facts
/// belongs to another module that is free to change it. Resolving them at read time would give a
/// customer a different bill on a second look.
/// </para>
/// <para>
/// <b>The total is checked against the lines, always.</b> Invariant 3 makes Finance assert
/// debits = credits; the equivalent here is that a bill's amount is the sum of what is printed on
/// it. <see cref="Calculate"/> throws rather than store a document that does not add up.
/// </para>
/// <para>
/// Lines are a navigation collection, unlike the reading register hanging off a meter (WP-2.2). A
/// bill has a handful of lines, they are always read with it, and there is no path that loads a bill
/// and does not want them — the opposite of a stock ledger or a decade of readings.
/// </para>
/// </remarks>
public sealed class Bill
{
    /// <summary>Longest stored form of a status name.</summary>
    public const int EnumNameLength = 32;

    /// <summary>Longest reason recorded against a transition.</summary>
    public const int ReasonLength = 512;

    /// <summary>Longest cycle code stored, matching the reading register's.</summary>
    public const int CycleCodeLength = 32;

    /// <summary>Longest name stored — a customer's, a tariff's.</summary>
    public const int NameLength = 256;

    /// <summary>Total digits a money column stores.</summary>
    public const int MoneyPrecision = Money.Precision;

    /// <summary>Decimal places a money column stores.</summary>
    public const int MoneyScale = Money.DecimalPlaces;

    /// <summary>Total digits a quantity column stores.</summary>
    public const int QuantityPrecision = 18;

    /// <summary>Decimal places a consumption or dial figure carries.</summary>
    public const int QuantityScale = RateEngine.ConsumptionDecimalPlaces;

    private readonly List<BillLine> _lines = [];

    private Bill()
    {
        // EF materialisation.
        BillNumber = string.Empty;
        AccountNumber = string.Empty;
        CustomerName = string.Empty;
        RatePlanCode = string.Empty;
        RatePlanName = string.Empty;
        Currency = string.Empty;
        UnitOfMeasure = string.Empty;
        MeterNumber = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this bill. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The number printed on it, e.g. <c>BIL-000001</c>. Unique across bills.</summary>
    public string BillNumber { get; private init; }

    /// <summary>The service account billed, in the Customers schema.</summary>
    public Guid ServiceAccountId { get; private init; }

    /// <summary>Its number, as printed. Stamped, because a bill has to be readable on its own.</summary>
    public string AccountNumber { get; private init; }

    /// <summary>The customer who owes it.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>Their name at the time the bill was raised.</summary>
    public string CustomerName { get; private init; }

    /// <summary>The premise supplied. A meter is fitted to a premise, so this is where the units came from.</summary>
    public Guid ServiceLocationId { get; private init; }

    /// <summary>The tariff version priced against.</summary>
    public Guid RatePlanId { get; private init; }

    /// <summary>Its code, as printed.</summary>
    public string RatePlanCode { get; private init; }

    /// <summary>Its name, as printed.</summary>
    public string RatePlanName { get; private init; }

    /// <summary>The day that tariff version took effect — why these rates and not others.</summary>
    public DateOnly RatePlanEffectiveFrom { get; private init; }

    /// <summary>ISO 4217 code every amount on this bill is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>What the units are measured in, e.g. <c>kWh</c>.</summary>
    public string UnitOfMeasure { get; private init; }

    /// <summary>First day of the billed period.</summary>
    public DateOnly PeriodStart { get; private init; }

    /// <summary>Last day of it — the day the meter was read.</summary>
    public DateOnly PeriodEnd { get; private init; }

    /// <summary>The reading cycle this bill came from, or <see langword="null"/> for an ad-hoc bill.</summary>
    public string? CycleCode { get; private init; }

    /// <summary>The reading in Metering's register that closed the period.</summary>
    public Guid MeterReadingId { get; private init; }

    /// <summary>The meter that produced it.</summary>
    public Guid MeterId { get; private init; }

    /// <summary>Its number, as printed.</summary>
    public string MeterNumber { get; private init; }

    /// <summary>The dials at the start of the period.</summary>
    public decimal? PreviousReading { get; private init; }

    /// <summary>The dials at the end of it.</summary>
    public decimal? CurrentReading { get; private init; }

    /// <summary>Units billed.</summary>
    public decimal Consumption { get; private init; }

    /// <summary>What the bill comes to — the sum of its lines, as printed.</summary>
    public decimal TotalAmount { get; private init; }

    /// <summary>How much of it has been paid.</summary>
    public decimal AmountPaid { get; private set; }

    /// <summary>Where the bill stands.</summary>
    public BillStatus Status { get; private set; }

    /// <summary>When it was calculated.</summary>
    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>The day it was issued, or <see langword="null"/> while it is a draft.</summary>
    public DateOnly? IssuedOn { get; private set; }

    /// <summary>The day payment falls due, or <see langword="null"/> while it is a draft.</summary>
    public DateOnly? DueDate { get; private set; }

    /// <summary>When it was fully settled.</summary>
    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>When the status last moved.</summary>
    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>Why it last moved.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>Subject id of whoever raised the bill.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>The lines, in order.</summary>
    public IReadOnlyList<BillLine> Lines => _lines;

    /// <summary>What is still owed.</summary>
    public decimal Balance => TotalAmount - AmountPaid;

    /// <summary>Whether the utility is still owed money on this bill.</summary>
    public bool IsOutstanding => BillTransitions.IsOutstanding(Status);

    /// <summary>The statuses this bill may move to, for rendering transition buttons.</summary>
    public IReadOnlyList<BillStatus> AllowedTransitions => BillTransitions.AllowedFrom(Status);

    /// <summary>
    /// Whether the bill is past its due date as at <paramref name="asOf"/> and still owed — what an
    /// overdue review asks before it moves anything.
    /// </summary>
    public bool IsPastDue(DateOnly asOf) => IsOutstanding && DueDate is { } due && asOf > due;

    /// <summary>
    /// Calculates a bill and holds it as a <see cref="BillStatus.Draft"/>.
    /// </summary>
    /// <param name="billNumber">The number to print on it, already reserved by the caller.</param>
    /// <param name="account">Who is billed, from the Customers module's directory.</param>
    /// <param name="reading">The reading that closed the period.</param>
    /// <param name="calculation">What the tariff made of the consumption.</param>
    /// <param name="periodStart">First day of the billed period.</param>
    /// <param name="periodEnd">Last day of it.</param>
    /// <param name="actor">Who raised it.</param>
    /// <param name="now">The clock, for the row's own identity and timestamp.</param>
    /// <param name="cycleCode">The reading cycle it came from, for a cycle bill.</param>
    /// <exception cref="BillingValidationException">
    /// The number is missing, the period runs backwards, or the calculation does not add up to its
    /// own lines.
    /// </exception>
    public static Bill Calculate(
        string billNumber,
        ServiceAccountSummary account,
        BilledReading reading,
        RateCalculation calculation,
        DateOnly periodStart,
        DateOnly periodEnd,
        RegistryActor actor,
        DateTimeOffset now,
        string? cycleCode = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(calculation);
        ArgumentNullException.ThrowIfNull(actor);

        // Every guard before the first line is built — WP-1.4's ordering rule.
        if (string.IsNullOrWhiteSpace(billNumber))
        {
            throw new BillingValidationException("A bill must be given a number before it can be raised.");
        }

        if (periodEnd < periodStart)
        {
            throw new BillingValidationException(
                $"A billing period cannot end before it starts; {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}.");
        }

        // THE MONEY GUARD. Invariant 3 makes Finance assert that a posting balances; the equivalent
        // here is that a bill equals the sum of what is printed on it. Refused rather than corrected:
        // a total silently replaced by the sum of the lines would hide whatever produced the
        // disagreement, and the next bill would carry it too.
        var printed = Money.Total(calculation.Charges.Select(charge => charge.Amount));

        if (printed != calculation.Total)
        {
            throw new BillingValidationException(
                $"Bill {billNumber} totals {calculation.Total} but its lines add up to {printed}. "
                + "A bill must equal the sum of what is printed on it.");
        }

        if (!Money.IsRounded(calculation.Total))
        {
            throw new BillingValidationException(
                $"Bill {billNumber} totals {calculation.Total}, which is finer than a cent.");
        }

        var bill = new Bill
        {
            Id = Guid.CreateVersion7(now),
            BillNumber = billNumber.Trim(),
            ServiceAccountId = account.Id,
            AccountNumber = account.AccountNumber,
            CustomerId = account.CustomerId,
            CustomerName = RegistryText.Clean(account.CustomerName, NameLength) ?? account.AccountNumber,
            ServiceLocationId = account.ServiceLocationId,
            RatePlanId = calculation.RatePlanId,
            RatePlanCode = calculation.RatePlanCode,
            RatePlanName = calculation.RatePlanName,
            RatePlanEffectiveFrom = calculation.EffectiveFrom,
            Currency = calculation.Currency,
            UnitOfMeasure = calculation.UnitOfMeasure,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            CycleCode = RegistryText.Clean(cycleCode, CycleCodeLength),
            MeterReadingId = reading.ReadingId,
            MeterId = reading.MeterId,
            MeterNumber = reading.MeterNumber,
            PreviousReading = reading.PreviousReading,
            CurrentReading = reading.CurrentReading,
            Consumption = calculation.Consumption,
            TotalAmount = calculation.Total,
            AmountPaid = Money.Zero,
            Status = BillStatus.Draft,
            CreatedAt = now,
            StatusChangedAt = now,
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new BillingValidationException("A bill must name who raised it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
        };

        foreach (var charge in calculation.Charges)
        {
            bill._lines.Add(BillLine.From(bill.Id, charge, now));
        }

        return bill;
    }

    /// <summary>
    /// Sends the bill: it becomes money the utility is owed, and Finance posts the receivable.
    /// </summary>
    /// <param name="issuedOn">The day it goes out.</param>
    /// <param name="dueDate">When payment falls due. Must not precede the issue date.</param>
    /// <exception cref="BillingWorkflowException">The bill is not a draft.</exception>
    /// <exception cref="BillingValidationException">The due date precedes the issue date.</exception>
    public void Issue(DateOnly issuedOn, DateOnly dueDate, RegistryActor actor, DateTimeOffset now, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (dueDate < issuedOn)
        {
            throw new BillingValidationException(
                $"Bill {BillNumber} cannot fall due on {dueDate:yyyy-MM-dd}, before it is issued on {issuedOn:yyyy-MM-dd}.");
        }

        Move(BillStatus.Issued, now, reason);

        IssuedOn = issuedOn;
        DueDate = dueDate;
    }

    /// <summary>
    /// Records money received against the bill, moving it to <see cref="BillStatus.PartiallyPaid"/>
    /// or <see cref="BillStatus.Paid"/>.
    /// </summary>
    /// <remarks>
    /// <b>Written now, called later.</b> WP-2.5 owns the payments simulator and the consumer that
    /// reduces a balance when <c>PaymentApproved</c> arrives; what belongs here is what it means for
    /// a bill to be paid, which is this module's business and is unit-tested as such. The same shape
    /// WP-1.3 left <c>Asset.RecordMaintenance</c> in for WP-3.4.
    /// </remarks>
    /// <exception cref="BillingWorkflowException">
    /// The bill is not outstanding, or the payment is more than is owed.
    /// </exception>
    /// <exception cref="BillingValidationException">The amount is not positive, or is finer than a cent.</exception>
    public void RecordPayment(decimal amount, RegistryActor actor, DateTimeOffset now, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (amount <= Money.Zero)
        {
            throw new BillingValidationException($"A payment against bill {BillNumber} must be positive; '{amount}' is not.");
        }

        if (!Money.IsRounded(amount))
        {
            // Refused rather than rounded: this is a figure somebody or some provider stated, not
            // one GridCore computed. The same call WP-1.1 made for a deposit finer than a cent.
            throw new BillingValidationException($"A payment is recorded to the cent; '{amount}' is finer than that.");
        }

        if (!IsOutstanding)
        {
            throw new BillingWorkflowException(
                $"Bill {BillNumber} is {Status} and is not owed, so there is nothing to pay against it.");
        }

        if (amount > Balance)
        {
            // Refused rather than absorbed. An overpayment is a credit on the account, which is
            // Finance's to hold (WP-2.6) — a bill that quietly swallowed it would leave money with
            // no record of where it went.
            throw new BillingWorkflowException(
                $"Bill {BillNumber} has {Balance} outstanding; a payment of {amount} is more than is owed.");
        }

        AmountPaid += amount;

        var settled = Balance is Money.Zero ? BillStatus.Paid : BillStatus.PartiallyPaid;

        if (settled == Status)
        {
            // The second instalment against a bill that is already part paid. The status has not
            // moved, so the machine is not consulted — PartiallyPaid → PartiallyPaid is deliberately
            // absent from it, because a self-transition in a state machine is a way for a bill to
            // "move" to where it already is. The money and the timestamps still move.
            StatusChangedAt = now;
            StatusReason = RegistryText.Clean(reason, ReasonLength);
        }
        else
        {
            Move(settled, now, reason);
        }

        if (Status is BillStatus.Paid)
        {
            PaidAt = now;
        }
    }

    /// <summary>
    /// Marks the bill overdue because it is past its due date and still owed.
    /// </summary>
    /// <param name="asOf">The day to judge against.</param>
    /// <returns>
    /// <see langword="true"/> if the bill moved. <see langword="false"/> — not an exception — when
    /// it is not yet due or is not owed: an overdue review walks every outstanding bill and "this
    /// one is fine" is an ordinary answer, not a failure.
    /// </returns>
    public bool MarkOverdue(DateOnly asOf, RegistryActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!IsPastDue(asOf) || Status is BillStatus.Overdue)
        {
            return false;
        }

        Move(BillStatus.Overdue, now, $"Past due since {DueDate:yyyy-MM-dd}.");

        return true;
    }

    /// <summary>
    /// Withdraws the bill. Terminal — anything still owed afterwards is a new bill, never this one
    /// brought back.
    /// </summary>
    /// <exception cref="BillingWorkflowException">The bill is already settled or already cancelled.</exception>
    /// <exception cref="BillingValidationException">No reason was given.</exception>
    public void Cancel(string reason, RegistryActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // Required, unlike most transitions' reasons. Cancelling a bill removes money the utility
        // was owed, and "why" is the first question asked of it — the same call WP-1.4 made about
        // an unexplained stock adjustment.
        if (RegistryText.Clean(reason, ReasonLength) is null)
        {
            throw new BillingValidationException($"Cancelling bill {BillNumber} needs a reason.");
        }

        Move(BillStatus.Cancelled, now, reason);
    }

    /// <summary>
    /// The one place the status moves, so no path can move it without checking the machine first.
    /// </summary>
    /// <exception cref="BillingWorkflowException">
    /// The move is not one <see cref="BillTransitions"/> allows. A 409 rather than a 400: legality
    /// depends on where the bill is now, which no validator at the edge can see.
    /// </exception>
    private void Move(BillStatus to, DateTimeOffset now, string? reason)
    {
        if (!BillTransitions.IsAllowed(Status, to))
        {
            throw new BillingWorkflowException(
                $"Bill {BillNumber} is {Status} and cannot move to {to}. "
                + $"Allowed from {Status}: {(AllowedTransitions.Count is 0 ? "nothing — it is final" : string.Join(", ", AllowedTransitions))}.");
        }

        Status = to;
        StatusChangedAt = now;
        StatusReason = RegistryText.Clean(reason, ReasonLength);
    }
}
