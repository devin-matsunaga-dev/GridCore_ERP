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
