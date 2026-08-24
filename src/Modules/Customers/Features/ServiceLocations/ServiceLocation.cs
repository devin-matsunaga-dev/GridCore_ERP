using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.Features.ServiceLocations;

/// <summary>
/// A premise service is delivered to — the thing a meter is fitted at, a crew is sent to and an
/// asset stands on. It exists independently of whoever is served there, so a customer moving out
/// does not take the premise off the network.
/// </summary>
public sealed class ServiceLocation
{
    /// <summary>Longest description stored.</summary>
    public const int DescriptionLength = 512;

    /// <summary>Longest reason recorded against a deactivation.</summary>
    public const int ReasonLength = 1024;

    private ServiceLocation()
    {
        // EF materialisation. Address is set by EF as an owned type.
        LocationCode = string.Empty;
        Address = null!;
    }

    /// <summary>Identifier of this location. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The code quoted on a work order, e.g. <c>L-000001</c>. Unique across locations.</summary>
    public string LocationCode { get; private init; }

    /// <summary>Where the premise is.</summary>
    public Address Address { get; private set; }

    /// <summary>What the premise is, in a crew's words — "Pump house behind the school".</summary>
    public string? Description { get; private set; }

    /// <summary>
    /// Whether service may still be delivered here. A demolished or permanently disconnected
    /// premise is deactivated, never deleted: its meters, work orders and bills are history that
    /// later reports read.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>Why it was last deactivated or reactivated.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>When the location was registered.</summary>
    public DateTimeOffset RegisteredAt { get; private init; }

    /// <summary>
    /// Registers a location under a code the caller has already reserved — see
    /// <see cref="IRegistryNumberGenerator"/>.
    /// </summary>
    /// <exception cref="RegistryValidationException">The code is missing, or the address is incomplete.</exception>
    public static ServiceLocation Register(
        string locationCode,
        Address address,
        DateTimeOffset now,
        string? description = null,
        bool isActive = true)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (string.IsNullOrWhiteSpace(locationCode))
        {
            throw new RegistryValidationException("'locationCode' is required to register a service location.");
        }

        return new ServiceLocation
        {
            Id = Guid.CreateVersion7(now),
            LocationCode = locationCode.Trim(),
            Address = address,
            Description = Clean(description, DescriptionLength),
            IsActive = isActive,
            RegisteredAt = now,
        };
    }

    /// <summary>
    /// Changes what can be corrected about a premise. The code is not among them — a meter, a work
    /// order and an asset all quote it.
    /// </summary>
    public void UpdateDetails(Address address, string? description, bool isActive, string? statusReason = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Only recorded when the flag actually moves, so a plain address correction cannot silently
        // overwrite why the premise was taken out of service.
        if (isActive != IsActive)
        {
            StatusReason = Clean(statusReason, ReasonLength);
        }

        Address = address;
        Description = Clean(description, DescriptionLength);
        IsActive = isActive;
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length is 0)
        {
            return null;
        }

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
