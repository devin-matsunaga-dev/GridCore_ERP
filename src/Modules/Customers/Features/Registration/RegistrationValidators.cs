using FluentValidation;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.Features.Registration;

/// <summary>Rules for the premise half of an intake.</summary>
/// <remarks>
/// "Exactly one of the two" is asserted here rather than only in the aggregate, because it is a
/// property of the body alone — a form that named neither is a 400, and finding that out after the
/// transaction has opened would be answering a filled-in form with a workflow conflict.
/// </remarks>
public sealed class IntakePremiseRequestValidator : AbstractValidator<IntakePremiseRequest>
{
    /// <summary>Builds the rules.</summary>
    public IntakePremiseRequestValidator()
    {
        RuleFor(premise => premise)
            .Must(premise => NamesNew(premise) != NamesExisting(premise))
            .WithMessage("An intake names either a new premise or an existing one, not both.");

        RuleFor(premise => premise.NewPremise!)
            .SetValidator(new NewPremiseRequestValidator())
            .When(NamesNew);
    }

    private static bool NamesNew(IntakePremiseRequest premise) => premise.NewPremise is not null;

    private static bool NamesExisting(IntakePremiseRequest premise) =>
        premise.ServiceLocationId is { } id && id != Guid.Empty;
}

/// <summary>Rules for a premise registered as part of an intake.</summary>
public sealed class NewPremiseRequestValidator : AbstractValidator<NewPremiseRequest>
{
    /// <summary>Builds the rules.</summary>
    public NewPremiseRequestValidator()
    {
        RuleFor(premise => premise.Address).NotNull().SetValidator(new AddressPayloadValidator());
        RuleFor(premise => premise.Description!).MaximumLength(ServiceLocation.DescriptionLength);
    }
}

/// <summary>
/// Rules for a customer intake.
/// </summary>
/// <remarks>
/// Field shape only. Whether the caller may collect a deposit, whether the assessed figure covers
/// what was taken, whether the premise is deactivated or already served — all of those depend on
/// state or on who is calling, which a validator cannot see. They are 403s and 409s from the
/// service.
/// </remarks>
public sealed class RegisterCustomerIntakeRequestValidator : AbstractValidator<RegisterCustomerIntakeRequest>
{
    /// <summary>Builds the rules.</summary>
    public RegisterCustomerIntakeRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(Customer.NameLength);
        RuleFor(request => request.Class).IsInEnum();
        RuleFor(request => request.ServiceType).IsInEnum().WithMessage("Not a service GridCore declares.");
        RuleFor(request => request.ContactName!).MaximumLength(Customer.NameLength);
        RuleFor(request => request.Phone!).MaximumLength(Customer.PhoneLength);
        RuleFor(request => request.Reason!).MaximumLength(ServiceAccount.ReasonLength);

        RuleFor(request => request.Email!)
            .MaximumLength(Customer.EmailLength)
            .EmailAddress()
            .When(request => !string.IsNullOrWhiteSpace(request.Email));

        RuleFor(request => request.Premise).NotNull().SetValidator(new IntakePremiseRequestValidator());

        RuleFor(request => request.DepositCollected)
            .GreaterThanOrEqualTo(Money.Zero)
            .WithMessage("A deposit collected cannot be negative.")
            .Must(Money.IsRounded)
            .WithMessage("A deposit must be a whole number of cents.");
    }
}
