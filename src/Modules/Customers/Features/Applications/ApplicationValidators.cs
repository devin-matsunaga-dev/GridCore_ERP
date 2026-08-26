using FluentValidation;
using GridCore.Contracts.Services;

namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>
/// Rules for filing an application.
/// </summary>
/// <remarks>
/// Field shape only. Whether the customer may take on new service, whether the premise is
/// deactivated, whether the supply is already taken there and whether an application for it is
/// already open all depend on state a validator cannot see; they are 409s from the service.
/// </remarks>
public sealed class SubmitApplicationRequestValidator : AbstractValidator<SubmitApplicationRequest>
{
    /// <summary>Builds the rules.</summary>
    public SubmitApplicationRequestValidator()
    {
        RuleFor(request => request.CustomerId).NotEmpty();
        RuleFor(request => request.ServiceLocationId).NotEmpty();

        // NotEqual(default) as well as IsInEnum, which is the whole point of making the field
        // required: ServiceType numbers from 1, so a body that simply omits it binds to 0 — and
        // IsInEnum alone would already catch that. Both are stated so the message names the field
        // rather than describing the value.
        RuleFor(request => request.ServiceType)
            .NotEqual(default(ServiceType))
            .WithMessage("An application must say which supply is being applied for.")
            .IsInEnum()
            .WithMessage("Not a service GridCore declares.");

        RuleFor(request => request.Notes!).MaximumLength(ServiceApplication.NotesLength);
    }
}

/// <summary>Rules for deciding an application — the same three fields whichever way it goes.</summary>
/// <remarks>
/// Which codes fit which decision, and which of them must say more, is
/// <see cref="ApplicationReasons"/>' business and is enforced in the aggregate: one endpoint body
/// serves approve, reject and withdraw, so a validator here could only check the code against a
/// decision it cannot see from the body.
/// </remarks>
public sealed class DecideApplicationRequestValidator : AbstractValidator<DecideApplicationRequest>
{
    /// <summary>Builds the rules.</summary>
    public DecideApplicationRequestValidator()
    {
        RuleFor(request => request.ReasonCode).IsInEnum().WithMessage("Not a reason code GridCore declares.");
        RuleFor(request => request.Notes!).MaximumLength(ServiceApplication.NotesLength);
    }
}

/// <summary>Rules for a resubmission body.</summary>
public sealed class ResubmitApplicationRequestValidator : AbstractValidator<ResubmitApplicationRequest>
{
    /// <summary>Builds the rules.</summary>
    public ResubmitApplicationRequestValidator() =>
        RuleFor(request => request.Notes!).MaximumLength(ServiceApplication.NotesLength);
}
