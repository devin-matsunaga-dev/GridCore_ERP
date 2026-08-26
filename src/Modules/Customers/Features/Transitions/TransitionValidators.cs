using FluentValidation;

namespace GridCore.Modules.Customers.Features.Transitions;

/// <summary>
/// The field rules every transition request shares, over <see cref="ITransitionRequest"/> so no
/// body has to stand in for another — the shape <c>CustomerDetailsValidator</c> already takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>"A transition without a reason code → 400" is enforced here, and again in the aggregate.</b>
/// A missing code deserialises as <see cref="TransitionReasonCode.Other"/> — it is the zero value —
/// so the edge cannot tell "absent" from "deliberately Other" by looking at the enum alone. That is
/// exactly why <see cref="TransitionReasonCode.Other"/> is the one code obliged to carry free text:
/// a body with no reason code and no notes fails this validator, which is the 400
/// WORK_PACKAGES.md asks for, while a caller who genuinely means Other says so by writing what
/// happened.
/// </para>
/// <para>
/// <b>Whether the code fits the kind is NOT checked here.</b> A validator sees one body and not
/// which route it arrived on, and duplicating the map would give two answers to keep in step. The
/// aggregate refuses it — see <c>AccountTransition.Record</c> and <c>Customer.ChangeClass</c> — so a
/// seeder and a later in-process caller meet the same rule.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The request body being validated.</typeparam>
public abstract class TransitionRequestValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : ITransitionRequest
{
    /// <summary>Builds the rules.</summary>
    protected TransitionRequestValidator()
    {
        RuleFor(request => request.ReasonCode).IsInEnum();
        RuleFor(request => request.Notes!).MaximumLength(AccountTransition.NotesLength);

        RuleFor(request => request.Notes)
            .NotEmpty()
            .When(request => TransitionReasons.RequiresNotes(request.ReasonCode))
            .WithMessage($"'{nameof(ITransitionRequest.Notes)}' is required when the reason code is "
                + $"'{TransitionReasonCode.Other}'. A fixed list is only fixed if its escape hatch explains itself.");

        // No ceiling on how far forward, and no floor beyond the ones the register itself applies. A
        // class change dated the first of next month is the ordinary case, and how far BACK a
        // transition may be dated depends on what has already been billed and when the account was
        // opened — neither of which a validator can see.
    }
}

/// <summary>Rules for moving a customer between classes.</summary>
public sealed class ChangeCustomerClassRequestValidator : TransitionRequestValidator<ChangeCustomerClassRequest>
{
    /// <summary>Builds the rules.</summary>
    public ChangeCustomerClassRequestValidator() => RuleFor(request => request.Class).IsInEnum();
}

/// <summary>Rules for moving a customer between statuses.</summary>
/// <remarks>
/// Only that the status is one GridCore declares. Whether the move is <i>legal</i> depends on where
/// the customer is now, which the validator cannot see and <c>CustomerTransitions</c> can — so that
/// answer is a 409 from the aggregate, not a 400 from here.
/// </remarks>
public sealed class ChangeCustomerStatusRequestValidator : TransitionRequestValidator<ChangeCustomerStatusRequest>
{
    /// <summary>Builds the rules.</summary>
    public ChangeCustomerStatusRequestValidator() => RuleFor(request => request.Status).IsInEnum();
}

/// <summary>Rules for moving a customer in at a premise.</summary>
public sealed class MoveInRequestValidator : TransitionRequestValidator<MoveInRequest>
{
    /// <summary>Builds the rules.</summary>
    public MoveInRequestValidator() => RuleFor(request => request.ServiceLocationId).NotEmpty();
}

/// <summary>Rules for ending a customer's service at a premise.</summary>
public sealed class MoveOutRequestValidator : TransitionRequestValidator<MoveOutRequest>
{
    /// <summary>Builds the rules.</summary>
    public MoveOutRequestValidator() => RuleFor(request => request.ServiceAccountId).NotEmpty();
}

/// <summary>Rules for moving a customer's service between premises.</summary>
/// <remarks>
/// That the destination is not where they already are is a rule about the <i>account</i> rather than
/// about the body — the request names an account and a premise, and whether they are the same
/// premise needs the account loaded. The service refuses it with a 409.
/// </remarks>
public sealed class TransferServiceRequestValidator : TransitionRequestValidator<TransferServiceRequest>
{
    /// <summary>Builds the rules.</summary>
    public TransferServiceRequestValidator()
    {
        RuleFor(request => request.FromServiceAccountId).NotEmpty();
        RuleFor(request => request.ToServiceLocationId).NotEmpty();
    }
}
