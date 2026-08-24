namespace GridCore.Modules.Customers.Features.Shared;

/// <summary>
/// Base of the registry failures the endpoints translate into ProblemDetails responses. The
/// services throw these rather than returning result objects, so a rule can be enforced in the one
/// place that knows it and still reach the caller as the right status code.
/// </summary>
public abstract class RegistryException(string message) : Exception(message);

/// <summary>No customer with that id. Surfaces as 404.</summary>
public sealed class CustomerNotFoundException(Guid id)
    : RegistryException($"Customer '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid CustomerId { get; } = id;
}

/// <summary>No service location with that id. Surfaces as 404.</summary>
public sealed class ServiceLocationNotFoundException(Guid id)
    : RegistryException($"Service location '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid ServiceLocationId { get; } = id;
}

/// <summary>
/// The registry is not in a state that allows what was asked — an illegal status transition, or a
/// number already taken. Surfaces as 409.
/// </summary>
public sealed class RegistryWorkflowException(string message) : RegistryException(message);

/// <summary>
/// The thing as described could not be registered. Surfaces as 400. Edge validation catches most of
/// these first; this is the aggregate's own guard, which also protects a seeder or a later module
/// calling the service directly.
/// </summary>
public sealed class RegistryValidationException(string message) : RegistryException(message);
