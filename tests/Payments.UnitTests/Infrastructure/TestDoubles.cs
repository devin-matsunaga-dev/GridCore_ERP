using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Contracts.Providers;
using GridCore.Contracts.Services;
using GridCore.Platform.Messaging;
using GridCore.Platform.Security;

namespace GridCore.Modules.Payments.UnitTests.Infrastructure;

/// <summary>A clock the test moves by hand, so nothing waits on wall time.</summary>
public sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => _now;

    private DateTimeOffset _now = now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>A caller with an explicit identity, so tests never build a token.</summary>
public sealed class FakeCurrentUser(string userId, string? userName = null) : ICurrentUser
{
    /// <inheritdoc />
    public string UserId { get; } = userId;

    /// <inheritdoc />
    public string? UserName { get; } = userName ?? userId;

    /// <inheritdoc />
    public bool HasPermission(string permission) => true;
}

/// <summary>
/// Captures what was published instead of writing it to the outbox. The real publisher and the
/// outbox are the platform's, tested in the gate tier; what a payments test needs to know is that
/// taking money published the right fact — and that a refusal published nothing at all.
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
/// A payment provider whose answers the test chooses. The sandbox has its own tests; what a service
/// test needs is to say "the provider declines this one" without hunting for a payment number that
/// happens to draw a decline.
/// </summary>
public sealed class StubPaymentProvider : IPaymentProvider
{
    private readonly Queue<PaymentOutcome> _queued = new();

    /// <summary>What is answered when nothing is queued.</summary>
    public PaymentOutcome Default { get; set; } = PaymentOutcome.Approved;

    /// <summary>Every request the register put to the provider, so a test can assert what was sent.</summary>
    public List<PaymentAuthorizationRequest> Requests { get; } = [];

    /// <summary>The reference the provider answers with. Blank is how a test drives that failure.</summary>
    public string Reference { get; set; } = "SIM-TEST-0001";

    /// <inheritdoc />
    public string Name => "Stub payment provider";

    /// <summary>Queues the next answer, ahead of <see cref="Default"/>.</summary>
    public StubPaymentProvider WillAnswer(PaymentOutcome outcome)
    {
        _queued.Enqueue(outcome);

        return this;
    }

    /// <inheritdoc />
    public Task<PaymentAuthorizationResult> AuthorizeAsync(
        PaymentAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Requests.Add(request);

        var outcome = _queued.Count > 0 ? _queued.Dequeue() : Default;

        return Task.FromResult(new PaymentAuthorizationResult(
            outcome,
            Reference,
            DateTimeOffset.UnixEpoch,
            null));
    }
}

/// <summary>
/// The Billing module's register, as this module is allowed to see it — a dictionary rather than a
/// database.
/// </summary>
/// <remarks>
/// This is why the fast tier can test taking a payment at all. The real implementation lives in
/// another module over another schema; because Payments depends on the <see cref="IBillDirectory"/>
/// interface and never on that module, a test supplies a bill in one line and no Postgres container
/// is needed to answer "how much is owed here".
/// </remarks>
public sealed class FakeBillDirectory : IBillDirectory
{
    private readonly Dictionary<Guid, BillSummary> _bills = [];
    private int _ordinal;

    /// <summary>Every bill the register asked about, so a test can assert it went through the seam.</summary>
    public List<Guid> Lookups { get; } = [];

    /// <summary>
    /// Adds a bill and hands it back.
    /// </summary>
    /// <param name="serviceAccountId">The account it is billed to.</param>
    /// <param name="customerId">Who owes it.</param>
    /// <param name="amountDue">What is owed on it before anything has been paid.</param>
    /// <param name="amountPaid">How much has already been paid against it.</param>
    /// <param name="status">Its lifecycle status, by name.</param>
    /// <param name="currency">What its amounts are expressed in.</param>
    public BillSummary Add(
        Guid serviceAccountId,
        Guid customerId,
        decimal amountDue = 120.00m,
        decimal amountPaid = 0m,
        string status = "Issued",
        string currency = "USD")
    {
        var id = Guid.CreateVersion7();

        var bill = new BillSummary(
            id,
            $"BIL-{++_ordinal:000000}",
            serviceAccountId,
            $"A-{_ordinal:000000}",
            customerId,
            $"Customer {_ordinal}",
            currency,

            // The printed total and what is owed deliberately differ, as WP-2.4 made them: the
            // adjustment is what a payment checked against the total rather than the balance would
            // get wrong.
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
    /// <remarks>
    /// Payments never asks this — the statement WP-2.14 widened the seam for is composed in
    /// Customers, and this module has no document to write. Answering with nothing rather than
    /// throwing keeps the double a faithful <see cref="IBillDirectory"/> rather than a partial one:
    /// a test that started calling it would get an empty history, which is what a customer with no
    /// bills has.
    /// </remarks>
    public Task<IReadOnlyList<BillActivity>> ActivityForCustomerAsync(
        Guid customerId,
        DateOnly issuedOnOrBefore,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BillActivity>>([]);

    /// <inheritdoc />
    /// <remarks>
    /// Payments never asks this either — it is WP-2.15's question, asked by the Customers transition
    /// register about how far back a class change may be dated. Null for the reason the history above
    /// is empty: a faithful answer for a module that seeds no issued-bill dates.
    /// </remarks>
    public Task<DateOnly?> LastIssuedOnForCustomerAsync(Guid customerId, CancellationToken cancellationToken = default) =>
        Task.FromResult<DateOnly?>(null);

    /// <inheritdoc />
    /// <remarks>
    /// Nor this — WP-2.19's arrears is asked by the Customers delinquency register, which decides
    /// whether a supply may be cut off. An empty picture for the reason the two answers above are
    /// empty: a module that seeds no due dates has nothing honest to age.
    /// </remarks>
    public Task<AccountArrears> ArrearsForAccountAsync(
        Guid serviceAccountId,
        DateOnly asOf,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AccountArrears(
            serviceAccountId,
            "USD",
            asOf,
            OutstandingAmount: 0m,
            PastDueAmount: 0m,
            CurrentAmount: 0m,
            OldestDueDate: null,
            DaysPastDue: 0,
            Buckets: [],
            Bills: []));

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
}

/// <summary>
/// The Customers module's service account registry, as this module is allowed to see it. The
/// sibling of <see cref="FakeBillDirectory"/> and there for the same reason.
/// </summary>
public sealed class FakeServiceAccountDirectory : IServiceAccountDirectory
{
    private readonly Dictionary<Guid, ServiceAccountSummary> _accounts = [];
    private int _ordinal;

    /// <summary>Adds an account and hands it back.</summary>
    /// <param name="status">Its lifecycle status, by name.</param>
    public ServiceAccountSummary Add(string status = "Active")
    {
        var id = Guid.CreateVersion7();

        var account = new ServiceAccountSummary(
            id,
            $"A-{++_ordinal:000000}",
            Guid.CreateVersion7(),
            $"Customer {_ordinal}",
            Guid.CreateVersion7(),
            status,
            ServiceType.Electricity,
            IsMetered: true,
            HoldsPremise: !string.Equals(status, "Closed", StringComparison.Ordinal),
            DateTimeOffset.UnixEpoch);

        _accounts[id] = account;

        return account;
    }

    /// <summary>Forgets an account, so a test can drive the "the bill names an account nobody knows" path.</summary>
    public void Forget(Guid id) => _accounts.Remove(id);

    /// <inheritdoc />
    public Task<ServiceAccountSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.GetValueOrDefault(id));

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        IReadOnlyDictionary<Guid, ServiceAccountSummary> found = ids
            .Distinct()
            .Select(_accounts.GetValueOrDefault)
            .OfType<ServiceAccountSummary>()
            .ToDictionary(account => account.Id);

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<ServiceAccountSummary?> FindOpenAtLocationAsync(
        Guid serviceLocationId,
        ServiceType serviceType,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.Values.FirstOrDefault(account =>
            account.ServiceLocationId == serviceLocationId && account.ServiceType == serviceType && account.HoldsPremise));

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindOpenAtLocationsAsync(
        IReadOnlyCollection<Guid> serviceLocationIds,
        ServiceType serviceType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceLocationIds);

        var wanted = serviceLocationIds.Distinct().ToHashSet();

        IReadOnlyDictionary<Guid, ServiceAccountSummary> found = _accounts.Values
            .Where(account => account.HoldsPremise && account.ServiceType == serviceType)
            .Where(account => wanted.Contains(account.ServiceLocationId))
            .ToDictionary(account => account.ServiceLocationId);

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServiceAccountSummary>> ListOpenAtLocationAsync(
        Guid serviceLocationId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ServiceAccountSummary> found =
        [
            .. _accounts.Values
                .Where(account => account.ServiceLocationId == serviceLocationId && account.HoldsPremise)
                .OrderBy(account => account.ServiceType),
        ];

        return Task.FromResult(found);
    }
}
