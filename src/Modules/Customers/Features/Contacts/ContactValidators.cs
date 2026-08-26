using FluentValidation;

namespace GridCore.Modules.Customers.Features.Contacts;

/// <summary>
/// Rules for a contact method arriving on the wire.
/// </summary>
/// <remarks>
/// The <b>format</b> rules live here and the structural ones live in the aggregate, which is the
/// split <c>CustomerDetailsValidator</c> already makes: the edge decides whether an email looks like
/// an email, and <see cref="ContactMethod"/> decides whether a value is present and short enough —
/// because a seeder calling the service directly must not be able to write what a request could not.
/// </remarks>
public sealed class ContactMethodRequestValidator : AbstractValidator<ContactMethodRequest>
{
    /// <summary>Builds the rules.</summary>
    public ContactMethodRequestValidator()
    {
        RuleFor(request => request.Kind).IsInEnum();

        RuleFor(request => request.Value)
            .NotEmpty()
            .Must((request, value) => value is null || value.Trim().Length <= ContactMethod.MaxLengthFor(request.Kind))
            .WithMessage(request => $"A {request.Kind} contact method may be at most {ContactMethod.MaxLengthFor(request.Kind)} characters.");

        RuleFor(request => request.Value)
            .EmailAddress()
            .When(request => request.Kind is ContactMethodKind.Email && !string.IsNullOrWhiteSpace(request.Value));
    }
}

/// <summary>Rules for adding a contact to a customer.</summary>
public sealed class CreateContactRequestValidator : AbstractValidator<CreateContactRequest>
{
    /// <summary>Builds the rules.</summary>
    public CreateContactRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(CustomerContact.NameLength);
        RuleFor(request => request.Relationship!).MaximumLength(CustomerContact.RelationshipLength);

        // Each method on an intake gets the same rules it would get arriving on its own — the whole
        // point of the shared validator, since a contact may be created with its numbers already.
        RuleForEach(request => request.Methods!)
            .SetValidator(new ContactMethodRequestValidator())
            .When(request => request.Methods is not null);
    }
}

/// <summary>Rules for correcting a contact.</summary>
/// <remarks>
/// Nothing here about <see cref="UpdateContactRequest.IsAuthorisedToDiscuss"/>: whether the caller
/// may move it is a permission question the service answers with a 403, and a validator that
/// refused it would report a 400 for what is plainly not a malformed request.
/// </remarks>
public sealed class UpdateContactRequestValidator : AbstractValidator<UpdateContactRequest>
{
    /// <summary>Builds the rules.</summary>
    public UpdateContactRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(CustomerContact.NameLength);
        RuleFor(request => request.Relationship!).MaximumLength(CustomerContact.RelationshipLength);
    }
}

/// <summary>Rules for correcting a method's number or address.</summary>
/// <remarks>
/// The kind is not in the body — a method's kind is fixed at the moment it is recorded, because
/// turning a mobile into an email address is not a correction of that method, it is a different one.
/// So this can only check what every kind shares; the per-kind width is the aggregate's.
/// </remarks>
public sealed class UpdateContactMethodRequestValidator : AbstractValidator<UpdateContactMethodRequest>
{
    /// <summary>Builds the rules.</summary>
    public UpdateContactMethodRequestValidator()
    {
        RuleFor(request => request.Value).NotEmpty().MaximumLength(ContactMethod.ValueLength);
    }
}
