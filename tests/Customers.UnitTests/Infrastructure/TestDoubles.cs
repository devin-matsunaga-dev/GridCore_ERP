using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Platform.Messaging;
using GridCore.Platform.Security;

namespace GridCore.Modules.Customers.UnitTests.Infrastructure;

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
    private int _ordinal;

    /// <summary>Every bill the ledger asked about, so a test can assert it went through the seam.</summary>
    public List<Guid> Lookups { get; } = [];

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
}
