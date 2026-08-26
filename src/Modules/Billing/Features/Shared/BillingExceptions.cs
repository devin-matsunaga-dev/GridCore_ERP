namespace GridCore.Modules.Billing.Features.Shared;

/// <summary>
/// Base of the failures the billing register's endpoints translate into ProblemDetails responses.
/// The service throws these rather than returning result objects, so a rule can be enforced in the
/// one place that knows it and still reach the caller as the right status code.
/// </summary>
/// <remarks>
/// Billing's own hierarchy rather than a shared one, for the reason WP-1.3 gave and WP-2.1 repeated:
/// every message in it names a bill, an account or a tariff, and a platform-wide "not found" would
/// have to be told what it was looking for.
/// </remarks>
public abstract class BillingRegistryException(string message) : Exception(message);

/// <summary>No bill with that id. Surfaces as 404.</summary>
public sealed class BillNotFoundException(Guid id)
    : BillingRegistryException($"Bill '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid BillId { get; } = id;
}

/// <summary>No account charge with that id (WP-2.16). Surfaces as 404.</summary>
public sealed class AccountChargeNotFoundException(Guid id)
    : BillingRegistryException($"Account charge '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid AccountChargeId { get; } = id;
}

/// <summary>
/// No rate plan with that code, or none in force on the day asked about. Surfaces as 404 — tariffs
/// are reference data, so a code that matches nothing is a caller naming a plan the utility has
/// never published rather than a malformed request.
/// </summary>
public sealed class RatePlanNotFoundException(string message) : BillingRegistryException(message);

/// <summary>
/// The service account a bill or a tariff assignment names is not one the Customers module knows.
/// Surfaces as 404, naming the account rather than the bill.
/// </summary>
/// <remarks>
/// Its own type rather than a validation failure because the answer depends on another module's
/// registry, which no validator at this edge can see — the same call WP-2.1 made for a premise.
/// </remarks>
public sealed class ServiceAccountNotFoundException(Guid id)
    : BillingRegistryException($"Service account '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid ServiceAccountId { get; } = id;
}

/// <summary>
/// The register is not in a state that allows what was asked — issuing a bill that is already
/// issued, cancelling one that is paid, paying more than is owed, or billing an account for a cycle
/// it has already been billed for. Surfaces as 409.
/// </summary>
public sealed class BillingWorkflowException(string message) : BillingRegistryException(message);

/// <summary>
/// The caller may not do this, though they were allowed through the door. Surfaces as 403.
/// </summary>
/// <remarks>
/// The first of these in this module (WP-2.14). Every other permission Billing enforces sits on the
/// route and nowhere else; the reprint demands its own as well, because CONVENTIONS.md's rule is
/// that a service enforces its permissions rather than trusting the endpoint that called it, and a
/// document that leaves the building is not the place to keep making the older exception.
/// </remarks>
public sealed class BillingPermissionException(string message) : BillingRegistryException(message);

/// <summary>
/// The bill or tariff as described could not be produced. Surfaces as 400. Edge validation catches
/// most of these first; this is the aggregate's own guard, which also protects a seeder or a later
/// module calling the service directly.
/// </summary>
public sealed class BillingValidationException(string message) : BillingRegistryException(message);
