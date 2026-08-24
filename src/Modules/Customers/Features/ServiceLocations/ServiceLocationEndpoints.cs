using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.ServiceLocations;

/// <summary>An address as the API accepts and returns it.</summary>
/// <param name="Line1">Street address, or how a crew would find it.</param>
/// <param name="City">Town or village.</param>
/// <param name="Region">State, province or island.</param>
/// <param name="Country">Country name or code.</param>
/// <param name="Line2">Unit, floor or building.</param>
/// <param name="PostalCode">Postal code, where there is one.</param>
public sealed record AddressPayload(
    string Line1,
    string City,
    string Region,
    string Country,
    string? Line2 = null,
    string? PostalCode = null)
{
    /// <summary>Builds the value object this payload describes.</summary>
    /// <exception cref="RegistryValidationException">A required part is missing.</exception>
    public Address ToAddress() => Address.Create(Line1, City, Region, Country, Line2, PostalCode);

    /// <summary>Projects an <see cref="Address"/> for the wire.</summary>
    public static AddressPayload From(Address address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return new AddressPayload(address.Line1, address.City, address.Region, address.Country, address.Line2, address.PostalCode);
    }
}

/// <summary>Body of a request to register or correct a service location.</summary>
/// <param name="Address">Where the premise is.</param>
/// <param name="Description">What it is, in a crew's words.</param>
/// <param name="IsActive">Whether service may be delivered there.</param>
/// <param name="StatusReason">Why it was deactivated or reactivated, when the flag moves.</param>
public sealed record ServiceLocationRequest(
    AddressPayload Address,
    string? Description = null,
    bool IsActive = true,
    string? StatusReason = null);

/// <summary>A service location as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="LocationCode">The code quoted on a work order.</param>
/// <param name="Address">Where it is.</param>
/// <param name="FormattedAddress">The address on one line, for a list.</param>
/// <param name="Description">What it is.</param>
/// <param name="IsActive">Whether service may be delivered there.</param>
/// <param name="StatusReason">Why it was last deactivated or reactivated.</param>
/// <param name="RegisteredAt">When it was registered.</param>
public sealed record ServiceLocationResponse(
    Guid Id,
    string LocationCode,
    AddressPayload Address,
    string FormattedAddress,
    string? Description,
    bool IsActive,
    string? StatusReason,
    DateTimeOffset RegisteredAt)
{
    /// <summary>Projects a <see cref="ServiceLocation"/> for the wire.</summary>
    public static ServiceLocationResponse From(ServiceLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        return new ServiceLocationResponse(
            location.Id,
            location.LocationCode,
            AddressPayload.From(location.Address),
            location.Address.OneLine,
            location.Description,
            location.IsActive,
            location.StatusReason,
            location.RegisteredAt);
    }
}

/// <summary>The service location registry's HTTP surface.</summary>
public static class ServiceLocationEndpoints
{
    /// <summary>Route prefix of the service location registry.</summary>
    public const string RoutePrefix = "/api/service-locations";

    /// <summary>Maps the service location endpoints.</summary>
    public static IEndpointRouteBuilder MapServiceLocationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Service locations");

        group
            .MapGet("/", async (
                    string? search,
                    string? region,
                    bool? isActive,
                    int? limit,
                    [FromServices] IServiceLocationService locations,
                    CancellationToken cancellationToken) =>
                Results.Ok((await locations.ListAsync(new ServiceLocationQuery(search, region, isActive, limit ?? 50), cancellationToken))
                    .Select(ServiceLocationResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListServiceLocations");

        group
            .MapGet("/{id:guid}", async ([FromRoute] Guid id, [FromServices] IServiceLocationService locations, CancellationToken cancellationToken) =>
            {
                var location = await locations.FindAsync(id, cancellationToken);

                return location is null
                    ? RegistryProblems.ServiceLocationNotFound(id)
                    : Results.Ok(ServiceLocationResponse.From(location));
            })
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetServiceLocation");

        group
            .MapPost("/", (ServiceLocationRequest body, [FromServices] IServiceLocationService locations, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var location = await locations.RegisterAsync(
                        new ServiceLocationInput(body.Address.ToAddress(), body.Description, body.IsActive, body.StatusReason),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{location.Id}", ServiceLocationResponse.From(location));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<ServiceLocationRequest>()
            .WithName("CreateServiceLocation");

        // No DELETE, deliberately. A premise is deactivated through this endpoint's IsActive flag:
        // its meters, work orders and bills are history that later reports read, and a deleted row
        // would take their context with it.
        group
            .MapPut("/{id:guid}", ([FromRoute] Guid id, ServiceLocationRequest body, [FromServices] IServiceLocationService locations, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ServiceLocationResponse.From(await locations.UpdateAsync(
                        id,
                        new ServiceLocationInput(body.Address.ToAddress(), body.Description, body.IsActive, body.StatusReason),
                        cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<ServiceLocationRequest>()
            .WithName("UpdateServiceLocation");

        return endpoints;
    }
}
