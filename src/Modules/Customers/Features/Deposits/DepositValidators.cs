using FluentValidation;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.Features.Deposits;

/// <summary>
/// Rules for taking a deposit.
/// </summary>
/// <remarks>
/// Field shape only, the split every validator in this module keeps. <b>Nothing here caps the
/// amount:</b> WP-2.8 refuses an intake that collects more than the schedule asks, and that rule
/// stays with the intake — a later collection rebuilds a deposit spent on a bill, or asks more of a
/// customer with a run of arrears, and a ceiling here would refuse both. Whether the caller may take
/// money at all is a 403 from the service, because a validator cannot see who is calling.
/// </remarks>
public sealed class CollectDepositRequestValidator : AbstractValidator<CollectDepositRequest>
{
    /// <summary>Builds the rules.</summary>
    public CollectDepositRequestValidator()
    {
        RuleFor(request => request.Amount)
            .GreaterThan(Money.Zero)
            .WithMessage("A deposit collected must be positive; a movement of nothing is not a movement.")
            .Must(Money.IsRounded)
            .WithMessage("A deposit must be a whole number of cents.");

        RuleFor(request => request.Reason!).MaximumLength(DepositEntry.ReasonLength);
    }
}

/// <summary>
/// Rules for putting a held deposit against a bill.
/// </summary>
/// <remarks>
/// How much the bill has outstanding is not among them: it lives in Billing, reaches this module
/// through <c>IBillDirectory</c>, and a request that offers more than is owed is a 409 from the
/// service rather than a malformed body.
/// </remarks>
public sealed class ApplyDepositRequestValidator : AbstractValidator<ApplyDepositRequest>
{
    /// <summary>Builds the rules.</summary>
    public ApplyDepositRequestValidator()
    {
        RuleFor(request => request.BillId).NotEmpty().WithMessage("Applying a deposit needs the bill it is applied to.");

        RuleFor(request => request.Amount)
            .GreaterThan(Money.Zero)
            .WithMessage("A deposit applied must be positive; a movement of nothing is not a movement.")
            .Must(Money.IsRounded)
            .WithMessage("A deposit must be a whole number of cents.");

        RuleFor(request => request.Reason!).MaximumLength(DepositEntry.ReasonLength);
    }
}

/// <summary>
/// Rules for giving a deposit back.
/// </summary>
/// <remarks>
/// "A refund cannot exceed the held balance" is deliberately absent: the balance is state, and the
/// aggregate refuses it — one guard in <c>Customer.RecordDepositMovement</c> covering refunds and
/// applications alike, rather than a copy here that could drift out of step with it.
/// </remarks>
public sealed class RefundDepositRequestValidator : AbstractValidator<RefundDepositRequest>
{
    /// <summary>Builds the rules.</summary>
    public RefundDepositRequestValidator()
    {
        RuleFor(request => request.Amount)
            .GreaterThan(Money.Zero)
            .WithMessage("A deposit refunded must be positive; a movement of nothing is not a movement.")
            .Must(Money.IsRounded)
            .WithMessage("A deposit must be a whole number of cents.");

        RuleFor(request => request.Reason!).MaximumLength(DepositEntry.ReasonLength);
    }
}
