using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.ServiceLocations;

/// <summary>
/// Customers' answer to <see cref="IServiceLocationDirectory"/>: the premise registry as the rest
/// of GridCore is allowed to see it.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam ARCHITECTURE.md's boundary rule requires. Metering (WP-2.1) has to know that
/// the premise a meter is being fitted at exists and is still in service, and it may neither
/// reference this module nor read <c>customers.service_locations</c>. So it takes the interface
/// from <c>Contracts</c>, and this class — registered by <see cref="CustomersModule"/>, the only
/// place that knows both halves — is what answers it.
/// </para>
/// <para>
/// Every method projects to <see cref="ServiceLocationSummary"/> rather than returning the entity,
/// and every query is <c>AsNoTracking</c>: a caller outside this module has no business holding a
/// tracked premise, and a tracked one could be mutated into the unit of work by mistake.
/// </para>
/// </remarks>
public sealed class ServiceLocationDirectory(CustomersDbContext database) : IServiceLocationDirectory
{
    /// <summary>The largest page <see cref="ListServiceableAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = ServiceLocationService.MaxPageSize;

    /// <inheritdoc />
    public async Task<ServiceLocationSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var location = await database.ServiceLocations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return location is null ? null : Summarise(location);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ServiceLocationSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // Distinct before the query: a list of meters at one premise would otherwise send the same
        // id a dozen times, and the answer is keyed by id anyway.
        var wanted = ids.Distinct().ToArray();

        if (wanted.Length is 0)
        {
            return new Dictionary<Guid, ServiceLocationSummary>();
        }

        var located = await database.ServiceLocations
            .AsNoTracking()
            .Where(location => wanted.Contains(location.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return located.ToDictionary(location => location.Id, Summarise);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServiceLocationSummary>> ListServiceableAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var located = await database.ServiceLocations
            .AsNoTracking()
            .Where(location => location.IsActive)

            // Ordered by key: ids are Guid v7, so the primary-key index already orders
            // chronologically on Postgres and on the fast tier's SQLite alike.
            .OrderByDescending(location => location.Id)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return located.ConvertAll(Summarise);
    }

    /// <summary>
    /// Projects in memory rather than in the <c>Select</c>: <see cref="Address.OneLine"/> is a
    /// computed property EF cannot translate, and rebuilding the join here would be a second
    /// rendering of an address to keep in step with the one the module already publishes.
    /// </summary>
    private static ServiceLocationSummary Summarise(ServiceLocation location) =>
        new(
            location.Id,
            location.LocationCode,
            location.Address.OneLine,
            location.Address.City,
            location.Address.Region,
            location.IsActive);
}
