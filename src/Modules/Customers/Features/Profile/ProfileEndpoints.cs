using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Profile;

/// <summary>Body of a request to save a customer's profile.</summary>
/// <param name="BillDeliveryChannel">How the bill reaches them.</param>
/// <param name="OutageNotices">Whether they want outage notices.</param>
/// <param name="DunningNotices">Whether they want reminders before collections.</param>
/// <param name="PreferredLanguage">What language to write to them in.</param>
/// <param name="MailingAddress">
/// Where post goes. <see langword="null"/> clears the override, which sends post back to the
/// service address — it does not leave the customer without one.
/// </param>
public sealed record UpdateCustomerProfileRequest(
    BillDeliveryChannel BillDeliveryChannel,
    bool OutageNotices,
    bool DunningNotices,
    CommunicationLanguage PreferredLanguage,
    AddressPayload? MailingAddress = null);

/// <summary>A customer's profile as the API returns it.</summary>
/// <param name="CustomerId">Whose profile.</param>
/// <param name="MailingAddress">Where post actually goes — the override, or the service address it falls back to.</param>
/// <param name="FormattedMailingAddress">That address on one line, for a card.</param>
/// <param name="Source">Which of the two it came from: <c>Override</c>, <c>ServiceAddress</c> or <c>None</c>.</param>
/// <param name="ServiceAddress">The premise post falls back to, so a screen can show what clearing the override would do.</param>
/// <param name="ServiceLocationId">Which premise that is.</param>
/// <param name="BillDeliveryChannel">How the bill reaches them.</param>
/// <param name="OutageNotices">Whether they want outage notices.</param>
/// <param name="DunningNotices">Whether they want reminders before collections.</param>
/// <param name="PreferredLanguage">What language to write to them in.</param>
/// <param name="UpdatedAt">When it was last saved, or <see langword="null"/> while these are still the defaults.</param>
public sealed record CustomerProfileResponse(
    Guid CustomerId,
    AddressPayload? MailingAddress,
    string? FormattedMailingAddress,
    string Source,
    AddressPayload? ServiceAddress,
    Guid? ServiceLocationId,
    string BillDeliveryChannel,
    bool OutageNotices,
    bool DunningNotices,
    string PreferredLanguage,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>Projects a <see cref="CustomerProfileView"/> for the wire.</summary>
    public static CustomerProfileResponse From(CustomerProfileView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new CustomerProfileResponse(
            view.CustomerId,
            view.MailingAddress is null ? null : AddressPayload.From(view.MailingAddress),
            view.MailingAddress?.OneLine,
            view.Source.ToString(),
            view.ServiceAddress is null ? null : AddressPayload.From(view.ServiceAddress),
            view.ServiceLocationId,
            view.BillDeliveryChannel.ToString(),
            view.OutageNotices,
            view.DunningNotices,
            view.PreferredLanguage.ToString(),
            view.UpdatedAt);
    }
}

/// <summary>The customer profile's HTTP surface.</summary>
public static class ProfileEndpoints
{
    /// <summary>Route of a customer's profile. A sub-resource of the customer, because that is what it is.</summary>
    public const string RoutePrefix = "/api/customers/{customerId:guid}/profile";

    /// <summary>Maps the profile endpoints.</summary>
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Customers");

        group
            .MapGet("/", ([FromRoute] Guid customerId, [FromServices] ICustomerProfileService profiles, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(CustomerProfileResponse.From(await profiles.GetAsync(customerId, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetCustomerProfile");

        // PUT rather than PATCH: the body is the whole profile, and a partial save is how a cleared
        // mailing address and an omitted one become impossible to tell apart — which is exactly the
        // distinction this resource exists to carry.
        group
            .MapPut("/", ([FromRoute] Guid customerId, UpdateCustomerProfileRequest body, [FromServices] ICustomerProfileService profiles, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(CustomerProfileResponse.From(await profiles.UpdateAsync(
                        customerId,
                        new UpdateCustomerProfileInput(
                            body.MailingAddress?.ToAddress(),
                            body.BillDeliveryChannel,
                            body.OutageNotices,
                            body.DunningNotices,
                            body.PreferredLanguage),
                        cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<UpdateCustomerProfileRequest>()
            .WithName("UpdateCustomerProfile");

        return endpoints;
    }
}
