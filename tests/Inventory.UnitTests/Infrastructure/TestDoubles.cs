using GridCore.Platform.Security;

namespace GridCore.Modules.Inventory.UnitTests.Infrastructure;

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
    /// <remarks>
    /// Always true. What a caller is <i>allowed</i> to do is decided by the endpoint's permission
    /// policy, which <c>StockItemEndpointsTests</c> asserts on directly — a service test that also
    /// re-checked permissions would be testing the double.
    /// </remarks>
    public bool HasPermission(string permission) => true;
}
