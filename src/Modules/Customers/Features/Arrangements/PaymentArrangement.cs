using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>
/// What Customer Service does instead of disconnecting: a promise about receivables that already
/// exist (WP-2.20).
/// </summary>
/// <remarks>
/// <para>
/// <b>It creates no money and never mutates a bill.</b> WORK_PACKAGES.md says so in as many words,
/// and it is the rule the whole feature is shaped around: the customer still owes exactly what the
/// bills say, and this records <i>how and when</i> it will arrive. Nothing here raises a charge,
/// reduces a balance or touches a bill's status — which is also why this register publishes no
/// event. There is nothing downstream for it to be true of.
/// </para>
/// <para>
/// <b>The arrears it is made against is STAMPED, never re-read.</b> The precedent is
/// <see cref="Delinquency.DunningNotice"/>, and the reason is the same: what a customer promised is
/// what they were told they owed on the day they promised it. An arrangement that re-read the
/// register would grow every time a new bill was issued, and a customer keeping every instalment
/// would find their promise had moved.
/// </para>
/// <para>
/// <b>The two ceilings that governed it are stamped too</b>, which is what pays for
/// <see cref="ArrangementLimit"/> not being effective-dated: whether this arrangement needed
/// approval, and against what figures, is readable off the row for ever.
/// </para>
/// <para>
/// <b>Append-only apart from the status and the money that arrives.</b> The schedule is what was
/// promised; a promise that could be re-cut after a missed instalment is not a promise. A broken
/// arrangement is replaced by a fresh row, never resumed — see
/// <see cref="PaymentArrangementTransitions"/>.
/// </para>
/// </remarks>
public sealed class PaymentArrangement
{
    /// <summary>The most instalments GridCore will schedule at all, whatever anybody approves.</summary>
    /// <remarks>
    /// Distinct from <see cref="ArrangementLimit.MaximumInstalments"/>, which is what a rep may
    /// agree <i>alone</i>. This is the outer edge: a debt spread over more than three years is a
    /// write-off that nobody has had to call one, and an approval queue is not the place to discover
    /// that.
    /// </remarks>
    public const int MaximumInstalments = 36;

    /// <summary>Longest stored form of a status or class name.</summary>
    public const int StatusNameLength = 32;

    /// <summary>Longest stored form of an account or arrangement number.</summary>
    public const int NumberLength = RegistryNumbers.MaxLength;

    /// <summary>Longest name stored against a customer.</summary>
    public const int NameLength = 200;

    /// <summary>Longest ISO 4217 code stored.</summary>
    public const int CurrencyLength = 8;

    /// <summary>Longest note recorded against an arrangement.</summary>
    public const int NotesLength = 1024;

    private readonly List<ArrangementInstalment> _instalments = [];

    private PaymentArrangement()
    {
        // EF materialisation.
        ArrangementNumber = string.Empty;
        AccountNumber = string.Empty;
        CustomerName = string.Empty;
        Currency = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this arrangement. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>Its number, as quoted down the telephone, e.g. <c>PA-000001</c>.</summary>
    public string ArrangementNumber { get; private init; }

    /// <summary>The account it is against.</summary>
    public Guid ServiceAccountId { get; private init; }

    /// <summary>Its number, stamped so the arrangement reads on its own.</summary>
    public string AccountNumber { get; private init; }

    /// <summary>The customer who promised.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>Their name at the time.</summary>
    public string CustomerName { get; private init; }

    /// <summary>Their class at the time — the key the limit that governed this was read on.</summary>
    public CustomerClass CustomerClass { get; private init; }

    /// <summary>Where it stands, as recorded. <see cref="StandingOn"/> is what protection reads.</summary>
    public PaymentArrangementStatus Status { get; private set; }

    /// <summary>What was past due when it was made. Stamped, never re-read.</summary>
    public decimal ArrearsBalance { get; private init; }

    /// <summary>ISO 4217 code every figure on it is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>What was taken up front. Zero where nothing was.</summary>
    public decimal DownPayment { get; private init; }

    /// <summary>How many instalments the rest was spread over. Excludes the down payment.</summary>
    public int InstalmentCount { get; private init; }

    /// <summary>Days between instalments.</summary>
    public int IntervalDays { get; private init; }

    /// <summary>The day it was made.</summary>
    public DateOnly ArrangedOn { get; private init; }

    /// <summary>The ceiling on what a rep of this customer's class could arrange alone, that day.</summary>
    public decimal LimitMaximumBalance { get; private init; }

    /// <summary>The instalment ceiling that applied, that day.</summary>
    public int LimitMaximumInstalments { get; private init; }

    /// <summary>Whether it went beyond one of them and so needed approving before it could take effect.</summary>
    public bool RequiresApproval { get; private init; }

    /// <summary>The approval request raised for it, or <see langword="null"/> where it needed none.</summary>
    public Guid? ApprovalRequestId { get; private init; }

    /// <summary>The day it came into force, or <see langword="null"/> while it has not.</summary>
    public DateOnly? ActivatedOn { get; private set; }

    /// <summary>The day it was kept or broken, or <see langword="null"/> while neither.</summary>
    public DateOnly? ClosedOn { get; private set; }

    /// <summary>What the desk wrote beside it.</summary>
    public string? Notes { get; private set; }

    /// <summary>Subject id of whoever made it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it was recorded.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>The schedule, in the order it falls due.</summary>
    public IReadOnlyList<ArrangementInstalment> Instalments => _instalments;

    /// <summary>What the schedule adds up to. Equal to <see cref="ArrearsBalance"/> by construction.</summary>
    public decimal ScheduledAmount => Money.Total(_instalments.Select(instalment => instalment.Amount));

    /// <summary>What has arrived against it.</summary>
    public decimal PaidAmount => Money.Total(_instalments.Select(instalment => instalment.PaidAmount));

    /// <summary>What is still promised.</summary>
    public decimal OutstandingAmount => Money.Total(_instalments.Select(instalment => instalment.Outstanding));

    /// <summary>The next instalment that has not been settled, or <see langword="null"/> where none is left.</summary>
    public ArrangementInstalment? NextInstalment =>
        _instalments.Where(instalment => !instalment.IsSettled).OrderBy(instalment => instalment.Sequence).FirstOrDefault();

    /// <summary>
    /// Where it <i>effectively</i> stands on <paramref name="asOf"/> — what an account's protection
    /// is read from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Computed, so protection is never stale between reviews.</b> A missed instalment stops
    /// protecting the account the moment its due date passes, whether or not the review run has come
    /// round to write the break down. The alternative — a stored status alone — would mean an
    /// account defaulting on a Friday stayed protected from disconnection all weekend because a job
    /// had not run, which is the sort of gap that gets a utility a reputation.
    /// </para>
    /// <para>
    /// <b>It never disagrees with what the run records</b>, because the run writes down exactly what
    /// this answers: <c>PaymentArrangementService.ReviewAsync</c> calls it and persists the result.
    /// A stored terminal status wins outright, which is what makes "broken cannot be resumed" hold
    /// even after a late payment settles the instalment that broke it.
    /// </para>
    /// </remarks>
    public PaymentArrangementStatus StandingOn(DateOnly asOf)
    {
        if (Status is not PaymentArrangementStatus.Active)
        {
            return Status;
        }

        // Settled first, and deliberately: an arrangement whose every instalment has arrived has
        // been kept, even if one of them arrived a day late and no review ran in between. The
        // utility got its money on a promise the customer honoured, and calling that broken would be
        // a book-keeping opinion rather than a fact about the account.
        if (_instalments.All(instalment => instalment.IsSettled))
        {
            return PaymentArrangementStatus.Kept;
        }

        return _instalments.Any(instalment => instalment.IsMissedBy(asOf))
            ? PaymentArrangementStatus.Broken
            : PaymentArrangementStatus.Active;
    }

    /// <summary>
    /// Whether it stops the supply being cut off on <paramref name="asOf"/>.
    /// </summary>
    /// <remarks>
    /// <b>Only <see cref="PaymentArrangementStatus.Active"/> protects.</b> A proposal is not a
    /// promise — the customer has not agreed and, above the rep's limit, nobody has approved it — and
    /// a broken one is the very case disconnection exists for. This one method is the whole of what
    /// the rest of GridCore learns about arrangements.
    /// </remarks>
    public bool SuppressesDisconnectionOn(DateOnly asOf) => StandingOn(asOf) is PaymentArrangementStatus.Active;

    /// <summary>
    /// Proposes an arrangement over <paramref name="schedule"/>.
    /// </summary>
    /// <param name="arrangementNumber">Its number, from the registry generator.</param>
    /// <param name="account">The account it is against.</param>
    /// <param name="customer">The customer promising.</param>
    /// <param name="arrearsBalance">What was past due when it was made.</param>
    /// <param name="currency">ISO 4217 code every figure is in.</param>
    /// <param name="downPayment">What is taken up front.</param>
    /// <param name="instalmentCount">How many instalments the rest is spread over.</param>
    /// <param name="intervalDays">Days between them.</param>
    /// <param name="arrangedOn">The day it is made.</param>
    /// <param name="limit">The published ceilings that govern it.</param>
    /// <param name="approvalRequestId">The approval raised for it, where it needed one.</param>
    /// <param name="schedule">The dated lines, from <see cref="ArrangementSchedule.Build"/>.</param>
    /// <param name="notes">What the desk wrote beside it.</param>
    /// <param name="actor">Who made it.</param>
    /// <param name="now">The clock, for the row's identity and timestamp.</param>
    /// <exception cref="RegistryValidationException">A required value is missing, or the schedule does not add up to the balance.</exception>
    public static PaymentArrangement Propose(
        string arrangementNumber,
        ServiceAccounts.ServiceAccount account,
        Customer customer,
        decimal arrearsBalance,
        string currency,
        decimal downPayment,
        int instalmentCount,
        int intervalDays,
        DateOnly arrangedOn,
        ArrangementLimit limit,
        Guid? approvalRequestId,
        IReadOnlyList<ScheduledInstalment> schedule,
        string? notes,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(actor);

        // Every guard before the first field is set — WP-1.4's ordering rule.
        if (schedule.Count is 0)
        {
            throw new RegistryValidationException("An arrangement is a schedule, and an empty one promises nothing.");
        }

        // THE INVARIANT THE WHOLE FEATURE RESTS ON, asserted in code rather than trusted from the
        // builder — the same call the double-entry rule makes about debits and credits. A schedule
        // that did not add up to the balance would have a customer keeping every instalment and
        // still owing money, or paying off a debt that was never that large.
        var scheduled = Money.Total(schedule.Select(line => line.Amount));

        if (scheduled != arrearsBalance)
        {
            throw new RegistryValidationException(
                $"The schedule adds up to {scheduled:0.00} against an arranged balance of {arrearsBalance:0.00}. "
                + "An arrangement's instalments sum to exactly what was promised.");
        }

        var requiresApproval = limit.RequiresApproval(arrearsBalance, instalmentCount);

        if (requiresApproval && approvalRequestId is null)
        {
            throw new RegistryValidationException(
                $"{arrearsBalance:0.00} over {instalmentCount} instalments is beyond the published limit of "
                + $"{limit.MaximumBalance:0.00} over {limit.MaximumInstalments}, so it cannot be recorded without an "
                + "approval request to decide it.");
        }

        var arrangement = new PaymentArrangement
        {
            Id = Guid.CreateVersion7(now),
            ArrangementNumber = RegistryText.Clean(arrangementNumber, NumberLength)
                ?? throw new RegistryValidationException("An arrangement is quoted by number, so it must have one."),
            ServiceAccountId = account.Id,
            AccountNumber = RegistryText.Clean(account.AccountNumber, NumberLength)
                ?? throw new RegistryValidationException("An arrangement names the account it is against."),
            CustomerId = customer.Id,
            CustomerName = RegistryText.Clean(customer.Name, NameLength)
                ?? throw new RegistryValidationException("An arrangement names the customer who promised."),
            CustomerClass = customer.Class,
            Status = PaymentArrangementStatus.Proposed,
            ArrearsBalance = arrearsBalance,
            Currency = RegistryText.Clean(currency, CurrencyLength)
                ?? throw new RegistryValidationException("An arrangement names the currency its figures are in."),
            DownPayment = downPayment,
            InstalmentCount = instalmentCount,
            IntervalDays = intervalDays,
            ArrangedOn = arrangedOn,

            // Copied off the limit rather than looked up later, so re-cutting a rep's authority
            // cannot rewrite whether an arrangement already made needed approving.
            LimitMaximumBalance = limit.MaximumBalance,
            LimitMaximumInstalments = limit.MaximumInstalments,
            RequiresApproval = requiresApproval,
            ApprovalRequestId = approvalRequestId,
            Notes = RegistryText.Clean(notes, NotesLength),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new RegistryValidationException("An arrangement must name who made it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };

        foreach (var line in schedule)
        {
            arrangement._instalments.Add(ArrangementInstalment.From(arrangement.Id, line, now));
        }

        return arrangement;
    }

    /// <summary>Brings it into force.</summary>
    /// <exception cref="RegistryWorkflowException">It is not a proposal any more.</exception>
    public void Activate(DateOnly on)
    {
        RequireTransition(PaymentArrangementStatus.Active);

        Status = PaymentArrangementStatus.Active;
        ActivatedOn = on;
    }

    /// <summary>Records that it was kept — every instalment arrived.</summary>
    /// <exception cref="RegistryWorkflowException">It is not in force, or something is still owed.</exception>
    public void Keep(DateOnly on)
    {
        RequireTransition(PaymentArrangementStatus.Kept);

        if (OutstandingAmount > Money.Zero)
        {
            throw new RegistryWorkflowException(
                $"{ArrangementNumber} still has {OutstandingAmount:0.00} outstanding, so it has not been kept.");
        }

        Status = PaymentArrangementStatus.Kept;
        ClosedOn = on;
    }

    /// <summary>Records that it was broken — an instalment passed its due date unpaid.</summary>
    /// <exception cref="RegistryWorkflowException">It is not in force.</exception>
    public void Break(DateOnly on)
    {
        RequireTransition(PaymentArrangementStatus.Broken);

        Status = PaymentArrangementStatus.Broken;
        ClosedOn = on;
    }

    /// <summary>
    /// Puts <paramref name="amount"/> against the schedule, earliest unpaid instalment first, and
    /// answers what each line took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Earliest first, and cascading.</b> WORK_PACKAGES.md asks that "a payment applies to the
    /// earliest unpaid instalment"; a payment larger than that instalment carries on down the
    /// schedule rather than sitting as a credit on a line that is already settled, because a
    /// customer who pays two months at once has paid two months.
    /// </para>
    /// <para>
    /// <b>It does not decide whether the arrangement was kept.</b> That is a status change with an
    /// audit entry behind it, and it belongs to the caller that owns the unit of work — see
    /// <c>PaymentArrangementService</c>.
    /// </para>
    /// </remarks>
    /// <param name="amount">What arrived.</param>
    /// <param name="now">When it arrived.</param>
    /// <returns>The instalments that took something, and how much each took.</returns>
    internal IReadOnlyList<(ArrangementInstalment Instalment, decimal Applied)> Apply(decimal amount, DateTimeOffset now)
    {
        var applied = new List<(ArrangementInstalment, decimal)>();
        var remaining = amount;

        foreach (var instalment in _instalments.OrderBy(instalment => instalment.Sequence))
        {
            if (remaining <= Money.Zero)
            {
                break;
            }

            var before = instalment.PaidAmount;
            remaining = instalment.Settle(remaining, now);

            if (instalment.PaidAmount > before)
            {
                applied.Add((instalment, instalment.PaidAmount - before));
            }
        }

        return applied;
    }

    /// <exception cref="RegistryWorkflowException">The move is not one the state machine allows.</exception>
    private void RequireTransition(PaymentArrangementStatus to)
    {
        if (PaymentArrangementTransitions.IsAllowed(Status, to))
        {
            return;
        }

        throw new RegistryWorkflowException(
            PaymentArrangementTransitions.IsTerminal(Status)
                ? $"{ArrangementNumber} is {Status} and cannot be moved to {to}. A settled arrangement is replaced by a "
                  + "fresh one, never resumed."
                : $"{ArrangementNumber} is {Status} and cannot be moved to {to}.");
    }
}
