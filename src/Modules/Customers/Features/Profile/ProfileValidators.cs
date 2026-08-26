using FluentValidation;
using GridCore.Modules.Customers.Features.ServiceLocations;

namespace GridCore.Modules.Customers.Features.Profile;

/// <summary>
/// Rules for saving a customer's profile.
/// </summary>
/// <remarks>
/// An <b>absent</b> mailing address is valid and means "post follows the service address"; a
/// <b>present but incomplete</b> one is not, and gets the same address rules a premise does. Whether
/// the chosen bill channel can actually be used is not here — that depends on whether the customer
/// has an email on file, which the request cannot see and <c>CustomerProfileService</c> can.
/// </remarks>
public sealed class UpdateCustomerProfileRequestValidator : AbstractValidator<UpdateCustomerProfileRequest>
{
    /// <summary>Builds the rules.</summary>
    public UpdateCustomerProfileRequestValidator()
    {
        RuleFor(request => request.BillDeliveryChannel).IsInEnum();
        RuleFor(request => request.PreferredLanguage).IsInEnum();

        RuleFor(request => request.MailingAddress!)
            .SetValidator(new AddressPayloadValidator())
            .When(request => request.MailingAddress is not null);
    }
}
