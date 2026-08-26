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

/// <summary>No contact with that id. Surfaces as 404.</summary>
public sealed class CustomerContactNotFoundException(Guid id)
    : RegistryException($"Customer contact '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid CustomerContactId { get; } = id;
}

/// <summary>No note with that id. Surfaces as 404.</summary>
public sealed class CustomerNoteNotFoundException(Guid id)
    : RegistryException($"Customer note '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid CustomerNoteId { get; } = id;
}

/// <summary>No service location with that id. Surfaces as 404.</summary>
public sealed class ServiceLocationNotFoundException(Guid id)
    : RegistryException($"Service location '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid ServiceLocationId { get; } = id;
}

/// <summary>No service application with that id. Surfaces as 404.</summary>
public sealed class ServiceApplicationNotFoundException(Guid id)
    : RegistryException($"Service application '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid ServiceApplicationId { get; } = id;
}

/// <summary>
/// No such document on that application — or a row whose object has gone from the store. Surfaces
/// as 404.
/// </summary>
/// <remarks>
/// One exception for both, deliberately. From the caller's side they are the same answer — "that
/// document cannot be produced" — and the difference between them is a fault for an operator to
/// find in the trail, not a distinction a rep at a counter can act on differently.
/// </remarks>
public sealed class ApplicationDocumentNotFoundException(Guid id)
    : RegistryException($"Application document '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid ApplicationDocumentId { get; } = id;
}

/// <summary>No service account with that id. Surfaces as 404.</summary>
public sealed class ServiceAccountNotFoundException(Guid id)
    : RegistryException($"Service account '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid ServiceAccountId { get; } = id;
}

/// <summary>
/// The registry is not in a state that allows what was asked — an illegal status transition, or a
/// number already taken. Surfaces as 409.
/// </summary>
public sealed class RegistryWorkflowException(string message) : RegistryException(message);

/// <summary>
/// The caller may not do this, though they were allowed through the door. Surfaces as 403.
/// </summary>
/// <remarks>
/// The endpoint's own <c>RequirePermission</c> gate covers a whole route; this covers an act that
/// only <i>part</i> of a request performs — collecting a deposit on an intake (WP-2.8) — which
/// routing cannot see because it depends on what is in the body. Thrown by the service, never
/// assumed by the endpoint, exactly as <c>ApprovalPermissionException</c> is.
/// </remarks>
public sealed class RegistryPermissionException(string message) : RegistryException(message);

/// <summary>
/// The thing as described could not be registered. Surfaces as 400. Edge validation catches most of
/// these first; this is the aggregate's own guard, which also protects a seeder or a later module
/// calling the service directly.
/// </summary>
public sealed class RegistryValidationException(string message) : RegistryException(message);
