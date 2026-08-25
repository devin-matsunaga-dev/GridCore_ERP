namespace GridCore.Modules.Payments.Features.Shared;

/// <summary>
/// Base of the failures the payments register's endpoints translate into ProblemDetails responses.
/// The service throws these rather than returning result objects, so a rule can be enforced in the
/// one place that knows it and still reach the caller as the right status code.
/// </summary>
/// <remarks>
/// Payments' own hierarchy rather than a shared one, for the reason WP-1.3 gave and every registry
/// since has repeated: every message in it names a payment, a bill or an account, and a
/// platform-wide "not found" would have to be told what it was looking for.
/// </remarks>
public abstract class PaymentRegistryException(string message) : Exception(message);

/// <summary>No payment with that id. Surfaces as 404.</summary>
public sealed class PaymentNotFoundException(Guid id)
    : PaymentRegistryException($"Payment '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid PaymentId { get; } = id;
}

/// <summary>
/// The bill a payment names is not one the Billing module knows. Surfaces as 404, naming the bill
/// rather than the payment.
/// </summary>
/// <remarks>
/// Its own type rather than a validation failure because the answer depends on another module's
/// register, which no validator at this edge can see — the same call WP-2.1 made for a premise and
/// WP-2.3 for a service account.
/// </remarks>
public sealed class BillNotFoundException(Guid id)
    : PaymentRegistryException($"Bill '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid BillId { get; } = id;
}

/// <summary>
/// The service account the bill belongs to is not one the Customers module knows. Surfaces as 404.
/// </summary>
public sealed class ServiceAccountNotFoundException(Guid id)
    : PaymentRegistryException($"Service account '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid ServiceAccountId { get; } = id;
}

/// <summary>
/// The register is not in a state that allows what was asked — paying a bill that is not owed,
/// paying more than is outstanding on it, or answering a payment that has already been settled.
/// Surfaces as 409.
/// </summary>
/// <remarks>
/// <b>A declined payment is not one of these.</b> A refusal is an answer the provider gave, not a
/// state the register was in: the attempt is recorded, returned as <c>Declined</c>, and the caller
/// gets 200. Turning it into a 409 would be a 409 with a row behind it, which is the one response
/// nobody can act on.
/// </remarks>
public sealed class PaymentWorkflowException(string message) : PaymentRegistryException(message);

/// <summary>
/// The payment as described could not be taken. Surfaces as 400. Edge validation catches most of
/// these first; this is the aggregate's own guard, which also protects a later module calling the
/// service directly.
/// </summary>
public sealed class PaymentValidationException(string message) : PaymentRegistryException(message);
