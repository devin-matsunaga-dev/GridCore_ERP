using FluentValidation;

namespace GridCore.Modules.Payments.Features.Payments;

/// <summary>Rules for taking a payment.</summary>
public sealed class TakePaymentRequestValidator : AbstractValidator<TakePaymentRequest>
{
    /// <summary>Builds the rules.</summary>
    public TakePaymentRequestValidator()
    {
        RuleFor(request => request.BillId).NotEmpty();

        // Positive here as well as in the aggregate. A negative payment is a caller trying to write
        // a refund through the wrong door, and it reads as a 400 rather than as a workflow conflict.
        // Whether it is more than is owed is the aggregate's answer, not this one's: the balance is
        // in another module's register, which no validator at the edge can see.
        RuleFor(request => request.Amount).GreaterThan(0m);

        // Whether the method is one the utility accepts is the aggregate's answer too — it owns the
        // list — but an empty one should not reach it at all.
        RuleFor(request => request.Method).NotEmpty().MaximumLength(Payment.MethodLength);

        RuleFor(request => request.Instrument!).MaximumLength(Payment.InstrumentLength);
    }
}
