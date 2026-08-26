using FluentValidation;
using GridCore.Modules.Customers.Features.Deposits;

namespace GridCore.Modules.Customers.UnitTests.Deposits;

/// <summary>
/// The deposit bodies' field rules.
/// </summary>
/// <remarks>
/// Field shape only, and what is <i>absent</i> here is as deliberate as what is present: no ceiling
/// on a collection (WP-2.8's schedule cap belongs to the intake), and no check that a refund fits
/// the held balance (that is state the aggregate owns, and a copy here would drift out of step).
/// </remarks>
public class DepositValidatorTests
{
    private static readonly CollectDepositRequestValidator CollectRules = new();
    private static readonly ApplyDepositRequestValidator ApplyRules = new();
    private static readonly RefundDepositRequestValidator RefundRules = new();

    private static IReadOnlyList<string> FailedFieldsOf<T>(IValidator<T> validator, T request) =>
        [.. validator.Validate(request).Errors.Select(failure => failure.PropertyName).Distinct()];

    [Fact]
    public void A_complete_collection_passes() =>
        Assert.True(CollectRules.Validate(new CollectDepositRequest(75.00m, true, "Taken at the counter.")).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-75)]
    public void A_collection_of_nothing_or_less_is_rejected(decimal amount) =>
        Assert.Equal(["Amount"], FailedFieldsOf(CollectRules, new CollectDepositRequest(amount)));

    [Fact]
    public void A_collection_finer_than_a_cent_is_rejected() =>
        Assert.Equal(["Amount"], FailedFieldsOf(CollectRules, new CollectDepositRequest(75.125m)));

    [Fact]
    public void A_collection_above_the_schedule_is_NOT_rejected_here() =>
        // Deliberate. WP-2.8 refuses an intake that collects more than the class is assessed, and
        // that rule stays with the intake: a later collection rebuilds a deposit spent on a bill,
        // or asks more of a customer with a run of arrears, and a ceiling here would refuse both.
        Assert.True(CollectRules.Validate(new CollectDepositRequest(5_000.00m)).IsValid);

    [Fact]
    public void A_reason_longer_than_the_column_is_rejected_rather_than_truncated_at_the_edge() =>
        Assert.Equal(
            ["Reason"],
            FailedFieldsOf(CollectRules, new CollectDepositRequest(75.00m, Reason: new string('x', DepositEntry.ReasonLength + 1))));

    [Fact]
    public void An_application_that_names_no_bill_is_rejected() =>
        // A deposit is applied TO A BILL or it is not applied at all.
        Assert.Equal(["BillId"], FailedFieldsOf(ApplyRules, new ApplyDepositRequest(Guid.Empty, 40.00m)));

    [Fact]
    public void A_complete_application_passes() =>
        Assert.True(ApplyRules.Validate(new ApplyDepositRequest(Guid.CreateVersion7(), 40.00m)).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    public void An_application_of_nothing_or_less_is_rejected(decimal amount) =>
        Assert.Equal(["Amount"], FailedFieldsOf(ApplyRules, new ApplyDepositRequest(Guid.CreateVersion7(), amount)));

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    public void A_refund_of_nothing_or_less_is_rejected(decimal amount) =>
        Assert.Equal(["Amount"], FailedFieldsOf(RefundRules, new RefundDepositRequest(amount)));

    [Fact]
    public void A_refund_finer_than_a_cent_is_rejected() =>
        Assert.Equal(["Amount"], FailedFieldsOf(RefundRules, new RefundDepositRequest(0.005m)));

    [Fact]
    public void A_refund_larger_than_any_plausible_balance_is_NOT_rejected_here() =>
        // Also deliberate. The balance is state; Customer.RecordDepositMovement refuses it as a 409,
        // and a copy of that rule here would be a second place to keep in step.
        Assert.True(RefundRules.Validate(new RefundDepositRequest(1_000_000.00m)).IsValid);
}
