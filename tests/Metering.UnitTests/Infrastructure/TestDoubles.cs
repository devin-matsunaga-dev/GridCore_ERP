using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Contracts.Providers;
using GridCore.Contracts.Services;
using GridCore.Platform.Messaging;
using GridCore.Platform.Security;

namespace GridCore.Modules.Metering.UnitTests.Infrastructure;

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
/// outbox are the platform's, tested in the gate tier; what a register test needs to know is that
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
/// The Customers module's premise registry, as this module is allowed to see it — a dictionary
/// rather than a database.
/// </summary>
/// <remarks>
/// This is the whole reason the fast tier can test meter assignment at all. The real implementation
/// lives in another module over another schema; because Metering depends on the
/// <see cref="IServiceLocationDirectory"/> interface and never on that module, a test supplies a
/// premise in one line and no Postgres container is needed to answer "does this premise exist".
/// Rule C of CONVENTIONS.md's speed rules, falling straight out of ARCHITECTURE.md's boundary rule.
/// </remarks>
public sealed class FakeServiceLocationDirectory : IServiceLocationDirectory
{
    private readonly Dictionary<Guid, ServiceLocationSummary> _premises = [];

    /// <summary>Every call the register made, so a test can assert it went through the seam.</summary>
    public List<Guid> Lookups { get; } = [];

    /// <summary>Adds a premise the directory will answer for, and hands back its id.</summary>
    public Guid Add(string locationCode, bool isActive = true)
    {
        var id = Guid.CreateVersion7();

        _premises[id] = new ServiceLocationSummary(
            id,
            locationCode,
            $"{locationCode} Somewhere Road, Songsong, Rota",
            "Songsong",
            "Rota",
            isActive);

        return id;
    }

    /// <inheritdoc />
    public Task<ServiceLocationSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Lookups.Add(id);

        return Task.FromResult(_premises.GetValueOrDefault(id));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, ServiceLocationSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        Lookups.AddRange(ids);

        IReadOnlyDictionary<Guid, ServiceLocationSummary> found = ids
            .Distinct()
            .Select(_premises.GetValueOrDefault)
            .OfType<ServiceLocationSummary>()
            .ToDictionary(premise => premise.Id);

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServiceLocationSummary>> ListServiceableAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ServiceLocationSummary> serviceable = _premises.Values
            .Where(premise => premise.IsActive)
            .Take(limit)
            .ToList();

        return Task.FromResult(serviceable);
    }
}

/// <summary>
/// A reading provider that returns exactly what a test told it to, so a case about the register can
/// pin the numbers instead of predicting the simulator's.
/// </summary>
/// <remarks>
/// The simulator is deterministic and needs no infrastructure, so most tests use the real one. This
/// exists for the cases that have to state a specific dial reading — a rollover, a missing read, a
/// value the register cannot display — and for asserting what the module asked the provider for.
/// </remarks>
public sealed class ScriptedMeterReadingProvider : IMeterReadingProvider
{
    private readonly Dictionary<Guid, decimal?> _readings = [];

    /// <inheritdoc />
    public string Name => "Scripted meter reading provider";

    /// <summary>The route the module last described, so a test can assert what it was told.</summary>
    public IReadOnlyList<MeterReadingRequest> LastRoute { get; private set; } = [];

    /// <summary>Answers <paramref name="reading"/> for <paramref name="meterId"/>; null is a missing read.</summary>
    public ScriptedMeterReadingProvider Returns(Guid meterId, decimal? reading)
    {
        _readings[meterId] = reading;

        return this;
    }

    /// <summary>Extra results for meters that are not on the route, so the register's guard can be exercised.</summary>
    public List<MeterReadingResult> Extra { get; } = [];

    /// <inheritdoc />
    public Task<MeterReadingBatch> ReadCycleAsync(MeterReadingCycle cycle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        LastRoute = cycle.Meters;

        var readings = cycle.Meters
            .Where(meter => _readings.ContainsKey(meter.MeterId))
            .Select(meter => new MeterReadingResult(meter.MeterId, _readings[meter.MeterId], cycle.ReadAt, null))
            .Concat(Extra)
            .ToList();

        return Task.FromResult(new MeterReadingBatch(cycle.CycleCode, cycle.ReadAt, cycle.Seed, readings));
    }
}

/// <summary>
/// The Customers module's service account registry, as this module is allowed to see it — a list
/// rather than a database (WP-2.17).
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="FakeServiceLocationDirectory"/> and there for the same reason. Fitting
/// a meter now asks one more question across the boundary: is every supply taken at this premise
/// unmetered, in which case a revenue meter has nothing to measure there.
/// </para>
/// <para>
/// <b>Empty is the ordinary case</b>, and it allows the fit. A premise with no account at all is a
/// new build metered before anybody applies, and every test written before WP-2.17 is one — which is
/// exactly why the guard refuses only the premise where service IS taken and none of it is measured.
/// </para>
/// </remarks>
public sealed class FakeServiceAccountDirectory : IServiceAccountDirectory
{
    private readonly List<ServiceAccountSummary> _accounts = [];
    private int _ordinal;

    /// <summary>Every premise a caller asked about, so a test can assert it went through the seam.</summary>
    public List<Guid> Lookups { get; } = [];

    /// <summary>Opens an account of <paramref name="serviceType"/> at <paramref name="serviceLocationId"/>.</summary>
    public ServiceAccountSummary Open(Guid serviceLocationId, ServiceType serviceType, string status = "Active")
    {
        var account = new ServiceAccountSummary(
            Guid.CreateVersion7(),
            $"A-{++_ordinal:000000}",
            Guid.CreateVersion7(),
            $"Customer {_ordinal}",
            serviceLocationId,
            status,
            serviceType,
            ServiceTypes.IsMetered(serviceType),
            HoldsPremise: !string.Equals(status, "Closed", StringComparison.Ordinal),
            ServiceStartedAt: DateTimeOffset.UnixEpoch);

        _accounts.Add(account);

        return account;
    }

    /// <inheritdoc />
    public Task<ServiceAccountSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.FirstOrDefault(account => account.Id == id));

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        IReadOnlyDictionary<Guid, ServiceAccountSummary> found = _accounts
            .Where(account => ids.Contains(account.Id))
            .ToDictionary(account => account.Id);

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<ServiceAccountSummary?> FindOpenAtLocationAsync(
        Guid serviceLocationId,
        ServiceType serviceType,
        CancellationToken cancellationToken = default)
    {
        Lookups.Add(serviceLocationId);

        return Task.FromResult(_accounts.FirstOrDefault(account =>
            account.ServiceLocationId == serviceLocationId && account.ServiceType == serviceType && account.HoldsPremise));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindOpenAtLocationsAsync(
        IReadOnlyCollection<Guid> serviceLocationIds,
        ServiceType serviceType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceLocationIds);

        Lookups.AddRange(serviceLocationIds);

        IReadOnlyDictionary<Guid, ServiceAccountSummary> found = _accounts
            .Where(account => account.HoldsPremise && account.ServiceType == serviceType)
            .Where(account => serviceLocationIds.Contains(account.ServiceLocationId))
            .ToDictionary(account => account.ServiceLocationId);

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServiceAccountSummary>> ListOpenAtLocationAsync(
        Guid serviceLocationId,
        CancellationToken cancellationToken = default)
    {
        Lookups.Add(serviceLocationId);

        IReadOnlyList<ServiceAccountSummary> found =
        [
            .. _accounts
                .Where(account => account.ServiceLocationId == serviceLocationId && account.HoldsPremise)
                .OrderBy(account => account.ServiceType),
        ];

        return Task.FromResult(found);
    }
}
