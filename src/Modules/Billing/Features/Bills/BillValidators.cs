using FluentValidation;
using GridCore.Modules.Billing.Features.RatePlans;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>Rules for billing a reading cycle.</summary>
public sealed class RunBillingRequestValidator : AbstractValidator<RunBillingRequest>
{
    /// <summary>Builds the rules.</summary>
    public RunBillingRequestValidator() =>
        RuleFor(request => request.CycleCode).NotEmpty().MaximumLength(Bill.CycleCodeLength);
}

/// <summary>Rules for issuing a draft bill.</summary>
/// <remarks>
/// The due date is checked against the issue date by the aggregate rather than here, because the
/// issue date defaults to today when the caller omits it — a validator comparing two nullable fields
/// would either duplicate that default or refuse a body that is perfectly valid.
/// </remarks>
public sealed class IssueBillRequestValidator : AbstractValidator<IssueBillRequest>
{
    /// <summary>Builds the rules.</summary>
    public IssueBillRequestValidator() =>
        RuleFor(request => request.Reason!).MaximumLength(Bill.ReasonLength);
}

/// <summary>Rules for withdrawing a bill.</summary>
public sealed class CancelBillRequestValidator : AbstractValidator<CancelBillRequest>
{
    /// <summary>Builds the rules.</summary>
    public CancelBillRequestValidator() =>
        // Required here as well as in the aggregate. Cancelling a bill removes money the utility was
        // owed, and an empty reason should read as a 400 rather than reach the register at all.
        RuleFor(request => request.Reason).NotEmpty().MaximumLength(Bill.ReasonLength);
}

/// <summary>Rules for correcting an issued bill.</summary>
public sealed class AdjustBillRequestValidator : AbstractValidator<AdjustBillRequest>
{
    /// <summary>Builds the rules.</summary>
    public AdjustBillRequestValidator()
    {
        // A body naming a kind that is not one of ours would otherwise reach the aggregate and be
        // refused there as a 400 anyway — but only after the bill had been loaded, and with a
        // message about the enum rather than about the field the caller got wrong.
        RuleFor(request => request.Kind).IsInEnum();

        // Positive here as well as in the aggregate. The direction is the kind, so a negative amount
        // is a caller trying to say "credit" twice and reads as a 400 rather than as a workflow
        // conflict.
        RuleFor(request => request.Amount).GreaterThan(0m);

        // Required, like a cancellation's. An adjustment changes what a customer owes after they
        // have been told what they owe, and invariant 5 is the whole point of this endpoint.
        RuleFor(request => request.Reason).NotEmpty().MaximumLength(Bill.ReasonLength);
    }
}

/// <summary>Rules for reviewing overdue bills.</summary>
/// <remarks>
/// Nothing to check: the only field is an optional date, and any date is a legal thing to judge
/// against — reviewing "as at last month" is how somebody reconstructs what the ledger said then.
/// The validator exists because every write endpoint has one, which is what
/// <c>BillEndpointsTests</c> asserts.
/// </remarks>
public sealed class OverdueReviewRequestValidator : AbstractValidator<OverdueReviewRequest>;

/// <summary>Rules for putting a service account on a tariff.</summary>
public sealed class AssignRatePlanRequestValidator : AbstractValidator<AssignRatePlanRequest>
{
    /// <summary>Builds the rules.</summary>
    public AssignRatePlanRequestValidator() =>
        // Whether the code names a tariff the utility publishes is a 404 from the service, not a 400
        // from here: the answer is in the database, which no validator at the edge can see.
        RuleFor(request => request.RatePlanCode).NotEmpty().MaximumLength(RatePlan.CodeLength);
}
