using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceLocations;

namespace GridCore.Modules.Customers.UnitTests.Registration;

/// <summary>
/// The intake body's edge validation — the per-step rules the wizard enforces in the browser,
/// enforced again where they are binding. Everything here is a 400; the rules that depend on state
/// or on who is calling are the service's, and are tested there.
/// </summary>
public class IntakeValidatorTests
{
    private static readonly RegisterCustomerIntakeRequestValidator Validator = new();

    private static IntakePremiseRequest ANewPremise() =>
        new(new NewPremiseRequest(new AddressPayload("77 As Nieves Road", "Songsong", "Rota", "MP"), "Meter on the north wall"));

    private static RegisterCustomerIntakeRequest AnIntake(
        string name = "Reyes Family Residence",
        CustomerClass customerClass = CustomerClass.Residential,
        IntakePremiseRequest? premise = null,
        string? email = null,
        decimal deposit = 0m) =>
        new(name, customerClass, premise ?? ANewPremise(), "Ana Reyes", email, "+1-670-532-0199", deposit);

    private static IReadOnlyList<string> ErrorsFor(RegisterCustomerIntakeRequest request) =>
        [.. Validator.Validate(request).Errors.Select(failure => failure.PropertyName)];

    [Fact]
    public void A_complete_intake_passes() =>
        Assert.True(Validator.Validate(AnIntake(email: "ana.reyes@example.com", deposit: 75.00m)).IsValid);

    [Fact]
    public void A_customer_needs_a_name() =>
        Assert.Contains(nameof(RegisterCustomerIntakeRequest.Name), ErrorsFor(AnIntake(name: "  ")));

    [Fact]
    public void A_class_GridCore_does_not_declare_is_refused() =>
        Assert.Contains(nameof(RegisterCustomerIntakeRequest.Class), ErrorsFor(AnIntake(customerClass: (CustomerClass)77)));

    [Fact]
    public void An_address_that_is_not_an_email_is_refused() =>
        Assert.Contains("Email", ErrorsFor(AnIntake(email: "ana.reyes.example.com")));

    [Fact]
    public void An_intake_naming_no_premise_is_refused() =>
        // The failure a half-finished wizard would produce, caught at the edge rather than after
        // the transaction has opened.
        Assert.NotEmpty(ErrorsFor(AnIntake(premise: new IntakePremiseRequest())));

    [Fact]
    public void An_intake_naming_both_a_new_and_an_existing_premise_is_refused() =>
        Assert.NotEmpty(ErrorsFor(AnIntake(premise: new IntakePremiseRequest(
            ANewPremise().NewPremise,
            Guid.CreateVersion7()))));

    [Fact]
    public void A_new_premise_needs_an_address_that_a_crew_could_find() =>
        Assert.NotEmpty(ErrorsFor(AnIntake(premise: new IntakePremiseRequest(
            new NewPremiseRequest(new AddressPayload(string.Empty, "Songsong", "Rota", "MP"))))));

    [Fact]
    public void A_negative_deposit_is_refused() =>
        Assert.Contains(nameof(RegisterCustomerIntakeRequest.DepositCollected), ErrorsFor(AnIntake(deposit: -1m)));

    [Fact]
    public void A_deposit_finer_than_a_cent_is_refused_rather_than_rounded() =>
        Assert.Contains(nameof(RegisterCustomerIntakeRequest.DepositCollected), ErrorsFor(AnIntake(deposit: 75.125m)));

    [Fact]
    public void Whether_the_deposit_may_be_collected_at_all_is_not_this_validator_s_question() =>
        // A deposit above the schedule and a caller without the permission are both refused, by the
        // service — one needs the reference table and the other needs the principal, and a validator
        // over a request body has neither.
        Assert.True(Validator.Validate(AnIntake(deposit: 10_000m)).IsValid);
}
