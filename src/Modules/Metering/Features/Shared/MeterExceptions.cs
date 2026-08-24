namespace GridCore.Modules.Metering.Features.Shared;

/// <summary>
/// Base of the failures the meter register's endpoints translate into ProblemDetails responses. The
/// service throws these rather than returning result objects, so a rule can be enforced in the one
/// place that knows it and still reach the caller as the right status code.
/// </summary>
/// <remarks>
/// Metering's own hierarchy rather than a shared one, for the reason WP-1.3 gave: every message in
/// it names a meter, and a platform-wide "not found" would have to be told what it was looking for.
/// </remarks>
public abstract class MeterRegistryException(string message) : Exception(message);

/// <summary>No meter with that id. Surfaces as 404.</summary>
public sealed class MeterNotFoundException(Guid id)
    : MeterRegistryException($"Meter '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid MeterId { get; } = id;
}

/// <summary>
/// The register is not in a state that allows what was asked — an illegal status move, a meter
/// number or serial already taken, a premise that already has a meter on it, or a fitting change
/// asked of the wrong endpoint. Surfaces as 409.
/// </summary>
public sealed class MeterWorkflowException(string message) : MeterRegistryException(message);

/// <summary>
/// The meter as described could not be registered or changed. Surfaces as 400. Edge validation
/// catches most of these first; this is the aggregate's own guard, which also protects a seeder or
/// a later module calling the service directly.
/// </summary>
public sealed class MeterValidationException(string message) : MeterRegistryException(message);

/// <summary>
/// The premise a meter was to be fitted at is not one the Customers module knows. Surfaces as 404,
/// naming the premise rather than the meter — the meter is fine, the id in the body is not.
/// </summary>
/// <remarks>
/// Its own type rather than a validation failure because the answer depends on another module's
/// registry, which no validator at this edge can see.
/// </remarks>
public sealed class ServiceLocationNotFoundException(Guid id)
    : MeterRegistryException($"Service location '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid ServiceLocationId { get; } = id;
}
