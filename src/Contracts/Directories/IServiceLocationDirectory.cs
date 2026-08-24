namespace GridCore.Contracts.Directories;

/// <summary>
/// A premise as another module sees it: enough to name it on a screen and to decide whether
/// something may be attached to it, and nothing more.
/// </summary>
/// <remarks>
/// A DTO, never the entity. <c>ServiceLocation</c> is an EF type in the Customers schema, and
/// handing it across the boundary would let a caller walk a navigation into tables it must never
/// read (ARCHITECTURE.md's module rule, and CONVENTIONS.md's "no EF types in Contracts").
/// </remarks>
/// <param name="Id">Identifier of the premise, in the Customers schema.</param>
/// <param name="LocationCode">The code quoted on a work order, e.g. <c>L-000001</c>.</param>
/// <param name="FormattedAddress">The one-line address the owning module renders.</param>
/// <param name="City">Town or village.</param>
/// <param name="Region">State, province or island.</param>
/// <param name="IsActive">Whether service may still be delivered here.</param>
public sealed record ServiceLocationSummary(
    Guid Id,
    string LocationCode,
    string FormattedAddress,
    string City,
    string Region,
    bool IsActive);

/// <summary>
/// Read access to the premise registry for modules that are not Customers — the first
/// cross-module read seam in GridCore.
/// </summary>
/// <remarks>
/// <para>
/// ARCHITECTURE.md allows exactly two ways across a module boundary: a service interface for reads
/// and a domain event for effects. A module class library may not reference another module, so the
/// interface lives here in <c>Contracts</c> and the Customers module registers the implementation.
/// A consumer takes this dependency and never learns that a <c>customers</c> schema exists.
/// </para>
/// <para>
/// Read-only on purpose. Registering, correcting and deactivating a premise stay behind
/// <c>IServiceLocationService</c> inside Customers: a second module that could write to the
/// registry is a second module that owns it.
/// </para>
/// </remarks>
public interface IServiceLocationDirectory
{
    /// <summary>One premise, or <see langword="null"/> when there is no such id.</summary>
    Task<ServiceLocationSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The premises among <paramref name="ids"/> that exist, keyed by id. Ids that match nothing are
    /// simply absent — a caller rendering a list has to cope with a premise it cannot resolve
    /// anyway, and throwing would make one bad id lose the whole page.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ServiceLocationSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Premises service may currently be delivered to, newest first — what a demo seeder in another
    /// module needs in order to attach anything to the premises Customers has already seeded.
    /// </summary>
    Task<IReadOnlyList<ServiceLocationSummary>> ListServiceableAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
