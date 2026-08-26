using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.Features.Fees;

/// <summary>Where a raised charge stands.</summary>
public enum AccountChargeStatus
{
    /// <summary>
    /// Raised and waiting for a bill. Where every charge starts — a fee is money the customer will
    /// be asked for, and nobody has been asked yet.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// On a bill: the next cycle bill for the account, or a bill of its own raised at the counter.
    /// Terminal — a charge lands once, and correcting a billed one is an adjustment to that bill
    /// (WP-2.4), never a second landing.
    /// </summary>
    Billed = 2,

    /// <summary>
    /// Withdrawn before it reached a bill. Terminal, and the only way a charge leaves without being
    /// billed — the row keeps saying what it said, because a fee raised in error is part of the
    /// record of what the utility did.
    /// </summary>
    Cancelled = 3,
}

/// <summary>The charge state machine, in one place — the shape <c>BillTransitions</c> established.</summary>
public static class AccountChargeTransitions
{
    private static readonly Dictionary<AccountChargeStatus, AccountChargeStatus[]> Allowed = new()
    {
        [AccountChargeStatus.Pending] = [AccountChargeStatus.Billed, AccountChargeStatus.Cancelled],
        [AccountChargeStatus.Billed] = [],
        [AccountChargeStatus.Cancelled] = [],
    };

    /// <summary>The statuses a charge in <paramref name="status"/> may move to.</summary>
    public static IReadOnlyList<AccountChargeStatus> AllowedFrom(AccountChargeStatus status) =>
        Allowed.TryGetValue(status, out var next) ? next : [];

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> is a legal move.</summary>
    public static bool IsAllowed(AccountChargeStatus from, AccountChargeStatus to) => AllowedFrom(from).Contains(to);

    /// <summary>Whether a charge in <paramref name="status"/> is still waiting for a bill.</summary>
    public static bool IsPending(AccountChargeStatus status) => status is AccountChargeStatus.Pending;

    /// <summary>Whether a charge in <paramref name="status"/> can never move again.</summary>
    public static bool IsFinal(AccountChargeStatus status) => AllowedFrom(status).Count is 0;
}

/// <summary>
/// One fee raised against one service account: which published fee, what the schedule said on the
/// day, why, and the bill it eventually landed on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The charge stamps the schedule row that priced it and the amount it was priced at.</b> That is
/// the shape <c>DepositAssessment.RuleId</c> already gives a deposit, and it is what makes a document
/// reprinted after a repricing still show the figure the customer holds a copy of: nothing here is
/// ever re-derived from the catalogue, which will have moved on.
/// </para>
/// <para>
/// <b>A charge is not a bill line, and not a bill adjustment.</b> It exists before any bill carries
/// it — a fee raised at the desk on Tuesday lands on the cycle bill cut on the 28th — and it may
/// never reach one at all. When it does land, the line on the bill is a <see cref="ChargeKind.Fee"/>
/// line with no tier, no units and no rate: a published figure rather than an arithmetic result.
/// An <i>issued</i> bill is corrected by an adjustment (WP-2.4) and never by raising a charge
/// against it, which is why <see cref="AccountChargeStatus.Billed"/> is terminal.
/// </para>
/// <para>
/// <b>It is not a receivable until it is billed.</b> Finance posts on <c>BillIssued</c>, because
/// that is when the customer has been asked for the money — the same line <c>BillTransitions</c>
/// draws between a draft and an issued bill. A pending charge is a fact about what the utility
/// intends to charge, and nothing is owed on it yet.
/// </para>
/// </remarks>
public sealed class AccountCharge
{
    /// <summary>Longest stored form of a status name.</summary>
    public const int EnumNameLength = 32;

    /// <summary>Longest reason recorded against a charge or a transition.</summary>
    public const int ReasonLength = 512;

    private AccountCharge()
    {
        // EF materialisation.
        AccountNumber = string.Empty;
        CustomerName = string.Empty;
        Description = string.Empty;
        Currency = string.Empty;
        Reason = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this charge. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The service account charged, in the Customers schema.</summary>
    public Guid ServiceAccountId { get; private init; }

    /// <summary>Its number, as printed. Stamped, so a charge reads on its own.</summary>
    public string AccountNumber { get; private init; }

    /// <summary>The customer who will owe it.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>Their name at the time it was raised.</summary>
    public string CustomerName { get; private init; }

    /// <summary>Which published fee this is.</summary>
    public FeeCode Code { get; private init; }

    /// <summary>
    /// The schedule row that priced it. No foreign key to <c>fee_schedule</c>, for the reason a bill
    /// holds none to <c>rate_plans</c>: a charge must survive its schedule row being superseded, and
    /// a real key invites a cascade nobody wants near a figure on a document.
    /// </summary>
    public Guid FeeScheduleId { get; private init; }

    /// <summary>The day that schedule version took effect — why this figure and not another.</summary>
    public DateOnly ScheduleEffectiveFrom { get; private init; }

    /// <summary>What the line says when this reaches a bill. The schedule row's name, stamped.</summary>
    public string Description { get; private init; }

    /// <summary>
    /// How the schedule row arrived at its figure: a published amount, or a published rate on a
    /// basis (WP-2.19).
    /// </summary>
    public FeeBasis Basis { get; private init; }

    /// <summary>
    /// The rate the charge was taken at, or <see langword="null"/> where the fee is a flat one.
    /// </summary>
    /// <remarks>
    /// Stamped beside <see cref="FeeScheduleId"/> and for the same reason: the row it came from can
    /// be superseded, and re-reading the catalogue to explain an old charge would explain it with a
    /// figure that was not in force when it was raised.
    /// </remarks>
    public decimal? Rate { get; private init; }

    /// <summary>
    /// What the rate was taken on — the past-due balance, for a late charge. <see langword="null"/>
    /// on a flat fee.
    /// </summary>
    /// <remarks>
    /// <b>The third of the three columns that make a rate charge re-readable.</b> The schedule row
    /// says which rule, the rate says what it was, and this says what it was applied to; together
    /// they reproduce <see cref="Amount"/> exactly, years later, without anybody re-running an
    /// arrears query over a register that has moved on.
    /// </remarks>
    public decimal? BasisAmount { get; private init; }

    /// <summary>What was charged, to the cent. The schedule's figure on <see cref="RaisedOn"/>.</summary>
    public decimal Amount { get; private init; }

    /// <summary>ISO 4217 code the amount is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>
    /// The day the charge was priced against the schedule. Its own field rather than the date part
    /// of <see cref="RaisedAt"/>: a fee raised today for a reconnection performed last week is
    /// priced on last week's schedule, and the two dates are then different facts.
    /// </summary>
    public DateOnly RaisedOn { get; private init; }

    /// <summary>Why. Never optional — this is the sensitive action invariant 5 is about.</summary>
    public string Reason { get; private init; }

    /// <summary>Where the charge stands.</summary>
    public AccountChargeStatus Status { get; private set; }

    /// <summary>The bill it landed on, or <see langword="null"/> while it is pending.</summary>
    public Guid? BillId { get; private set; }

    /// <summary>That bill's number, stamped so the charge reads without a second lookup.</summary>
    public string? BillNumber { get; private set; }

    /// <summary>When it was raised.</summary>
    public DateTimeOffset RaisedAt { get; private init; }

    /// <summary>When the status last moved.</summary>
    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>Why it last moved — the cancellation's reason, where it was cancelled.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>Subject id of whoever raised it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>Whether the charge is still waiting for a bill.</summary>
    public bool IsPending => AccountChargeTransitions.IsPending(Status);

    /// <summary>The statuses it may move to, for rendering transition buttons.</summary>
    public IReadOnlyList<AccountChargeStatus> AllowedTransitions => AccountChargeTransitions.AllowedFrom(Status);

    /// <summary>
    /// Raises <paramref name="assessment"/> against <paramref name="account"/> as a pending charge.
    /// </summary>
    /// <param name="assessment">What the schedule said on the day — already read from the catalogue.</param>
    /// <param name="account">Who is charged, from the Customers module's directory.</param>
    /// <param name="raisedOn">The day priced against.</param>
    /// <param name="reason">Why. Required.</param>
    /// <param name="actor">Who raised it.</param>
    /// <param name="now">The clock, for the row's own identity and timestamp.</param>
    /// <exception cref="BillingValidationException">
    /// No reason was given, nobody is named, or the assessed amount is not a positive whole number
    /// of cents.
    /// </exception>
    public static AccountCharge Raise(
        FeeAssessment assessment,
        ServiceAccountSummary account,
        DateOnly raisedOn,
        string reason,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(actor);

        // Every guard before the first field is set — WP-1.4's ordering rule.
        //
        // A zero fee is refused as firmly as a negative one. The schedule can hold a zero row (a fee
        // the utility has waived), but raising one puts a line reading "0.00" on a customer's bill
        // and a row in a register nobody can reconcile — the argument WP-2.12 already makes for
        // having no deposit movement of zero.
        // AN UNPRICED ASSESSMENT IS REFUSED (WP-2.19). A rate fee comes off the catalogue with no
        // figure at all — it has one only once something has been charged on it — so raising one
        // without calling FeeAssessment.PriceOn is a caller that has not decided what the customer
        // is being charged on. Refused here rather than defaulted to zero, because a zero would be
        // caught by the next guard with a message about the schedule and blame the wrong thing.
        if (assessment.Amount is not { } amount)
        {
            throw new BillingValidationException(
                $"{assessment.Code} is published as a {assessment.Basis} fee and has no figure until it is priced on "
                + "a basis. Charge it through the run that computes one, not from a screen.");
        }

        if (amount <= Money.Zero)
        {
            throw new BillingValidationException(
                $"The schedule prices {assessment.Code} at {amount} on {raisedOn:yyyy-MM-dd}, "
                + "which is not something to charge a customer for.");
        }

        if (!Money.IsRounded(amount))
        {
            throw new BillingValidationException(
                $"The schedule prices {assessment.Code} at {amount}, which is finer than a cent.");
        }

        return new AccountCharge
        {
            Id = Guid.CreateVersion7(now),
            ServiceAccountId = account.Id,
            AccountNumber = account.AccountNumber,
            CustomerId = account.CustomerId,
            CustomerName = RegistryText.Clean(account.CustomerName, Bills.Bill.NameLength) ?? account.AccountNumber,
            Code = assessment.Code,
            FeeScheduleId = assessment.FeeScheduleId,
            ScheduleEffectiveFrom = assessment.EffectiveFrom,
            Description = assessment.Name,
            Basis = assessment.Basis,
            Rate = assessment.Rate,
            BasisAmount = assessment.BasisAmount,
            Amount = amount,
            Currency = assessment.Currency,
            RaisedOn = raisedOn,
            Reason = RegistryText.Clean(reason, ReasonLength)
                ?? throw new BillingValidationException("A charge must say why the customer is being charged."),
            Status = AccountChargeStatus.Pending,
            RaisedAt = now,
            StatusChangedAt = now,
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new BillingValidationException("A charge must name who raised it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
        };
    }

    /// <summary>
    /// Records that the charge landed on a bill.
    /// </summary>
    /// <exception cref="BillingWorkflowException">The charge has already been billed or cancelled.</exception>
    /// <exception cref="BillingValidationException">The bill is not named.</exception>
    public void MarkBilled(Guid billId, string billNumber, DateTimeOffset now)
    {
        if (RegistryText.Clean(billNumber, RegistryNumbers.MaxLength) is not { } number)
        {
            throw new BillingValidationException($"Charge {Description} cannot be billed without a bill number.");
        }

        Move(AccountChargeStatus.Billed, now, reason: null);

        BillId = billId;
        BillNumber = number;
    }

    /// <summary>
    /// Withdraws the charge before it reaches a bill.
    /// </summary>
    /// <exception cref="BillingWorkflowException">It has already been billed, or already cancelled.</exception>
    /// <exception cref="BillingValidationException">No reason was given.</exception>
    public void Cancel(string reason, DateTimeOffset now)
    {
        if (RegistryText.Clean(reason, ReasonLength) is not { } cleaned)
        {
            throw new BillingValidationException($"Withdrawing charge {Description} needs a reason.");
        }

        Move(AccountChargeStatus.Cancelled, now, cleaned);
    }

    /// <summary>
    /// The charge as a bill line: a published figure with no tier, no units and no rate.
    /// </summary>
    /// <remarks>
    /// <b>What distinguishes a fee line from a consumption line, stated once.</b> The three per-unit
    /// fields are null because a fee is not per unit — it is what the schedule said on the day, and
    /// the only arithmetic behind it is the schedule row this charge stamped.
    /// </remarks>
    /// <remarks>
    /// The sequence is left at zero: where a fee sits on a bill is the bill's business, and
    /// <c>Bill.Calculate</c> numbers the fee lines after the tariff's own. A charge that carried its
    /// own position would be one more thing that can disagree with the document it lands on.
    /// </remarks>
    public RateCharge AsBillLine() => new(
        Sequence: 0,
        ChargeKind.Fee,
        Description,
        TierSequence: null,
        Units: null,
        RatePerUnit: null,
        Amount);

    /// <summary>
    /// Moves the charge, refusing an illegal transition.
    /// </summary>
    /// <remarks>
    /// A 409 from the aggregate rather than a 400 from a validator, the call every state machine in
    /// GridCore makes: legality depends on the current state, which a validator holding one request
    /// body cannot see.
    /// </remarks>
    private void Move(AccountChargeStatus to, DateTimeOffset now, string? reason)
    {
        if (!AccountChargeTransitions.IsAllowed(Status, to))
        {
            throw new BillingWorkflowException(
                $"Charge '{Description}' on account {AccountNumber} is {Status} and cannot become {to}. "
                + (Status is AccountChargeStatus.Billed
                    ? "A charge that has reached a bill is corrected by adjusting that bill, not by moving the charge."
                    : "A withdrawn charge stays withdrawn; charging the customer again is a new charge."));
        }

        Status = to;
        StatusChangedAt = now;
        StatusReason = reason;
    }
}
