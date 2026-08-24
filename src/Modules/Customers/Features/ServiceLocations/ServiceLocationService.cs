using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.ServiceLocations;

/// <summary>What a caller supplies to register or correct a premise.</summary>
/// <param name="Address">Where it is.</param>
/// <param name="Description">What it is, in a crew's words.</param>
/// <param name="IsActive">Whether service may be delivered there.</param>
/// <param name="StatusReason">Why it was deactivated or reactivated, when the flag moves.</param>
public sealed record ServiceLocationInput(
    Address Address,
    string? Description = null,
    bool IsActive = true,
    string? StatusReason = null);

/// <summary>How the location list is filtered.</summary>
/// <param name="Search">Matched against the code, the street line and the town, case-insensitively.</param>
/// <param name="Region">Only premises in this state, province or island.</param>
/// <param name="IsActive">Only premises with this flag.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record ServiceLocationQuery(
    string? Search = null,
    string? Region = null,
    bool? IsActive = null,
    int Limit = 50);

/// <summary>The service location registry. The module's own surface; endpoints are a thin layer over it.</summary>
public interface IServiceLocationService
{
    /// <summary>Registers a premise, issuing it the next location code.</summary>
    Task<ServiceLocation> RegisterAsync(ServiceLocationInput input, CancellationToken cancellationToken = default);

    /// <summary>Corrects a premise's address, description or active flag.</summary>
    Task<ServiceLocation> UpdateAsync(Guid id, ServiceLocationInput input, CancellationToken cancellationToken = default);

    /// <summary>One premise, or <see langword="null"/> if there is no such id.</summary>
    Task<ServiceLocation?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The location list, newest first.</summary>
    Task<IReadOnlyList<ServiceLocation>> ListAsync(ServiceLocationQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The service location registry over the customers schema. Writes run inside
/// <see cref="IUnitOfWork.ExecuteAsync"/> and never save themselves — see
/// <see cref="Customers.CustomerService"/> for why.
/// </summary>
public sealed class ServiceLocationService(
    CustomersDbContext database,
    IRegistryNumberGenerator numbers,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    TimeProvider clock) : IServiceLocationService
{
    /// <summary>The largest page <see cref="ListAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public Task<ServiceLocation> RegisterAsync(ServiceLocationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var code = await numbers.NextServiceLocationCodeAsync(ct).ConfigureAwait(false);

                // The unique index is the real guarantee; this turns the loser of a race into a 409.
                if (await database.ServiceLocations.AnyAsync(existing => existing.LocationCode == code, ct).ConfigureAwait(false))
                {
                    throw new RegistryWorkflowException(
                        $"Location code {code} has just been taken by another registration. Try again.");
                }

                var location = ServiceLocation.Register(code, input.Address, now, input.Description, input.IsActive);

                database.ServiceLocations.Add(location);

                audit.Record(
                    AuditActions.ServiceLocationCreated,
                    AuditEntityTypes.ServiceLocation,
                    location.Id.ToString(),
                    before: null,
                    after: ServiceLocationSnapshot.Of(location));

                await events.PublishAsync(
                    ServiceLocationRegistered.For(
                        now,
                        location.Id,
                        location.LocationCode,
                        location.Address.OneLine,
                        location.Address.City,
                        location.Address.Region),
                    ct).ConfigureAwait(false);

                return location;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ServiceLocation> UpdateAsync(Guid id, ServiceLocationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var location = await database.ServiceLocations.FirstOrDefaultAsync(candidate => candidate.Id == id, ct).ConfigureAwait(false)
                    ?? throw new ServiceLocationNotFoundException(id);

                var before = ServiceLocationSnapshot.Of(location);

                location.UpdateDetails(input.Address, input.Description, input.IsActive, input.StatusReason);

                audit.Record(
                    AuditActions.ServiceLocationUpdated,
                    AuditEntityTypes.ServiceLocation,
                    location.Id.ToString(),
                    before,
                    ServiceLocationSnapshot.Of(location));

                return location;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ServiceLocation?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.ServiceLocations.FirstOrDefaultAsync(location => location.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServiceLocation>> ListAsync(ServiceLocationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var locations = database.ServiceLocations.AsNoTracking();

        if (query.IsActive is { } isActive)
        {
            locations = locations.Where(location => location.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Region))
        {
            var region = query.Region.Trim().ToLowerInvariant();

            locations = locations.Where(location => location.Address.Region.ToLower() == region);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();

            locations = locations.Where(location =>
                location.LocationCode.ToLower().Contains(term)
                || location.Address.Line1.ToLower().Contains(term)
                || location.Address.City.ToLower().Contains(term));
        }

        return await locations
            .OrderByDescending(location => location.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The before/after shape a service location is audited as. A dedicated record rather than the
/// entity, for the reason <see cref="Customers.CustomerSnapshot"/> gives.
/// </summary>
/// <param name="Id">Which premise.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="Address">Where it is, on one line.</param>
/// <param name="City">Town or village.</param>
/// <param name="Region">State, province or island.</param>
/// <param name="Description">What it is.</param>
/// <param name="IsActive">Whether service may be delivered there.</param>
public sealed record ServiceLocationSnapshot(
    Guid Id,
    string LocationCode,
    string Address,
    string City,
    string Region,
    string? Description,
    bool IsActive)
{
    /// <summary>Takes a snapshot of <paramref name="location"/> as it stands.</summary>
    public static ServiceLocationSnapshot Of(ServiceLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        return new ServiceLocationSnapshot(
            location.Id,
            location.LocationCode,
            location.Address.OneLine,
            location.Address.City,
            location.Address.Region,
            location.Description,
            location.IsActive);
    }
}
