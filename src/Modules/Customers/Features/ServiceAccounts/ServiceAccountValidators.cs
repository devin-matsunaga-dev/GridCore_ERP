using FluentValidation;

namespace GridCore.Modules.Customers.Features.ServiceAccounts;

/// <summary>
/// Rules for opening a service account.
/// </summary>
/// <remarks>
/// Only that the ids are present and the reason fits. Whether the customer is allowed to take on
/// service, whether the premise is active and whether it is already served are all facts about the
/// registry's current state, which a validator cannot see — so those are 409s from the service.
/// </remarks>
public sealed class OpenServiceAccountRequestValidator : AbstractValidator<OpenServiceAccountRequest>
{
    /// <summary>Builds the rules.</summary>
    public OpenServiceAccountRequestValidator()
    {
        RuleFor(request => request.CustomerId).NotEmpty().WithMessage("A customer is required to open a service account.");
        RuleFor(request => request.ServiceLocationId).NotEmpty().WithMessage("A service location is required to open a service account.");

        // A 400 at the edge, so a body carrying a service nobody declares never reaches the
        // aggregate that would throw the same thing as a 400 anyway — one refusal, at the boundary.
        RuleFor(request => request.ServiceType).IsInEnum().WithMessage("Not a service GridCore declares.");
        RuleFor(request => request.Reason!).MaximumLength(ServiceAccount.ReasonLength);
    }
}

/// <summary>
/// Rules for starting, stopping or closing service. Only the reason's length: whether the move is
/// legal depends on where the account is now, which <see cref="ServiceAccountTransitions"/> knows
/// and this does not.
/// </summary>
public sealed class ServiceAccountTransitionRequestValidator : AbstractValidator<ServiceAccountTransitionRequest>
{
    /// <summary>Builds the rules.</summary>
    public ServiceAccountTransitionRequestValidator()
    {
        RuleFor(request => request.Reason!).MaximumLength(ServiceAccount.ReasonLength);
    }
}
