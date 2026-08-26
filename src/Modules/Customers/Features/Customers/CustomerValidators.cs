using FluentValidation;

namespace GridCore.Modules.Customers.Features.Customers;

/// <summary>
/// The field rules shared by registering and correcting a customer, over
/// <see cref="ICustomerDetails"/> so neither DTO has to stand in for the other.
/// </summary>
/// <typeparam name="TRequest">The request body being validated.</typeparam>
public abstract class CustomerDetailsValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : ICustomerDetails
{
    /// <summary>Builds the rules.</summary>
    protected CustomerDetailsValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(Customer.NameLength);
        RuleFor(request => request.Class).IsInEnum();
        RuleFor(request => request.ContactName!).MaximumLength(Customer.NameLength);
        RuleFor(request => request.Phone!).MaximumLength(Customer.PhoneLength);

        RuleFor(request => request.Email!)
            .MaximumLength(Customer.EmailLength)
            .EmailAddress()
            .When(request => !string.IsNullOrWhiteSpace(request.Email));

        // No deposit rule: since WP-2.12 neither body carries one. The deposit's own validators live
        // beside the lifecycle that moves it.
    }
}

/// <summary>Rules for registering a customer.</summary>
public sealed class CreateCustomerRequestValidator : CustomerDetailsValidator<CreateCustomerRequest>;

/// <summary>Rules for correcting a customer's details.</summary>
public sealed class UpdateCustomerRequestValidator : CustomerDetailsValidator<UpdateCustomerRequest>;

/// <summary>Rules for moving a customer to another status.</summary>
/// <remarks>
/// Only that the status is one GridCore declares. Whether the move is <i>legal</i> depends on where
/// the customer is now, which the validator cannot see and <see cref="CustomerTransitions"/> can —
/// so that answer is a 409 from the aggregate, not a 400 from here.
/// </remarks>
public sealed class ChangeCustomerStatusRequestValidator : AbstractValidator<ChangeCustomerStatusRequest>
{
    /// <summary>Builds the rules.</summary>
    public ChangeCustomerStatusRequestValidator()
    {
        RuleFor(request => request.Status).IsInEnum();
        RuleFor(request => request.Reason!).MaximumLength(Customer.ReasonLength);
    }
}
