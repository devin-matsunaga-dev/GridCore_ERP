using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Contracts.Providers;
using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Delinquency;
using GridCore.Platform.Messaging;
using GridCore.Platform.Notifications;
using GridCore.Platform.Security;

namespace GridCore.Modules.Customers.UnitTests.Infrastructure;

/// <summary>
/// Captures what the approval queue told anybody instead of logging it (WP-2.20).
/// </summary>
/// <remarks>
/// The queue is the real <c>ApprovalService</c> in this host, and it sends a notification on every
/// decision. Nothing in Customers reads one, so this exists to keep the composition honest rather
/// than to be asserted on — the module's own facts are in the audit trail.
/// </remarks>
public sealed class RecordingNotificationSender : INotificationSender
{
    /// <summary>Everything sent, in order.</summary>
    public List<Notification> Sent { get; } = [];

    /// <inheritdoc />
    public Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        Sent.Add(notification);

        return Task.CompletedTask;
    }
}

/// <summary>A clock the test moves by hand, so nothing waits on wall time.</summary>
public sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => _now;

    private DateTimeOffset _now = now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// A caller with an explicit identity, so tests never build a token.
/// </summary>
/// <remarks>
/// Holds every permission unless a test names the ones it holds. That default is what the registry
/// tests were written against — they are about the registry, not about authorization — while an
/// intake test that has to prove a deposit is refused hands over a narrowed set instead.
/// </remarks>
public sealed class FakeCurrentUser(string userId, string? userName = null, IReadOnlySet<string>? permissions = null) : ICurrentUser
{
    /// <inheritdoc />
    public string UserId { get; } = userId;

    /// <inheritdoc />
    public string? UserName { get; } = userName ?? userId;

    /// <inheritdoc />
    public bool HasPermission(string permission) => permissions?.Contains(permission) ?? true;

    /// <summary>A caller holding exactly <paramref name="permissions"/> and nothing else.</summary>
    public static FakeCurrentUser Holding(params string[] permissions) =>
        new("auth0|cs-agent", "Ana Cruz", permissions.ToHashSet(StringComparer.Ordinal));
}

/// <summary>
/// Captures what was published instead of writing it to the outbox. The real publisher and the
/// outbox are the platform's, tested in the gate tier; what a registry test needs to know is that
/// its write published the right fact, inside the transaction.
/// </summary>
public sealed class RecordingEventPublisher : IEventPublisher
{
    /// <summary>Everything published, in order.</summary>
    public List<IIntegrationEvent> Published { get; } = [];

    /// <inheritdoc />
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        Published.Add(@event);

        return Task.CompletedTask;
    }

    /// <summary>The single event of this type that was published, failing the test if there is not exactly one.</summary>
    public TEvent Single<TEvent>()
        where TEvent : class, IIntegrationEvent =>
        Assert.Single(Published.OfType<TEvent>());
}

/// <summary>
/// The meter register as Customers sees it, standing in for Metering's implementation.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier's answer to the WP-2.9 seam: <see cref="IMeterDirectory"/> lives in
/// <c>Contracts</c> and Metering registers the real one, so a Customers test cannot resolve it and
/// must not try — a <c>metering</c> schema is exactly what this module may never know about. This is
/// the shape <c>PaymentsTestHost</c> gives <c>IBillDirectory</c>, and it is why searching by meter
/// number is unit-tested in milliseconds with no meter tables present.
/// </para>
/// <para>
/// It matches the way the real directory matches — case-insensitive, exact for
/// <see cref="FindByNumberAsync"/> and containment for <see cref="SearchByNumberAsync"/> — because a
/// double that matched differently would let the search service's two-stage logic pass here and fail
/// against Postgres. <c>MeterDirectoryTests</c> in the Metering fast tier pins the real one to the
/// same rules.
/// </para>
/// </remarks>
public sealed class FakeMeterDirectory : IMeterDirectory
{
    /// <summary>The register this double answers from. A test adds what it needs.</summary>
    public List<MeterSummary> Meters { get; } = [];

    /// <summary>How many times a caller asked for an exact number — how a test proves the probe ran.</summary>
    public int ExactLookups { get; private set; }

    /// <summary>How many times a caller fell back to scanning for a partial number.</summary>
    public int PartialLookups { get; private set; }

    /// <summary>Adds a meter fitted at <paramref name="serviceLocationId"/>.</summary>
    public MeterSummary Fitted(string meterNumber, Guid serviceLocationId)
    {
        var meter = new MeterSummary(
            Guid.CreateVersion7(),
            meterNumber,
            $"SN-{meterNumber}",
            "SinglePhase",
            "Installed",
            serviceLocationId,
            IsFitted: true);

        Meters.Add(meter);

        return meter;
    }

    /// <summary>Adds a meter sitting in the store, on nobody's wall.</summary>
    public MeterSummary InStock(string meterNumber)
    {
        var meter = new MeterSummary(
            Guid.CreateVersion7(),
            meterNumber,
            $"SN-{meterNumber}",
            "SinglePhase",
            "InStore",
            ServiceLocationId: null,
            IsFitted: false);

        Meters.Add(meter);

        return meter;
    }

    /// <inheritdoc />
    public Task<MeterSummary?> FindByNumberAsync(string meterNumber, CancellationToken cancellationToken = default)
    {
        ExactLookups++;

        return Task.FromResult(Meters.FirstOrDefault(meter =>
            string.Equals(meter.MeterNumber, meterNumber, StringComparison.OrdinalIgnoreCase)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MeterSummary>> SearchByNumberAsync(
        string term,
        int limit,
        CancellationToken cancellationToken = default)
    {
        PartialLookups++;

        IReadOnlyList<MeterSummary> found = Meters
            .Where(meter => meter.MeterNumber.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(meter => meter.MeterNumber, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        return Task.FromResult(found);
    }
}

/// <summary>
/// The Billing module's register, as Customers is allowed to see it — a dictionary rather than a
/// database.
/// </summary>
/// <remarks>
/// <para>
/// The sixth cross-module read seam this module consumes a double for, and the second it consumes
/// at all (<see cref="FakeMeterDirectory"/> was the first, for WP-2.9's search). WP-2.12's deposit
/// lifecycle asks one question of Billing before any of a deposit is put against a bill: <i>how
/// much is actually owed on it</i>. A Customers test may not resolve the real
/// <see cref="IBillDirectory"/> — a <c>billing</c> schema is exactly what this module must never
/// know about — so a test supplies a bill in one line and no Postgres container is needed.
/// </para>
/// <para>
/// Shaped exactly like <c>FakeBillDirectory</c> in the Payments fast tier, deliberately: the two
/// modules ask the same seam the same question, and a double that answered differently in one of
/// them would let a rule pass here and fail against the real directory.
/// </para>
/// </remarks>
public sealed class FakeBillDirectory : IBillDirectory
{
    private readonly Dictionary<Guid, BillSummary> _bills = [];
    private readonly Dictionary<Guid, List<BillActivity>> _history = [];
    private readonly List<BillSummary> _outstanding = [];
    private int _ordinal;

    /// <summary>Every bill the ledger asked about, so a test can assert it went through the seam.</summary>
    public List<Guid> Lookups { get; } = [];

    /// <summary>How many bills the last history call was asked for — how a truncation test proves the cap reached the seam.</summary>
    public int LastHistoryLimit { get; private set; }

    /// <summary>Adds a bill and hands it back.</summary>
    /// <param name="customerId">Who owes it.</param>
    /// <param name="serviceAccountId">The account it is billed to.</param>
    /// <param name="amountDue">What is owed on it before anything has been paid.</param>
    /// <param name="amountPaid">How much has already been paid against it.</param>
    /// <param name="status">Its lifecycle status, by name.</param>
    /// <param name="currency">What its amounts are expressed in.</param>
    public BillSummary Add(
        Guid customerId,
        Guid? serviceAccountId = null,
        decimal amountDue = 120.00m,
        decimal amountPaid = 0m,
        string status = "Issued",
        string currency = "USD")
    {
        var id = Guid.CreateVersion7();
        _ordinal++;

        var bill = new BillSummary(
            id,
            $"BIL-{_ordinal:000000}",
            serviceAccountId ?? Guid.CreateVersion7(),
            $"A-{_ordinal:000000}",
            customerId,
            $"Customer {_ordinal}",
            currency,

            // The printed total and what is owed deliberately differ, as WP-2.4 made them: the
            // adjustment is what an application checked against the total rather than the balance
            // would get wrong.
            TotalAmount: amountDue + 10m,
            AmountDue: amountDue,
            AmountPaid: amountPaid,
            Balance: amountDue - amountPaid,
            status,
            IsOutstanding: status is "Issued" or "PartiallyPaid" or "Overdue",
            DueDate: new DateOnly(2026, 9, 30));

        _bills[id] = bill;

        return bill;
    }

    /// <inheritdoc />
    public Task<BillSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Lookups.Add(id);

        return Task.FromResult(_bills.GetValueOrDefault(id));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, BillSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        Lookups.AddRange(ids);

        IReadOnlyDictionary<Guid, BillSummary> found = ids
            .Distinct()
            .Select(_bills.GetValueOrDefault)
            .OfType<BillSummary>()
            .ToDictionary(bill => bill.Id);

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BillSummary>> OutstandingForAccountAsync(
        Guid serviceAccountId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BillSummary> found = _bills.Values
            .Where(bill => bill.ServiceAccountId == serviceAccountId && bill.IsOutstanding)
            .Take(limit)
            .ToList();

        return Task.FromResult(found);
    }

    /// <summary>
    /// Adds a bill that was <b>issued</b> on <paramref name="issuedOn"/> — what WP-2.14's statement
    /// reads, as opposed to the summary the deposit ledger asks for.
    /// </summary>
    /// <remarks>
    /// Kept in a second collection rather than derived from the first, because the two answer
    /// different questions: <see cref="Add"/> supplies a bill with a balance to be checked against,
    /// and this supplies a bill with a date, a printed total and a correction history. A test that
    /// needs both adds both — which is honest, since in the real system they are one row read two
    /// ways and a test asserting they agree is asserting something about the fake.
    /// </remarks>
    /// <param name="customerId">Whose bill.</param>
    /// <param name="issuedOn">The day it went out.</param>
    /// <param name="totalAmount">What it printed.</param>
    /// <param name="amountPaid">How much has been paid against it since.</param>
    /// <param name="status">Where it stands, by name.</param>
    /// <param name="currency">What its amounts are expressed in.</param>
    /// <param name="serviceAccountId">The account billed.</param>
    public BillActivity Issued(
        Guid customerId,
        DateOnly issuedOn,
        decimal totalAmount = 120.00m,
        decimal amountPaid = 0m,
        string status = "Issued",
        string currency = "USD",
        Guid? serviceAccountId = null)
    {
        _ordinal++;

        var activity = new BillActivity(
            Guid.CreateVersion7(),
            $"BIL-{_ordinal:000000}",
            serviceAccountId ?? Guid.CreateVersion7(),
            $"A-{_ordinal:000000}",
            currency,
            issuedOn,
            issuedOn.AddDays(21),
            issuedOn.AddDays(-30),
            issuedOn.AddDays(-1),
            totalAmount,
            AdjustmentTotal: 0m,
            amountPaid,
            status,
            WithdrawnAt: null,
            Corrections: []);

        History(customerId).Add(activity);

        return activity;
    }

    /// <summary>Appends a correction to a bill this double already holds, and hands back the new state.</summary>
    /// <param name="customerId">Whose bill.</param>
    /// <param name="bill">The bill to correct.</param>
    /// <param name="amount">The signed change to what is owed — negative on a credit.</param>
    /// <param name="recordedAt">When it was made.</param>
    /// <param name="reason">Why.</param>
    public BillActivity Correct(Guid customerId, BillActivity bill, decimal amount, DateTimeOffset recordedAt, string reason = "Meter misread")
    {
        ArgumentNullException.ThrowIfNull(bill);

        var correction = new BillCorrection(
            Guid.CreateVersion7(),
            bill.Corrections.Count + 1,
            amount < 0m ? "Credit" : "Charge",
            amount,
            bill.TotalAmount + bill.AdjustmentTotal + amount,
            reason,
            recordedAt);

        return Replace(
            customerId,
            bill,
            bill with
            {
                AdjustmentTotal = bill.AdjustmentTotal + amount,
                Corrections = [.. bill.Corrections, correction],
            });
    }

    /// <summary>Withdraws a bill this double already holds, and hands back the new state.</summary>
    public BillActivity Withdraw(Guid customerId, BillActivity bill, DateTimeOffset withdrawnAt)
    {
        ArgumentNullException.ThrowIfNull(bill);

        return Replace(customerId, bill, bill with { Status = "Cancelled", WithdrawnAt = withdrawnAt });
    }

    /// <inheritdoc />
    /// <remarks>
    /// Filters and caps exactly as <c>BillDirectory</c> does — oldest first, nothing issued after the
    /// day asked for, no more rows than the limit. The cap is the part that matters: a statement
    /// reports itself truncated when a register answers with as many rows as it was asked for, and a
    /// double that ignored the limit could never produce that.
    /// </remarks>
    public Task<IReadOnlyList<BillActivity>> ActivityForCustomerAsync(
        Guid customerId,
        DateOnly issuedOnOrBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        LastHistoryLimit = limit;

        IReadOnlyList<BillActivity> found = History(customerId)
            .Where(bill => bill.IssuedOn <= issuedOnOrBefore)
            .OrderBy(bill => bill.IssuedOn)
            .ThenBy(bill => bill.BillNumber, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Read off the same history the statement reads, so a test that seeds an issued bill for the
    /// statement has also seeded the date a class change is measured against — one fact, one place.
    /// Answers <see langword="null"/> for a customer with no issued bill, which is what lets the
    /// effective-date guard say "there is nothing behind this date" rather than assuming a floor.
    /// </remarks>
    public Task<DateOnly?> LastIssuedOnForCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var history = History(customerId);

        return Task.FromResult(history.Count == 0 ? null : (DateOnly?)history.Max(bill => bill.IssuedOn));
    }

    /// <summary>
    /// Adds an outstanding bill that is part of one account's arrears picture, and hands it back.
    /// </summary>
    /// <remarks>
    /// <b>It seeds both halves.</b> A past-due bill is a <see cref="BillSummary"/> — because the
    /// deposit offset applies to it through <c>ApplyAsync</c>, which asks this seam what is
    /// outstanding — and an <see cref="ArrearsBill"/>, because the delinquency picture reads the
    /// ageing. In the real system those are one row read two ways; here they are seeded together so
    /// a test cannot state a debt that only half of the module can see.
    /// </remarks>
    /// <param name="customerId">Who owes it.</param>
    /// <param name="serviceAccountId">The account it is billed to.</param>
    /// <param name="dueDate">The day it fell due.</param>
    /// <param name="balance">What is still owed on it.</param>
    /// <param name="currency">What its amounts are expressed in.</param>
    public BillSummary Outstanding(
        Guid customerId,
        Guid serviceAccountId,
        DateOnly dueDate,
        decimal balance = 120.00m,
        string currency = "USD")
    {
        var id = Guid.CreateVersion7();
        _ordinal++;

        var bill = new BillSummary(
            id,
            $"BIL-{_ordinal:000000}",
            serviceAccountId,
            $"A-{_ordinal:000000}",
            customerId,
            $"Customer {_ordinal}",
            currency,
            TotalAmount: balance,
            AmountDue: balance,
            AmountPaid: 0m,
            Balance: balance,
            "Issued",
            IsOutstanding: true,
            dueDate);

        _bills[id] = bill;
        _outstanding.Add(bill);

        return bill;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>The ageing bands are deliberately empty.</b> Which bands exist and where their boundaries
    /// fall is Billing's policy, tested against the real <c>ArrearsAgeing</c> in that module's own
    /// suite; a double that invented its own would have Customers' tests asserting something about
    /// the fake. What this answers faithfully is everything the delinquency register actually reads:
    /// what is past due, how late the oldest of it is, and which bills a deposit may be set against.
    /// </remarks>
    public Task<AccountArrears> ArrearsForAccountAsync(
        Guid serviceAccountId,
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        var lines = _outstanding
            .Where(bill => bill.ServiceAccountId == serviceAccountId && bill.Balance > 0m)
            .Select(bill => new ArrearsBill(
                bill.Id,
                bill.BillNumber,
                bill.DueDate,

                // Read back off the dictionary rather than off the captured row, so a bill this
                // double has since had a deposit applied to reports what it now owes.
                _bills[bill.Id].Balance,
                bill.DueDate is { } due ? Math.Max(0, asOf.DayNumber - due.DayNumber) : 0,
                bill.DueDate is { } dueDate && dueDate < asOf))
            .Where(line => line.Balance > 0m)
            .OrderBy(line => line.DueDate ?? DateOnly.MaxValue)
            .ThenBy(line => line.BillNumber, StringComparer.Ordinal)
            .ToList();

        var pastDue = lines.Where(line => line.IsPastDue).ToList();

        return Task.FromResult(new AccountArrears(
            serviceAccountId,
            lines.Count is 0 ? "USD" : _bills[lines[0].Id].Currency,
            asOf,
            lines.Sum(line => line.Balance),
            pastDue.Sum(line => line.Balance),
            lines.Where(line => !line.IsPastDue).Sum(line => line.Balance),
            pastDue.Count is 0 ? null : pastDue[0].DueDate,
            pastDue.Count is 0 ? 0 : pastDue[0].DaysPastDue,
            Buckets: [],
            lines));
    }

    /// <summary>
    /// Records that <paramref name="amount"/> was settled against a bill this double holds — what
    /// Billing does when it consumes <c>CustomerDepositApplied</c>.
    /// </summary>
    /// <remarks>
    /// The double does it synchronously, which the real system does not: there the reduction happens
    /// in a consumer, after the transaction that wrote the deposit entry has committed. That is why
    /// <c>DelinquencyService</c> computes its post-offset arrears rather than re-reading it, and why
    /// this exists — a test that wants to see the arrears fall has to say when the other module
    /// caught up.
    /// </remarks>
    public void Settle(Guid billId, decimal amount)
    {
        var bill = _bills[billId];
        var paid = bill.AmountPaid + amount;
        var balance = bill.AmountDue - paid;

        _bills[billId] = bill with
        {
            AmountPaid = paid,
            Balance = balance,
            Status = balance <= 0m ? "Paid" : "PartiallyPaid",
            IsOutstanding = balance > 0m,
        };
    }

    private List<BillActivity> History(Guid customerId)
    {
        if (!_history.TryGetValue(customerId, out var bills))
        {
            bills = [];
            _history[customerId] = bills;
        }

        return bills;
    }

    private BillActivity Replace(Guid customerId, BillActivity bill, BillActivity replacement)
    {
        var bills = History(customerId);
        var index = bills.FindIndex(candidate => candidate.Id == bill.Id);

        Assert.True(index >= 0, "The bill being corrected is not one this double holds.");

        bills[index] = replacement;

        return replacement;
    }
}

/// <summary>
/// The Payments module's register, as Customers is allowed to see it — a dictionary rather than a
/// database.
/// </summary>
/// <remarks>
/// <para>
/// The seam WP-2.13 added to <c>Contracts</c>, and the third this module consumes a double for
/// (<see cref="FakeMeterDirectory"/> was the first, <see cref="FakeBillDirectory"/> the second). A
/// note filed against a payment has to name a real payment of that customer's, and a Customers test
/// may not resolve the real <see cref="IPaymentDirectory"/> — a <c>payments</c> schema is exactly
/// what this module must never know about — so a test supplies one in a line.
/// </para>
/// <para>
/// Shaped like <see cref="FakeBillDirectory"/> on purpose, down to the ordinal-numbered references:
/// the two seams answer the same shape of question, and a double that behaved differently in one of
/// them would let a rule pass here and fail against the real directory. <c>PaymentDirectoryTests</c>
/// in the Payments fast tier pins the real one to the same answers.
/// </para>
/// </remarks>
public sealed class FakePaymentDirectory : IPaymentDirectory
{
    private readonly Dictionary<Guid, PaymentSummary> _payments = [];
    private int _ordinal;

    /// <summary>Every payment a note asked about, so a test can assert it went through the seam.</summary>
    public List<Guid> Lookups { get; } = [];

    /// <summary>How many payments the last history call was asked for.</summary>
    public int LastHistoryLimit { get; private set; }

    /// <summary>Adds a payment and hands it back.</summary>
    /// <param name="customerId">Who paid.</param>
    /// <param name="serviceAccountId">The account credited.</param>
    /// <param name="billId">The bill it was taken against.</param>
    /// <param name="amount">How much was asked for.</param>
    /// <param name="status">Where the attempt stands, by name.</param>
    /// <param name="currency">What the amount is expressed in.</param>
    /// <param name="method">How it was tendered.</param>
    /// <param name="requestedAt">When it was taken. A statement dates a settled payment by it.</param>
    public PaymentSummary Add(
        Guid customerId,
        Guid? serviceAccountId = null,
        Guid? billId = null,
        decimal amount = 120.00m,
        string status = "Approved",
        string currency = "USD",
        string method = "card",
        DateTimeOffset? requestedAt = null)
    {
        var id = Guid.CreateVersion7();
        _ordinal++;

        var payment = new PaymentSummary(
            id,
            $"PAY-{_ordinal:000000}",
            customerId,
            serviceAccountId ?? Guid.CreateVersion7(),
            billId ?? Guid.CreateVersion7(),
            amount,
            currency,
            status,

            // The rule Payments owns, mirrored: approved is the only status that is money the utility
            // holds. A declined attempt is still a payment a note can be filed against.
            IsSettled: status is "Approved",
            method,
            RequestedAt: requestedAt ?? new DateTimeOffset(2026, 8, 20, 9, 30, 0, TimeSpan.Zero),

            // Answered when the provider answered, whatever it answered — as the real register has
            // it. A refusal is an answer, which is what lets an export date every row while a
            // statement, reading IsSettled beside it, credits none of them.
            AnsweredAt: status is "Pending" ? null : requestedAt ?? new DateTimeOffset(2026, 8, 20, 9, 30, 0, TimeSpan.Zero));

        _payments[id] = payment;

        return payment;
    }

    /// <inheritdoc />
    public Task<PaymentSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Lookups.Add(id);

        return Task.FromResult(_payments.GetValueOrDefault(id));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, PaymentSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        Lookups.AddRange(ids);

        IReadOnlyDictionary<Guid, PaymentSummary> found = ids
            .Distinct()
            .Select(_payments.GetValueOrDefault)
            .OfType<PaymentSummary>()
            .ToDictionary(payment => payment.Id);

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Oldest first and capped at the limit, exactly as <c>PaymentDirectory</c> answers — for the
    /// reason <see cref="FakeBillDirectory.ActivityForCustomerAsync"/> gives about its own cap.
    /// </remarks>
    public Task<IReadOnlyList<PaymentSummary>> ForCustomerAsync(
        Guid customerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        LastHistoryLimit = limit;

        IReadOnlyList<PaymentSummary> found = _payments.Values
            .Where(payment => payment.CustomerId == customerId)
            .OrderBy(payment => payment.Id)
            .Take(limit)
            .ToList();

        return Task.FromResult(found);
    }
}

/// <summary>
/// The Metering module's usage register, as this module is allowed to see it — a dictionary rather
/// than a decade of meter readings (WP-2.17).
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="FakeMeterDirectory"/> and there for the same reason: WP-2.17's deposit
/// re-assessment prices measured usage, and a Customers test may not resolve the real
/// <c>UsageDirectory</c> because a metering schema is the thing this module must never know about.
/// </para>
/// <para>
/// <b>Nothing measured is the default</b>, which is what a premise with no reading history answers —
/// and the case a usage-based rule has to fall back to its minimum for. A test that wants an average
/// says so with <see cref="Measured"/>.
/// </para>
/// </remarks>
public sealed class FakeUsageDirectory : IUsageDirectory
{
    private readonly Dictionary<(Guid Premise, ServiceType Service), PremiseUsage> _usage = [];

    /// <summary>Every premise a caller asked about — how a test proves the seam was consulted at all.</summary>
    public List<Guid> Lookups { get; } = [];

    /// <summary>The periods the last caller asked to average over.</summary>
    public List<int> RequestedPeriods { get; } = [];

    /// <summary>Records that <paramref name="serviceLocationId"/> averages <paramref name="average"/> a month.</summary>
    public void Measured(Guid serviceLocationId, decimal average, ServiceType serviceType = ServiceType.Electricity, int periods = 2)
    {
        _usage[(serviceLocationId, serviceType)] = new PremiseUsage(
            serviceLocationId,
            average,
            periods,
            DaysCovered: periods * 30,
            FirstPeriodStart: DateTimeOffset.UnixEpoch,
            LastPeriodEnd: DateTimeOffset.UnixEpoch.AddDays(periods * 30));
    }

    /// <inheritdoc />
    public Task<PremiseUsage> AverageMonthlyAtLocationAsync(
        Guid serviceLocationId,
        ServiceType serviceType,
        int periods,
        CancellationToken cancellationToken = default)
    {
        Lookups.Add(serviceLocationId);
        RequestedPeriods.Add(periods);

        return Task.FromResult(
            _usage.GetValueOrDefault((serviceLocationId, serviceType)) ?? PremiseUsage.None(serviceLocationId));
    }
}

/// <summary>
/// The object store, as a dictionary. CONVENTIONS.md rule C, applied to storage: a checklist is
/// tested without a container, and MinIO's own round trip is one gate-tier test.
/// </summary>
/// <remarks>
/// <para>
/// It behaves the way the real store does in the two ways a caller can tell: a key that was never
/// written comes back as <see langword="null"/> rather than throwing, and putting the same key twice
/// replaces what was there. Those are the only behaviours WP-2.18's service depends on, so a double
/// that got them right cannot let a bug through by being generous.
/// </para>
/// <para>
/// <see cref="FailNextPut"/> exists because the one thing a fake store must be able to prove is what
/// happens when the real one is unavailable: no row is written, and the caller sees the store's own
/// exception rather than a half-recorded document.
/// </para>
/// </remarks>
public sealed class FakeDocumentStore : IDocumentStore
{
    private readonly Dictionary<string, StoredDocumentContent> _objects = new(StringComparer.Ordinal);

    /// <summary>Every key written, in order — how a test proves the bytes went in before the row did.</summary>
    public List<string> Written { get; } = [];

    /// <summary>Every key read back, in order.</summary>
    public List<string> Read { get; } = [];

    /// <summary>Makes the next <see cref="PutAsync"/> fail as an unreachable store would.</summary>
    public bool FailNextPut { get; set; }

    /// <summary>What is filed under <paramref name="key"/>, or <see langword="null"/>.</summary>
    public StoredDocumentContent? At(string key) => _objects.GetValueOrDefault(key);

    /// <summary>How many objects the store holds.</summary>
    public int Count => _objects.Count;

    /// <summary>Removes the object at <paramref name="key"/>, standing in for a bucket somebody emptied.</summary>
    public void Lose(string key) => _objects.Remove(key);

    /// <inheritdoc />
    public Task<StoredDocument> PutAsync(DocumentUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        if (FailNextPut)
        {
            FailNextPut = false;

            throw new DocumentStoreException($"The object store refused to file '{upload.Key}'.");
        }

        _objects[upload.Key] = new StoredDocumentContent(
            upload.Key,
            upload.ContentType,
            upload.Content.Length,
            upload.Content.ToArray());

        Written.Add(upload.Key);

        return Task.FromResult(new StoredDocument(upload.Key, upload.ContentType, upload.Content.Length));
    }

    /// <inheritdoc />
    public Task<StoredDocumentContent?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Read.Add(key);

        return Task.FromResult(_objects.GetValueOrDefault(key));
    }
}

/// <summary>
/// The payment arrangements the fourth disconnection test asks about (WP-2.19), standing in for a
/// register WP-2.20 has not built yet.
/// </summary>
/// <remarks>
/// The production composition registers <c>NoPaymentArrangements</c>, which answers nothing for
/// everybody — which is honest, and untestable: a test that wants to prove an arrangement suppresses
/// disconnection needs to be able to say there is one. So the fast tier stands this in front of the
/// seam, and <c>CustomersModuleTests</c> is what pins the real composition to the null one.
/// </remarks>
public sealed class FakePaymentArrangementDirectory : IPaymentArrangementDirectory
{
    private readonly Dictionary<Guid, PaymentArrangementStanding> _standings = [];

    /// <summary>Puts an arrangement against an account that stops it being cut off.</summary>
    public void Active(Guid serviceAccountId) =>
        _standings[serviceAccountId] = new PaymentArrangementStanding(serviceAccountId, "Active", SuppressesDisconnection: true);

    /// <summary>Puts a broken arrangement against an account, which protects it from nothing.</summary>
    public void Broken(Guid serviceAccountId) =>
        _standings[serviceAccountId] = new PaymentArrangementStanding(serviceAccountId, "Broken", SuppressesDisconnection: false);

    /// <inheritdoc />
    public Task<PaymentArrangementStanding?> StandingForAccountAsync(
        Guid serviceAccountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_standings.GetValueOrDefault(serviceAccountId));
}
