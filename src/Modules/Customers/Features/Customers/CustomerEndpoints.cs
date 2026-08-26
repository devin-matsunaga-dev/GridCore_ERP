using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Customers;

/// <summary>
/// The customer fields a request body carries. Create and update take the same set today and are
/// still separate DTOs — the day one grows a field the other must not — so the shared rules are
/// expressed as this interface rather than by one DTO standing in for the other.
/// </summary>
public interface ICustomerDetails
{
    /// <summary>Who they are.</summary>
    string Name { get; }

    // No Class since WP-2.15: a registration states one and a correction may not change it. The
    // shared rules are the fields both bodies genuinely have in common, and the class rule now sits
    // on CreateCustomerRequestValidator alone.

    /// <summary>Who to ask for.</summary>
    string? ContactName { get; }

    /// <summary>Where to email them.</summary>
    string? Email { get; }

    /// <summary>Where to call them.</summary>
    string? Phone { get; }
}

/// <summary>Body of a request to register a customer.</summary>
/// <param name="Name">Who they are.</param>
/// <param name="Class">Residential or commercial.</param>
/// <param name="ContactName">Who to ask for.</param>
/// <param name="Email">Where to email them.</param>
/// <param name="Phone">Where to call them.</param>
/// <remarks>
/// <b>No deposit field (WP-2.12).</b> Money is taken through
/// <c>POST /api/customers/{id}/deposits/collections</c>, which is gated on
/// <c>customers.deposit</c> and writes a ledger entry — a balance a registration form could set is
/// a balance that disagrees with the general ledger.
/// </remarks>
public sealed record CreateCustomerRequest(
    string Name,
    CustomerClass Class,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null) : ICustomerDetails;

/// <summary>Body of a request to correct a customer's details.</summary>
/// <param name="Name">Who they are.</param>
/// <param name="ContactName">Who to ask for.</param>
/// <param name="Email">Where to email them.</param>
/// <param name="Phone">Where to call them.</param>
/// <remarks>
/// <para><b>No deposit field (WP-2.12)</b>, for the reason <see cref="CreateCustomerRequest"/> gives.</para>
/// <para>
/// <b>No class field either, since WP-2.15</b> — the same removal for the same reason one step on.
/// A class decides the tariff, so it moves through
/// <c>POST /api/customers/{id}/transitions/class</c>, which demands a reason code from the fixed
/// list and the day the new class applies from. A correction form that could type over it would
/// change what somebody is billed and leave no record of why.
/// </para>
/// </remarks>
public sealed record UpdateCustomerRequest(
    string Name,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null) : ICustomerDetails;

/// <summary>A customer as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="Name">Who they are.</param>
/// <param name="ContactName">Who to ask for.</param>
/// <param name="Email">Where to email them.</param>
/// <param name="Phone">Where to call them.</param>
/// <param name="Class">Residential or commercial.</param>
/// <param name="Status">Where they stand.</param>
/// <param name="AllowedTransitions">Statuses they may still move to — what a UI renders as buttons.</param>
/// <param name="DepositHeld">Deposit the utility holds.</param>
/// <param name="RegisteredAt">When they were registered.</param>
/// <param name="StatusChangedAt">When the status last moved.</param>
/// <param name="StatusReason">Why it last moved.</param>
/// <param name="StatusEffectiveOn">The day the current status applies from; absent since registration.</param>
/// <param name="ClassChangedAt">When the class last moved; absent if it never has.</param>
/// <param name="ClassEffectiveOn">
/// The day the current class applies from (WP-2.15); absent for a customer still on the class they
/// were registered under. <b>Not the same as <paramref name="ClassChangedAt"/></b> — that is when a
/// rep typed it, this is when the utility says it happened, and it is what the billing pass prices
/// from.
/// </param>
public sealed record CustomerResponse(
    Guid Id,
    string AccountNumber,
    string Name,
    string? ContactName,
    string? Email,
    string? Phone,
    string Class,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    decimal DepositHeld,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? StatusChangedAt,
    string? StatusReason,
    DateOnly? StatusEffectiveOn,
    DateTimeOffset? ClassChangedAt,
    DateOnly? ClassEffectiveOn)
{
    /// <summary>Projects a <see cref="Customer"/> for the wire.</summary>
    public static CustomerResponse From(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return new CustomerResponse(
            customer.Id,
            customer.AccountNumber,
            customer.Name,
            customer.ContactName,
            customer.Email,
            customer.Phone,
            customer.Class.ToString(),
            customer.Status.ToString(),
            customer.AllowedTransitions.Select(status => status.ToString()).ToList(),
            customer.DepositHeld,
            customer.RegisteredAt,
            customer.StatusChangedAt,
            customer.StatusReason,
            customer.StatusEffectiveOn,
            customer.ClassChangedAt,
            customer.ClassEffectiveOn);
    }
}

/// <summary>The customer registry's HTTP surface.</summary>
public static class CustomerEndpoints
{
    /// <summary>Route prefix of the customer registry.</summary>
    public const string RoutePrefix = "/api/customers";

    /// <summary>Maps the customer endpoints.</summary>
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Customers");

        group
            .MapGet("/", async (
                    string? search,
                    CustomerStatus? status,
                    CustomerClass? @class,
                    int? limit,
                    [FromServices] ICustomerService customers,
                    CancellationToken cancellationToken) =>
                Results.Ok((await customers.ListAsync(new CustomerQuery(search, status, @class, limit ?? 50), cancellationToken))
                    .Select(CustomerResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListCustomers");

        group
            .MapGet("/{id:guid}", async ([FromRoute] Guid id, [FromServices] ICustomerService customers, CancellationToken cancellationToken) =>
            {
                var customer = await customers.FindAsync(id, cancellationToken);

                return customer is null ? RegistryProblems.CustomerNotFound(id) : Results.Ok(CustomerResponse.From(customer));
            })
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetCustomer");

        group
            .MapPost("/", (CreateCustomerRequest body, [FromServices] ICustomerService customers, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var customer = await customers.RegisterAsync(
                        new RegisterCustomerInput(body.Name, body.Class, body.ContactName, body.Email, body.Phone),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{customer.Id}", CustomerResponse.From(customer));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<CreateCustomerRequest>()
            .WithName("CreateCustomer");

        group
            .MapPut("/{id:guid}", ([FromRoute] Guid id, UpdateCustomerRequest body, [FromServices] ICustomerService customers, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var customer = await customers.UpdateAsync(
                        id,
                        new UpdateCustomerInput(body.Name, body.ContactName, body.Email, body.Phone),
                        cancellationToken);

                    return Results.Ok(CustomerResponse.From(customer));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<UpdateCustomerRequest>()
            .WithName("UpdateCustomer");

        // NO status route here since WP-2.15. It moved to POST /api/customers/{id}/transitions/status,
        // which demands a reason code from the fixed list, an effective date and the narrower
        // customers.transition permission. Leaving this one in place beside it would have been the
        // bypass that makes "every transition carries a reason code" untrue — the same call WP-2.12
        // made when it took the deposit out of the correction form rather than leaving both ways open.

        return endpoints;
    }
}
