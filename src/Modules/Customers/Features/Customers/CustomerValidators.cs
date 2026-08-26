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
        RuleFor(request => request.ContactName!).MaximumLength(Customer.NameLength);
        RuleFor(request => request.Phone!).MaximumLength(Customer.PhoneLength);

        RuleFor(request => request.Email!)
            .MaximumLength(Customer.EmailLength)
            .EmailAddress()
            .When(request => !string.IsNullOrWhiteSpace(request.Email));

        // No deposit rule: since WP-2.12 neither body carries one. The deposit's own validators live
        // beside the lifecycle that moves it. No class rule either, since WP-2.15: only a
        // registration states one, so the rule sits on the one body that still carries it.
    }
}

/// <summary>Rules for registering a customer.</summary>
public sealed class CreateCustomerRequestValidator : CustomerDetailsValidator<CreateCustomerRequest>
{
    /// <summary>Builds the rules.</summary>
    public CreateCustomerRequestValidator() => RuleFor(request => request.Class).IsInEnum();
}

/// <summary>Rules for correcting a customer's details.</summary>
/// <remarks>
/// No class rule, because there is no class field: since WP-2.15 it moves through the transition
/// register with a reason code and an effective date. The status rules moved there with it —
/// <c>ChangeCustomerStatusRequestValidator</c> now lives beside the route that uses it.
/// </remarks>
public sealed class UpdateCustomerRequestValidator : CustomerDetailsValidator<UpdateCustomerRequest>;
