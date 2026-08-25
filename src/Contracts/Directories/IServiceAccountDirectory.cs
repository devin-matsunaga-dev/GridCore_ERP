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
/// <param name="HoldsPremise">
/// Whether the account still holds its premise, which is what makes it "the open account here".
/// Decided by Customers, because the rule belongs to the lifecycle that module owns.
/// </param>
/// <param name="ServiceStartedAt">When supply was most recently energised, if it ever was.</param>
public sealed record ServiceAccountSummary(
    Guid Id,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    Guid ServiceLocationId,
    string Status,
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
    /// The account currently holding <paramref name="serviceLocationId"/>, or
    /// <see langword="null"/> when nobody is taking service there. At most one exists: a closed
    /// account releases its premise, which is what frees it for the next occupant.
    /// </summary>
    Task<ServiceAccountSummary?> FindOpenAtLocationAsync(
        Guid serviceLocationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The accounts open at the premises among <paramref name="serviceLocationIds"/>, keyed by
    /// <b>premise</b>. The batched form of <see cref="FindOpenAtLocationAsync"/>, so a billing run
    /// over a reading cycle makes one boundary call rather than one per meter.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindOpenAtLocationsAsync(
        IReadOnlyCollection<Guid> serviceLocationIds,
        CancellationToken cancellationToken = default);
}
