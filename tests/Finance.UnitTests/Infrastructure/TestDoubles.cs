using GridCore.Platform.Security;

namespace GridCore.Modules.Finance.UnitTests.Infrastructure;

/// <summary>A clock the test moves by hand, so nothing waits on wall time.</summary>
public sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => _now;

    private DateTimeOffset _now = now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>A caller with an explicit identity, so tests never build a token.</summary>
/// <remarks>
/// Rarely what a Finance test wants: postings happen in consumers, which run outside any request,
/// so the ledger's ordinary actor is <see cref="SystemUser"/>. This is here for the one assertion
/// that cares — that an entry records whoever posted it, whoever that turns out to be.
/// </remarks>
public sealed class FakeCurrentUser(string userId, string? userName = null) : ICurrentUser
{
    /// <inheritdoc />
    public string UserId { get; } = userId;

    /// <inheritdoc />
    public string? UserName { get; } = userName ?? userId;

    /// <inheritdoc />
    public bool HasPermission(string permission) => true;
}
