using FluentValidation;

namespace GridCore.Modules.Customers.Features.ServiceLocations;

/// <summary>Rules for the address on a service location request.</summary>
public sealed class AddressPayloadValidator : AbstractValidator<AddressPayload>
{
    /// <summary>Builds the rules.</summary>
    public AddressPayloadValidator()
    {
        RuleFor(address => address.Line1).NotEmpty().MaximumLength(Address.LineLength);
        RuleFor(address => address.Line2!).MaximumLength(Address.LineLength);
        RuleFor(address => address.City).NotEmpty().MaximumLength(Address.PlaceLength);
        RuleFor(address => address.Region).NotEmpty().MaximumLength(Address.PlaceLength);
        RuleFor(address => address.PostalCode!).MaximumLength(Address.PostalCodeLength);
        RuleFor(address => address.Country).NotEmpty().MaximumLength(Address.CountryLength);
    }
}

/// <summary>Rules for registering or correcting a service location.</summary>
public sealed class ServiceLocationRequestValidator : AbstractValidator<ServiceLocationRequest>
{
    /// <summary>Builds the rules.</summary>
    public ServiceLocationRequestValidator()
    {
        RuleFor(request => request.Address).NotNull().SetValidator(new AddressPayloadValidator());
        RuleFor(request => request.Description!).MaximumLength(ServiceLocation.DescriptionLength);
        RuleFor(request => request.StatusReason!).MaximumLength(ServiceLocation.ReasonLength);
    }
}
