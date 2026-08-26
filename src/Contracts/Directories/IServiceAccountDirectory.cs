using GridCore.Contracts.Services;

namespace GridCore.Contracts.Directories;

/// <summary>
/// A service account as another module sees it: who is served, where, and whether the account is
/// in a state that may be billed. Nothing more.
/// </summary>
/// <remarks>
/// A DTO, never the entity — the same rule <see cref="ServiceLocationSummary"/> follows and for the
/// same reason: <c>ServiceAccount</c> is an EF type in the Customers schema with a history
/// collection hanging off it, and handing it across the boundary would let a caller walk into
/// tables it must never read.
/// </remarks>
/// <param name="Id">Identifier of the account, in the Customers schema.</param>
/// <param name="AccountNumber">The number quoted on a bill, e.g. <c>A-000001</c>.</param>
/// <param name="CustomerId">Who is served.</param>
/// <param name="CustomerName">Their name, so a bill header needs no second lookup.</param>
/// <param name="ServiceLocationId">Where they are served.</param>
/// <param name="Status">The account's status, by name — Contracts takes no dependency on the module's enum.</param>
/// <param name="ServiceType">
/// Which service this account takes (WP-2.17). The one field here carried as its own enum rather
/// than by name, because <see cref="Services.ServiceType"/> is declared in <c>Contracts</c> and
/// belongs to no module — see the type's own remarks.
/// </param>
/// <param name="IsMetered">
/// Whether a device at the premise measures what this account consumes. Derived from
/// <paramref name="ServiceType"/> through <see cref="ServiceTypes.IsMetered"/> and carried on the
/// record anyway, so a caller can act on it without importing the rule and, one day, without
/// GridCore's answer having to be derivable from the service alone.
/// </param>
/// <param name="HoldsPremise">
/// Whether the account still holds its premise <i>for its own service</i>, which is what makes it
/// "the open electricity account here". Decided by Customers, because the rule belongs to the
/// lifecycle that module owns.
/// </param>
/// <param name="ServiceStartedAt">When supply was most recently energised, if it ever was.</param>
public sealed record ServiceAccountSummary(
    Guid Id,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    Guid ServiceLocationId,
    string Status,
    ServiceType ServiceType,
    bool IsMetered,
    bool HoldsPremise,
    DateTimeOffset? ServiceStartedAt);

/// <summary>
/// Read access to the service account registry for modules that are not Customers.
/// </summary>
/// <remarks>
/// <para>
/// The second cross-module read seam in GridCore, shaped exactly like
/// <see cref="IServiceLocationDirectory"/>: the interface lives in <c>Contracts</c>, the Customers
/// module registers the implementation, and a consumer takes the dependency without ever learning
/// that a <c>customers</c> schema exists.
/// </para>
/// <para>
/// Billing (WP-2.3) is the first consumer and needs it for a derivation WP-2.1 named but did not
/// have to make: a meter is fitted to a <i>premise</i>, so the account a bill is raised against is
/// "the account open at the premise this meter is on". That is
/// <see cref="FindOpenAtLocationAsync"/>, and it can answer with at most one account because
/// <c>ux_service_accounts_open_location</c> makes it a database fact.
/// </para>
/// <para>
/// <b>WP-2.17 made "the account open here" a question that needs a service to answer.</b> A premise
/// may now hold an electricity account, a water account and a wastewater account at once, so the
/// index those two lookups rely on is keyed on the premise <i>and the service</i> — and a caller
/// that used to ask for "the open account" now has to say which supply it means. Billing asks for
/// <see cref="ServiceType.Electricity"/> because a meter reading is an electricity reading; it is
/// stated at the call site rather than defaulted here, because a default would be this interface
/// guessing which supply a bill is for.
/// </para>
/// <para>
/// Read-only, for <see cref="IServiceLocationDirectory"/>'s reason: opening, starting, stopping and
/// closing an account stay behind <c>IServiceAccountService</c> inside Customers. A second module
/// that could move an account through its lifecycle is a second module that owns it.
/// </para>
/// </remarks>
public interface IServiceAccountDirectory
{
    /// <summary>One account, or <see langword="null"/> when there is no such id.</summary>
    Task<ServiceAccountSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The accounts among <paramref name="ids"/> that exist, keyed by id. Ids that match nothing are
    /// simply absent — a caller rendering a list has to cope with one it cannot resolve anyway.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The account currently taking <paramref name="serviceType"/> at
    /// <paramref name="serviceLocationId"/>, or <see langword="null"/> when nobody is taking that
    /// supply there. At most one exists: a closed account releases its premise for its own service,
    /// which is what frees it for the next occupant.
    /// </summary>
    /// <param name="serviceLocationId">The premise.</param>
    /// <param name="serviceType">Which supply. Required since WP-2.17 — see the interface's remarks.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<ServiceAccountSummary?> FindOpenAtLocationAsync(
        Guid serviceLocationId,
        ServiceType serviceType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The accounts taking <paramref name="serviceType"/> at the premises among
    /// <paramref name="serviceLocationIds"/>, keyed by <b>premise</b>. The batched form of
    /// <see cref="FindOpenAtLocationAsync"/>, so a billing run over a reading cycle makes one
    /// boundary call rather than one per meter.
    /// </summary>
    /// <param name="serviceLocationIds">The premises.</param>
    /// <param name="serviceType">Which supply. Keying by premise is only unambiguous once it is fixed.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindOpenAtLocationsAsync(
        IReadOnlyCollection<Guid> serviceLocationIds,
        ServiceType serviceType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Every</b> account open at <paramref name="serviceLocationId"/>, whatever service each
    /// takes, in service order (WP-2.17).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question the other two cannot ask, and it exists for one caller: Metering, deciding
    /// whether a revenue meter may be fitted at a premise. That decision is not about one supply —
    /// it is about whether <i>any</i> metered service is taken here — so a lookup keyed on a service
    /// would have to be called once per member of an enum this module does not own.
    /// </para>
    /// <para>
    /// Open accounts only, and at most one per service, so the answer is a short list rather than a
    /// page: a premise has three supplies at the very most and usually one.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ServiceAccountSummary>> ListOpenAtLocationAsync(
        Guid serviceLocationId,
        CancellationToken cancellationToken = default);
}
