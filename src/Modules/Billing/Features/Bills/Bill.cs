using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.RatePlans;
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

/// <summary>What a bill was raised for.</summary>
/// <remarks>
/// <para>
/// Stored by name, like every other enum on this table. It is a fact about the document rather than
/// a state it moves through — a bill is one kind or the other from the moment it is calculated —
/// which is why it sits beside <see cref="BillStatus"/> rather than in it.
/// </para>
/// <para>
/// <b>It is what makes the nullable half of this table readable.</b> A charge bill has no meter, no
/// reading and no tariff, so those columns are null on one — and a column that is null for two
/// different reasons ("this bill has no meter" versus "this bill lost its meter") is a column nobody
/// can query. This says which.
/// </para>
/// </remarks>
public enum BillKind
{
    /// <summary>
    /// A period of supply priced against a tariff: a meter was read, and the units between the dials
    /// were charged. Every bill a billing run produces.
    /// </summary>
    Consumption = 1,

    /// <summary>
    /// Fees alone, with no period of supply behind them (WP-2.16) — what the counter raises when a
    /// customer is paying a reconnection fee now rather than waiting for their next bill.
    /// </summary>
    Charge = 2,
}

/// <summary>
/// A bill: one billing period for one service account, priced by one version of one tariff — or,
/// where it carries fees alone, a charge bill raised at the counter with no tariff behind it.
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
/// and does not want them — the opposite of a stock ledger or a decade of readings. Its
/// <see cref="Adjustments"/> are a navigation for the same reasons.
/// </para>
/// <para>
/// <b>What was printed and what is owed are two figures.</b> <see cref="TotalAmount"/> is what the
/// rate engine produced and never moves again; <see cref="AmountDue"/> is that plus every
/// correction since (WP-2.4). Correcting a bill by editing its total would leave the utility unable
/// to reproduce the document the customer is holding.
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
    private readonly List<BillAdjustment> _adjustments = [];

    private Bill()
    {
        // EF materialisation. The tariff and meter fields are absent rather than empty: a charge
        // bill genuinely has none, so they are nullable and are not initialised here.
        BillNumber = string.Empty;
        AccountNumber = string.Empty;
        CustomerName = string.Empty;
        Currency = string.Empty;
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

    /// <summary>What this bill was raised for — a period of supply, or fees alone.</summary>
    public BillKind Kind { get; private init; }

    /// <summary>
    /// The tariff version priced against, or <see langword="null"/> on a charge bill, which prices
    /// nothing against a tariff.
    /// </summary>
    public Guid? RatePlanId { get; private init; }

    /// <summary>Its code, as printed. Absent on a charge bill.</summary>
    public string? RatePlanCode { get; private init; }

    /// <summary>Its name, as printed. Absent on a charge bill.</summary>
    public string? RatePlanName { get; private init; }

    /// <summary>The day that tariff version took effect — why these rates and not others. Absent on a charge bill.</summary>
    public DateOnly? RatePlanEffectiveFrom { get; private init; }

    /// <summary>ISO 4217 code every amount on this bill is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>What the units are measured in, e.g. <c>kWh</c>. Absent on a charge bill, which has no units.</summary>
    public string? UnitOfMeasure { get; private init; }

    /// <summary>First day of the billed period.</summary>
    public DateOnly PeriodStart { get; private init; }

    /// <summary>Last day of it — the day the meter was read.</summary>
    public DateOnly PeriodEnd { get; private init; }

    /// <summary>The reading cycle this bill came from, or <see langword="null"/> for an ad-hoc bill.</summary>
    public string? CycleCode { get; private init; }

    /// <summary>
    /// The reading in Metering's register that closed the period, or <see langword="null"/> on a
    /// charge bill — no meter was read, because no period of supply is being billed.
    /// </summary>
    public Guid? MeterReadingId { get; private init; }

    /// <summary>The meter that produced it. Absent on a charge bill.</summary>
    public Guid? MeterId { get; private init; }

    /// <summary>Its number, as printed. Absent on a charge bill.</summary>
    public string? MeterNumber { get; private init; }

    /// <summary>The dials at the start of the period.</summary>
    public decimal? PreviousReading { get; private init; }

    /// <summary>The dials at the end of it.</summary>
    public decimal? CurrentReading { get; private init; }

    /// <summary>Units billed.</summary>
    public decimal Consumption { get; private init; }

    /// <summary>What the bill comes to — the sum of its lines, as printed.</summary>
    public decimal TotalAmount { get; private init; }

    /// <summary>
    /// How much of <see cref="TotalAmount"/> is fees rather than supply (WP-2.16).
    /// </summary>
    /// <remarks>
    /// <b>Stored rather than summed from the lines, and it is Finance that needs it.</b> A fee earns
    /// fee revenue and consumption earns utility revenue, so <c>BillIssued</c> carries the split and
    /// the posting credits two accounts. A list does not load a bill's lines, and a figure that read
    /// as zero whenever they were absent would post the whole of a counter bill to the wrong account
    /// — silently. It never moves once the bill is calculated, exactly as the printed total does not:
    /// a fee credited afterwards is an adjustment, and adjustments are their own history.
    /// </remarks>
    public decimal FeeAmount { get; private init; }

    /// <summary>How much of it has been paid.</summary>
    public decimal AmountPaid { get; private set; }

    /// <summary>
    /// The signed sum of every correction made to it since — negative where credits outweigh
    /// charges. Held on the bill rather than derived from <see cref="Adjustments"/> so a list that
    /// does not load them still reports what is owed; <see cref="Adjust"/> checks the two agree
    /// before it adds another.
    /// </summary>
    public decimal AdjustmentTotal { get; private set; }

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

    /// <summary>The corrections made to it, in the order they were applied.</summary>
    public IReadOnlyList<BillAdjustment> Adjustments => _adjustments;

    /// <summary>
    /// What the customer owes on this bill today: what was calculated, plus every correction since.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="TotalAmount"/>. The printed total keeps saying what the rate
    /// engine produced and what the customer holds a copy of; a credit changes what is <i>owed</i>
    /// without changing what the document <i>said</i>. Reconciling the two is exactly what the
    /// adjustment history is for.
    /// </remarks>
    public decimal AmountDue => TotalAmount + AdjustmentTotal;

    /// <summary>What is still owed.</summary>
    public decimal Balance => AmountDue - AmountPaid;

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
    /// <param name="fees">
    /// Fees waiting against the account, landing on this bill after the tariff's own lines (WP-2.16).
    /// Each is a published figure the caller has already priced — <see cref="AccountCharge.AsBillLine"/>
    /// is what produces one.
    /// </param>
    /// <exception cref="BillingValidationException">
    /// The number is missing, the period runs backwards, the calculation does not add up to its own
    /// lines, or a fee line is not a positive whole number of cents of the right kind.
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
        string? cycleCode = null,
        IReadOnlyList<RateCharge>? fees = null)
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
        //
        // The tariff's half is checked against the calculation on its own, BEFORE the fees are added
        // in: a rate engine whose lines disagree with its own total is a different fault from a fee
        // that arrived malformed, and adding them together first would let one hide the other.
        var priced = Money.Total(calculation.Charges.Select(charge => charge.Amount));

        if (priced != calculation.Total)
        {
            throw new BillingValidationException(
                $"Bill {billNumber} totals {calculation.Total} but its lines add up to {priced}. "
                + "A bill must equal the sum of what is printed on it.");
        }

        if (!Money.IsRounded(calculation.Total))
        {
            throw new BillingValidationException(
                $"Bill {billNumber} totals {calculation.Total}, which is finer than a cent.");
        }

        var feeLines = RequireFeeLines(billNumber, fees);
        var feeTotal = Money.Total(feeLines.Select(fee => fee.Amount));

        var bill = new Bill
        {
            Id = Guid.CreateVersion7(now),
            BillNumber = billNumber.Trim(),
            ServiceAccountId = account.Id,
            AccountNumber = account.AccountNumber,
            CustomerId = account.CustomerId,
            CustomerName = RegistryText.Clean(account.CustomerName, NameLength) ?? account.AccountNumber,
            ServiceLocationId = account.ServiceLocationId,
            Kind = BillKind.Consumption,
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
            TotalAmount = Money.Total([calculation.Total, feeTotal]),
            FeeAmount = feeTotal,
            AmountPaid = Money.Zero,
            AdjustmentTotal = Money.Zero,
            Status = BillStatus.Draft,
            CreatedAt = now,
            StatusChangedAt = now,
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new BillingValidationException("A bill must name who raised it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
        };

        bill.Print(calculation.Charges, feeLines, now);

        return bill;
    }

    /// <summary>
    /// Raises a bill for fees alone — no meter, no period of supply, no tariff (WP-2.16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the counter raises when the customer is paying now.</b> A reconnection fee taken over
    /// the telephone cannot wait for the next cycle bill, and there is nothing to price it against:
    /// the fees are published figures, already stamped onto the charges being landed.
    /// </para>
    /// <para>
    /// <b>The period is the day it was raised, on both sides.</b> A charge bill covers no span of
    /// supply, and a zero-length period stated honestly beats a made-up month — <c>Finance</c> posts
    /// against these dates and a statement orders by them.
    /// </para>
    /// </remarks>
    /// <param name="billNumber">The number to print on it, already reserved by the caller.</param>
    /// <param name="account">Who is billed, from the Customers module's directory.</param>
    /// <param name="fees">The fee lines. At least one — a bill for nothing is not a bill.</param>
    /// <param name="currency">ISO 4217 code the fees are expressed in.</param>
    /// <param name="raisedOn">The day it is raised, which is its period on both sides.</param>
    /// <param name="actor">Who raised it.</param>
    /// <param name="now">The clock, for the row's own identity and timestamp.</param>
    /// <exception cref="BillingValidationException">
    /// The number or the currency is missing, there are no fees, or a fee line is not a positive
    /// whole number of cents of the right kind.
    /// </exception>
    public static Bill ForCharges(
        string billNumber,
        ServiceAccountSummary account,
        IReadOnlyList<RateCharge> fees,
        string currency,
        DateOnly raisedOn,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(fees);
        ArgumentNullException.ThrowIfNull(actor);

        // Every guard before the first line is built — WP-1.4's ordering rule.
        if (string.IsNullOrWhiteSpace(billNumber))
        {
            throw new BillingValidationException("A bill must be given a number before it can be raised.");
        }

        if (RegistryText.Clean(currency, RatePlan.CurrencyLength) is not { } cleanCurrency)
        {
            throw new BillingValidationException($"Bill {billNumber} must name the currency its fees are in.");
        }

        var feeLines = RequireFeeLines(billNumber, fees);

        if (feeLines.Count is 0)
        {
            throw new BillingValidationException(
                $"Bill {billNumber} carries no charges. A bill raised for nothing is a document nobody can pay.");
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
            Kind = BillKind.Charge,

            // No tariff, no meter and no units: absent rather than blank, so nothing downstream has
            // to know that an empty string means "there was no meter". See BillKind.
            Currency = cleanCurrency,
            PeriodStart = raisedOn,
            PeriodEnd = raisedOn,
            Consumption = 0m,
            TotalAmount = Money.Total(feeLines.Select(fee => fee.Amount)),
            FeeAmount = Money.Total(feeLines.Select(fee => fee.Amount)),
            AmountPaid = Money.Zero,
            AdjustmentTotal = Money.Zero,
            Status = BillStatus.Draft,
            CreatedAt = now,
            StatusChangedAt = now,
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new BillingValidationException("A bill must name who raised it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
        };

        bill.Print([], feeLines, now);

        return bill;
    }

    /// <summary>
    /// Checks the fee lines are what a fee line has to be, and hands back the set to print.
    /// </summary>
    /// <remarks>
    /// <b>A fee carries no tier, no units and no rate — that is what tells it from a consumption
    /// line</b>, and it is checked here rather than trusted because a fee that arrived with units on
    /// it would print as arithmetic the schedule never did. Refused rather than stripped: a caller
    /// that built one wrongly has a defect worth seeing.
    /// </remarks>
    private static IReadOnlyList<RateCharge> RequireFeeLines(string billNumber, IReadOnlyList<RateCharge>? fees)
    {
        if (fees is null or { Count: 0 })
        {
            return [];
        }

        foreach (var fee in fees)
        {
            if (fee.Kind is not ChargeKind.Fee)
            {
                throw new BillingValidationException(
                    $"Bill {billNumber} was handed a {fee.Kind} line among its fees. Only a fee lands on a bill this way; "
                    + "consumption comes from the rate engine.");
            }

            if (fee.TierSequence is not null || fee.Units is not null || fee.RatePerUnit is not null)
            {
                throw new BillingValidationException(
                    $"Fee '{fee.Description}' on bill {billNumber} carries a tier, units or a rate. A fee is a published "
                    + "figure, not a quantity at a price — that is what distinguishes it from a consumption line.");
            }

            if (fee.Amount <= Money.Zero)
            {
                throw new BillingValidationException(
                    $"Fee '{fee.Description}' on bill {billNumber} comes to {fee.Amount}, which is not something to charge for.");
            }

            if (!Money.IsRounded(fee.Amount))
            {
                throw new BillingValidationException(
                    $"Fee '{fee.Description}' on bill {billNumber} comes to {fee.Amount}, which is finer than a cent.");
            }
        }

        return fees;
    }

    /// <summary>
    /// Writes the bill's lines: the tariff's, then the fees, numbered in one series from 1.
    /// </summary>
    /// <remarks>
    /// The fees are re-sequenced rather than trusted to arrive numbered — where a fee sits is the
    /// document's business, and a charge that carried its own position would be one more thing able
    /// to disagree with the bill it landed on.
    /// </remarks>
    private void Print(IReadOnlyList<RateCharge> priced, IReadOnlyList<RateCharge> fees, DateTimeOffset now)
    {
        foreach (var charge in priced)
        {
            _lines.Add(BillLine.From(Id, charge, now));
        }

        foreach (var fee in fees)
        {
            _lines.Add(BillLine.From(Id, fee with { Sequence = _lines.Count + 1 }, now));
        }
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
    /// Corrects what the bill is owed, as an entry appended to its history. The bill itself is not
    /// rewritten and its status does not move — except where the correction settles it in full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sensitive one.</b> Invariant 5: the endpoint is gated on <c>billing.adjust</c> and the
    /// service audits before/after. What lives here is the part that is billing's own business —
    /// what may be corrected, by how much, and what it leaves owing.
    /// </para>
    /// <para>
    /// <b>Only a bill somebody actually owes can be adjusted.</b> A draft is not owed by anybody, so
    /// a wrong one is re-run or thrown away rather than credited; a paid or cancelled bill is
    /// settled, and money moving after that is a refund, which is the Payments module's act and
    /// Finance's entry. That is the same line <see cref="RecordPayment"/> draws.
    /// </para>
    /// <para>
    /// <b>A credit larger than the balance is refused, not absorbed.</b> Crediting more than is owed
    /// leaves money on the account, and a credit balance is Finance's to hold (WP-2.6) — a bill that
    /// quietly swallowed the difference would leave it with no record of where it went. Word for
    /// word the call <see cref="RecordPayment"/> makes about an overpayment.
    /// </para>
    /// </remarks>
    /// <param name="kind">Which way the money moves.</param>
    /// <param name="amount">How much, always positive — the kind carries the direction.</param>
    /// <param name="reason">Why. Required.</param>
    /// <param name="actor">Who is correcting it.</param>
    /// <param name="now">The clock, for the entry's own identity and timestamp.</param>
    /// <returns>The entry that was appended.</returns>
    /// <exception cref="BillingWorkflowException">
    /// The bill is not owed, or the credit is larger than its balance.
    /// </exception>
    /// <exception cref="BillingValidationException">
    /// The amount is not positive or is finer than a cent, no reason was given, the kind is not one
    /// this module knows, or the bill was loaded without its adjustment history.
    /// </exception>
    public BillAdjustment Adjust(
        BillAdjustmentKind kind,
        decimal amount,
        string reason,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // Every guard before the first mutation — WP-1.4's ordering rule, learned there from an
        // unexplained adjustment that moved the shelf and only then refused. The database would roll
        // it back; the aggregate the caller is still holding would not.
        BillAdjustment.RequireAmount(amount, BillNumber);

        var signed = BillAdjustment.Signed(kind, amount);

        if (RegistryText.Clean(reason, ReasonLength) is null)
        {
            throw new BillingValidationException($"Adjusting bill {BillNumber} needs a reason.");
        }

        if (!IsOutstanding)
        {
            throw new BillingWorkflowException(
                $"Bill {BillNumber} is {Status} and is not owed, so there is nothing to adjust. "
                + (Status is BillStatus.Draft
                    ? "A draft is corrected by billing it again, not by crediting it."
                    : "Money moving after a bill is settled is a refund, not an adjustment."));
        }

        if (-signed > Balance)
        {
            throw new BillingWorkflowException(
                $"Bill {BillNumber} has {Balance} outstanding; a credit of {amount} is more than is owed.");
        }

        // THE HISTORY GUARD, and the reason this is not merely a running total. If the bill was
        // loaded without its adjustments, the sum below is short and every figure this method writes
        // afterwards would be wrong — silently, and on a document about money. Refused rather than
        // corrected, for the reason Calculate refuses a total that disagrees with its own lines.
        var applied = Money.Total(_adjustments.Select(adjustment => adjustment.Amount));

        if (applied != AdjustmentTotal)
        {
            throw new BillingValidationException(
                $"Bill {BillNumber} carries adjustments totalling {AdjustmentTotal} but only {applied} of them are loaded. "
                + "A bill is adjusted with its whole history in hand.");
        }

        // Built before anything moves, because building it is the last thing that can fail — it is
        // Record that refuses an actor with no subject id. Its own share of the ordering rule.
        var adjustment = BillAdjustment.Record(
            Id,
            _adjustments.Count + 1,
            kind,
            signed,
            AmountDue + signed,
            reason,
            actor,
            now);

        AdjustmentTotal += signed;
        _adjustments.Add(adjustment);

        // A credit that clears the balance settles the bill. That is not an "adjusted" lifecycle
        // state sneaking in — the bill is genuinely no longer owed, and leaving it Issued would
        // park a zero-balance row on the AR worklist for good. The machine still decides: Issued,
        // PartiallyPaid and Overdue may all reach Paid, and nothing else gets here.
        if (Balance is Money.Zero)
        {
            Move(BillStatus.Paid, now, reason);

            PaidAt = now;
        }

        return adjustment;
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
