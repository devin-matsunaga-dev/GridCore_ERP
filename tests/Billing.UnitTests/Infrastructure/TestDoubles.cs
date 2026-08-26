using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Contracts.Services;
using GridCore.Platform.Messaging;
using GridCore.Platform.Security;

namespace GridCore.Modules.Billing.UnitTests.Infrastructure;

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
/// Holds every permission unless a test names the ones it holds. That default is what every billing
/// test was written against — they are about the register, not about authorization — while WP-2.14's
/// reprint, the one act in this module a service gates for itself, hands over a narrowed set. The
/// shape the Customers fast tier already uses.
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
        new("auth0|officer", "Bea Santos", permissions.ToHashSet(StringComparer.Ordinal));
}

/// <summary>
/// Captures what was published instead of writing it to the outbox. The real publisher and the
/// outbox are the platform's, tested in the gate tier; what a billing test needs to know is that
/// issuing a bill published the right fact, inside the transaction.
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
/// The Customers module's service account registry, as this module is allowed to see it — a
/// dictionary rather than a database.
/// </summary>
/// <remarks>
/// This is why the fast tier can test a billing run at all. The real implementation lives in another
/// module over another schema; because Billing depends on the
/// <see cref="IServiceAccountDirectory"/> interface and never on that module, a test supplies an
/// account in one line and no Postgres container is needed to answer "who is served here".
/// </remarks>
public sealed class FakeServiceAccountDirectory : IServiceAccountDirectory
{
    private readonly Dictionary<Guid, ServiceAccountSummary> _accounts = [];
    private int _ordinal;

    /// <summary>Every premise the register asked about, so a test can assert it went through the seam.</summary>
    public List<Guid> Lookups { get; } = [];

    /// <summary>
    /// Adds an account holding <paramref name="serviceLocationId"/> and hands it back.
    /// </summary>
    /// <param name="serviceLocationId">The premise it is open at.</param>
    /// <param name="status">Its lifecycle status, by name.</param>
    /// <param name="energised">
    /// Whether supply was ever switched on. An account that never was is not billed for the units on
    /// the meter at its premise.
    /// </param>
    /// <param name="accountNumber">Its number, if the test needs a specific one.</param>
    /// <param name="serviceType">Which supply it takes. Electricity, as every account a billing run touches is.</param>
    public ServiceAccountSummary Add(
        Guid serviceLocationId,
        string status = "Active",
        bool energised = true,
        string? accountNumber = null,
        ServiceType serviceType = ServiceType.Electricity)
    {
        var id = Guid.CreateVersion7();

        var account = new ServiceAccountSummary(
            id,
            accountNumber ?? $"A-{++_ordinal:000000}",
            Guid.CreateVersion7(),
            $"Customer {_ordinal}",
            serviceLocationId,
            status,
            serviceType,
            ServiceTypes.IsMetered(serviceType),
            HoldsPremise: !string.Equals(status, "Closed", StringComparison.Ordinal),
            energised ? DateTimeOffset.UnixEpoch : null);

        _accounts[id] = account;

        return account;
    }

    /// <inheritdoc />
    public Task<ServiceAccountSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Lookups.Add(id);

        return Task.FromResult(_accounts.GetValueOrDefault(id));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        Lookups.AddRange(ids);

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
        CancellationToken cancellationToken = default)
    {
        Lookups.Add(serviceLocationId);

        return Task.FromResult(_accounts.Values.FirstOrDefault(account =>
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

        var wanted = serviceLocationIds.Distinct().ToHashSet();

        // Filtered on the service as the real directory is, so a test that seeds two accounts at one
        // premise proves the same thing here that ux_service_accounts_open_location proves there.
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
        Lookups.Add(serviceLocationId);

        IReadOnlyList<ServiceAccountSummary> found =
        [
            .. _accounts.Values
                .Where(account => account.ServiceLocationId == serviceLocationId && account.HoldsPremise)
                .OrderBy(account => account.ServiceType),
        ];

        return Task.FromResult(found);
    }
}

/// <summary>
/// The Metering module's reading register, as this module is allowed to see it — a list rather than
/// a database. The sibling of <see cref="FakeServiceAccountDirectory"/> and there for the same
/// reason.
/// </summary>
public sealed class FakeMeterReadingDirectory : IMeterReadingDirectory
{
    private readonly List<MeterReadingSummary> _readings = [];
    private int _ordinal;

    /// <summary>Every cycle the register asked for.</summary>
    public List<string> Cycles { get; } = [];

    /// <summary>
    /// Adds a reading and hands it back.
    /// </summary>
    /// <param name="serviceLocationId">The premise it was taken at.</param>
    /// <param name="consumption">Units used, or <see langword="null"/> where there is nothing to bill.</param>
    /// <param name="cycleCode">The cycle it belongs to.</param>
    /// <param name="readingDate">When the dials were read — the end of the billed period.</param>
    /// <param name="periodDays">How long the period is, in days.</param>
    /// <param name="exceptionCode">Why it is on the worklist, if it is.</param>
    public MeterReadingSummary Add(
        Guid serviceLocationId,
        decimal? consumption,
        string cycleCode,
        DateTimeOffset readingDate,
        int periodDays = 30,
        string exceptionCode = "None")
    {
        var previous = consumption is null ? (decimal?)null : 1_000m;

        var reading = new MeterReadingSummary(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            $"MTR-{++_ordinal:000000}",
            serviceLocationId,
            readingDate,
            consumption is null ? null : previous + consumption,
            previous,
            consumption is null ? null : readingDate.AddDays(-periodDays),
            consumption,
            exceptionCode,
            !string.Equals(exceptionCode, "None", StringComparison.Ordinal),
            cycleCode);

        _readings.Add(reading);

        return reading;
    }

    /// <summary>Adds a reading with no period at all, for the guard that refuses to bill one.</summary>
    public MeterReadingSummary AddWithoutPeriod(Guid serviceLocationId, decimal consumption, string cycleCode, DateTimeOffset readingDate)
    {
        var reading = new MeterReadingSummary(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            $"MTR-{++_ordinal:000000}",
            serviceLocationId,
            readingDate,
            consumption,
            null,
            null,
            consumption,
            "None",
            false,
            cycleCode);

        _readings.Add(reading);

        return reading;
    }

    /// <inheritdoc />
    public Task<MeterReadingSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_readings.FirstOrDefault(reading => reading.Id == id));

    /// <inheritdoc />
    public Task<IReadOnlyList<MeterReadingSummary>> ForCycleAsync(
        string cycleCode,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Cycles.Add(cycleCode);

        IReadOnlyList<MeterReadingSummary> found = _readings
            .Where(reading => string.Equals(reading.CycleCode, cycleCode, StringComparison.Ordinal))
            .Take(limit)
            .ToList();

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MeterReadingSummary>> AtLocationAsync(
        Guid serviceLocationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MeterReadingSummary> found = _readings
            .Where(reading => reading.ServiceLocationId == serviceLocationId)
            .OrderByDescending(reading => reading.ReadingDate)
            .Take(limit)
            .ToList();

        return Task.FromResult(found);
    }
}
