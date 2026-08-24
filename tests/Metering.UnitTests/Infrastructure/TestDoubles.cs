using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
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
