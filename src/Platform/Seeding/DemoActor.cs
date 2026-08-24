using GridCore.Platform.Security;

namespace GridCore.Platform.Seeding;

/// <summary>
/// Attribution for a row a demo seeder invented — a stand-in colleague, not a real account.
/// </summary>
/// <remarks>
/// Demo data has to look like somebody did it, or every seeded row reads as "system" and the
/// approval queue cannot demonstrate separation of duties. But it must never be confused with a
/// real identity: the ids are prefixed <c>demo:</c> (the identity provider issues opaque subject
/// ids, so no real caller can ever collide with one) and the actor holds no permissions at all, so
/// nothing can be authorised as a demo colleague.
/// </remarks>
/// <param name="Username">The dev realm username this stands in for, e.g. <c>warehouse</c>.</param>
/// <param name="DisplayName">The name shown beside the seeded row.</param>
public sealed record DemoActor(string Username, string DisplayName) : ICurrentUser
{
    /// <summary>Prefix marking an id as a demo stand-in rather than an identity-provider subject.</summary>
    public const string IdPrefix = "demo:";

    /// <inheritdoc />
    public string UserId => IdPrefix + Username;

    /// <inheritdoc />
    public string? UserName => DisplayName;

    /// <inheritdoc />
    /// <remarks>Always <see langword="false"/>: a demo attribution is a label, never an authorisation.</remarks>
    public bool HasPermission(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        return false;
    }
}
