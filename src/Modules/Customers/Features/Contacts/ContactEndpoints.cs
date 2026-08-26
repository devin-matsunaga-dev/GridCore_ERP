using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Contacts;

/// <summary>Body of a request to record a way of reaching a contact.</summary>
/// <param name="Kind">Phone, mobile or email.</param>
/// <param name="Value">The number or address.</param>
/// <param name="IsPrimary">Whether it takes the primary place for its kind.</param>
public sealed record ContactMethodRequest(ContactMethodKind Kind, string Value, bool IsPrimary = false);

/// <summary>Body of a request to add a contact to a customer.</summary>
/// <param name="Name">Who they are.</param>
/// <param name="Relationship">What they are to the customer.</param>
/// <param name="IsAuthorisedToDiscuss">Whether a rep may discuss the account with them.</param>
/// <param name="Methods">How to reach them, if any are known already.</param>
public sealed record CreateContactRequest(
    string Name,
    string? Relationship = null,
    bool IsAuthorisedToDiscuss = false,
    IReadOnlyList<ContactMethodRequest>? Methods = null);

/// <summary>Body of a request to correct a contact.</summary>
/// <param name="Name">Who they are.</param>
/// <param name="Relationship">What they are to the customer.</param>
/// <param name="IsAuthorisedToDiscuss">Whether a rep may discuss the account with them.</param>
public sealed record UpdateContactRequest(string Name, string? Relationship = null, bool IsAuthorisedToDiscuss = false);

/// <summary>Body of a request to correct a method's number or address.</summary>
/// <param name="Value">The corrected number or address.</param>
public sealed record UpdateContactMethodRequest(string Value);

/// <summary>A contact method as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Kind">Phone, mobile or email.</param>
/// <param name="Value">The number or address, as stored.</param>
/// <param name="IsPrimary">Whether it is the one to try first for its kind.</param>
/// <param name="RecordedAt">When it was recorded.</param>
public sealed record ContactMethodResponse(Guid Id, string Kind, string Value, bool IsPrimary, DateTimeOffset RecordedAt)
{
    /// <summary>Projects a <see cref="ContactMethod"/> for the wire.</summary>
    public static ContactMethodResponse From(ContactMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);

        return new ContactMethodResponse(method.Id, method.Kind.ToString(), method.Value, method.IsPrimary, method.RecordedAt);
    }
}

/// <summary>A contact as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="CustomerId">Whose account they are a contact on.</param>
/// <param name="Name">Who they are.</param>
/// <param name="Relationship">What they are to the customer.</param>
/// <param name="IsAuthorisedToDiscuss">Whether the account may be discussed with them.</param>
/// <param name="Methods">How to reach them.</param>
/// <param name="RecordedAt">When the contact was added.</param>
public sealed record ContactResponse(
    Guid Id,
    Guid CustomerId,
    string Name,
    string? Relationship,
    bool IsAuthorisedToDiscuss,
    IReadOnlyList<ContactMethodResponse> Methods,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a <see cref="CustomerContact"/> for the wire.</summary>
    public static ContactResponse From(CustomerContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        return new ContactResponse(
            contact.Id,
            contact.CustomerId,
            contact.Name,
            contact.Relationship,
            contact.IsAuthorisedToDiscuss,
            [.. contact.Methods.Select(ContactMethodResponse.From)],
            contact.RecordedAt);
    }
}

/// <summary>
/// The contact register's HTTP surface.
/// </summary>
/// <remarks>
/// Two prefixes, because there are two kinds of route here: a customer's contacts hang off the
/// customer, and one contact — already identified — is addressed on its own. The alternative,
/// <c>/api/customers/{customerId}/contacts/{contactId}/methods/{methodId}</c>, makes a client quote
/// an id it already holds twice over and makes a mismatch between the two a case somebody has to
/// handle.
/// </remarks>
public static class ContactEndpoints
{
    /// <summary>Route of a customer's contacts.</summary>
    public const string CustomerContactsRoute = "/api/customers/{customerId:guid}/contacts";

    /// <summary>Route prefix of one contact.</summary>
    public const string RoutePrefix = "/api/customer-contacts";

    /// <summary>Maps the contact endpoints.</summary>
    public static IEndpointRouteBuilder MapContactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var customerScoped = endpoints.MapGroup(CustomerContactsRoute).WithTags("Customers");

        customerScoped
            .MapGet("/", ([FromRoute] Guid customerId, [FromServices] ICustomerContactService contacts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok((await contacts.ListAsync(customerId, cancellationToken)).Select(ContactResponse.From).ToList())))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListCustomerContacts");

        customerScoped
            .MapPost("/", ([FromRoute] Guid customerId, CreateContactRequest body, [FromServices] ICustomerContactService contacts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var contact = await contacts.AddAsync(
                        customerId,
                        new AddCustomerContactInput(
                            body.Name,
                            body.Relationship,
                            body.IsAuthorisedToDiscuss,
                            [.. (body.Methods ?? []).Select(method => new AddContactMethodInput(method.Kind, method.Value, method.IsPrimary))]),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{contact.Id}", ContactResponse.From(contact));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<CreateContactRequest>()
            .WithName("AddCustomerContact");

        var contactScoped = endpoints.MapGroup(RoutePrefix).WithTags("Customers");

        contactScoped
            .MapGet("/{contactId:guid}", async ([FromRoute] Guid contactId, [FromServices] ICustomerContactService contacts, CancellationToken cancellationToken) =>
            {
                var contact = await contacts.FindAsync(contactId, cancellationToken);

                return contact is null
                    ? RegistryProblems.CustomerContactNotFound(contactId)
                    : Results.Ok(ContactResponse.From(contact));
            })
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetCustomerContact");

        contactScoped
            .MapPut("/{contactId:guid}", ([FromRoute] Guid contactId, UpdateContactRequest body, [FromServices] ICustomerContactService contacts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ContactResponse.From(await contacts.UpdateAsync(
                        contactId,
                        new UpdateCustomerContactInput(body.Name, body.Relationship, body.IsAuthorisedToDiscuss),
                        cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<UpdateContactRequest>()
            .WithName("UpdateCustomerContact");

        contactScoped
            .MapDelete("/{contactId:guid}", ([FromRoute] Guid contactId, [FromServices] ICustomerContactService contacts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    await contacts.RemoveAsync(contactId, cancellationToken);

                    return Results.NoContent();
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithName("RemoveCustomerContact");

        contactScoped
            .MapPost("/{contactId:guid}/methods", ([FromRoute] Guid contactId, ContactMethodRequest body, [FromServices] ICustomerContactService contacts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ContactResponse.From(await contacts.AddMethodAsync(
                        contactId,
                        new AddContactMethodInput(body.Kind, body.Value, body.IsPrimary),
                        cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<ContactMethodRequest>()
            .WithName("AddContactMethod");

        contactScoped
            .MapPut("/{contactId:guid}/methods/{methodId:guid}", ([FromRoute] Guid contactId, [FromRoute] Guid methodId, UpdateContactMethodRequest body, [FromServices] ICustomerContactService contacts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ContactResponse.From(await contacts.CorrectMethodAsync(contactId, methodId, body.Value, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<UpdateContactMethodRequest>()
            .WithName("CorrectContactMethod");

        // Promotion is a transition, not a field edit — a POST sub-resource per CONVENTIONS.md. It
        // is also the one write here that changes a row the caller did not name: the method that
        // held the primary place is demoted in the same act.
        contactScoped
            .MapPost("/{contactId:guid}/methods/{methodId:guid}/primary", ([FromRoute] Guid contactId, [FromRoute] Guid methodId, [FromServices] ICustomerContactService contacts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ContactResponse.From(await contacts.MakeMethodPrimaryAsync(contactId, methodId, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithName("MakeContactMethodPrimary");

        contactScoped
            .MapDelete("/{contactId:guid}/methods/{methodId:guid}", ([FromRoute] Guid contactId, [FromRoute] Guid methodId, [FromServices] ICustomerContactService contacts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ContactResponse.From(await contacts.RemoveMethodAsync(contactId, methodId, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithName("RemoveContactMethod");

        return endpoints;
    }
}
