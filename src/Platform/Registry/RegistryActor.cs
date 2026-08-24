using GridCore.Platform.Security;

namespace GridCore.Platform.Registry;

/// <summary>
/// Who did something, as a registry record stores it. Distinct from the audit trail on purpose: an
/// audit entry answers "who changed this row" for an administrator, while a registry history is
/// part of the service record somebody reads back — on the phone to a customer, or off a tablet in
/// front of a transformer — so the name is captured alongside the id rather than resolved against
/// the identity provider years later, when the person may no longer exist there.
/// </summary>
/// <param name="Id">The identity-provider subject id, or <see cref="SystemUser.SystemUserId"/>.</param>
/// <param name="Name">Display name at the time, where one was known.</param>
public sealed record RegistryActor(string Id, string? Name)
{
    /// <summary>Longest actor id or name stored.</summary>
    public const int MaxLength = 256;

    /// <summary>The actor behind the current call.</summary>
    public static RegistryActor Of(ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);

        return new RegistryActor(currentUser.UserId, currentUser.UserName);
    }
}
