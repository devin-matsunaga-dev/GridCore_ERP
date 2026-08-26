using FluentValidation;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Transitions;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// Edge validation: the rules that answer 400 before a service is ever called. The aggregate keeps
/// its own guards for the same rules — a validator protects the endpoint, the aggregate protects
/// itself — so these assert what a caller is told, not whether the data could ever be stored.
/// </summary>
public class RegistryValidatorTests
{
    private static readonly CreateCustomerRequestValidator CustomerRules = new();
    private static readonly ChangeCustomerStatusRequestValidator StatusRules = new();
    private static readonly ServiceLocationRequestValidator LocationRules = new();
    private static readonly OpenServiceAccountRequestValidator OpenAccountRules = new();
    private static readonly ServiceAccountTransitionRequestValidator TransitionRules = new();

    private static CreateCustomerRequest AValidCustomer() =>
        new("Sablan Family Residence", CustomerClass.Residential, "Maria Sablan", "maria.sablan@example.com", "+1-670-532-0114");

    private static ServiceLocationRequest AValidLocation() =>
        new(new AddressPayload("128 As Nieves Road", "Songsong", "Rota", "MP", PostalCode: "96951"));

    private static IReadOnlyList<string> FailedFieldsOf<T>(IValidator<T> validator, T request) =>
        [.. validator.Validate(request).Errors.Select(failure => failure.PropertyName).Distinct()];

    [Fact]
    public void A_complete_registration_passes() =>
        Assert.True(CustomerRules.Validate(AValidCustomer()).IsValid);

    [Fact]
    public void A_registration_without_a_name_is_rejected() =>
        Assert.Equal(["Name"], FailedFieldsOf(CustomerRules, AValidCustomer() with { Name = "  " }));

    [Fact]
    public void A_name_longer_than_the_column_is_rejected_rather_than_truncated_at_the_edge() =>
        Assert.Equal(
            ["Name"],
            FailedFieldsOf(CustomerRules, AValidCustomer() with { Name = new string('x', Customer.NameLength + 1) }));

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("maria@")]
    [InlineData("@example.com")]
    public void An_address_that_is_not_an_email_is_rejected(string email) =>
        Assert.Equal(["Email"], FailedFieldsOf(CustomerRules, AValidCustomer() with { Email = email }));

    [Fact]
    public void No_email_at_all_is_fine() =>
        // A utility takes registrations over the counter. Requiring an email would make half of
        // them impossible.
        Assert.True(CustomerRules.Validate(AValidCustomer() with { Email = null }).IsValid);

    [Fact]
    public void A_class_that_is_not_declared_is_rejected() =>
        Assert.Equal(["Class"], FailedFieldsOf(CustomerRules, AValidCustomer() with { Class = (CustomerClass)99 }));

    [Fact]
    public void A_status_that_is_not_declared_is_rejected() =>
        Assert.Equal(
            ["Status"],
            FailedFieldsOf(StatusRules, new ChangeCustomerStatusRequest((CustomerStatus)42, TransitionReasonCode.UnpaidBalance)));

    [Fact]
    public void A_status_change_that_is_merely_illegal_passes_validation() =>
        // Deliberately: whether Prospect may become Suspended depends on where the customer is now,
        // which the validator cannot see. That is a 409 from the aggregate, not a 400 from here.
        Assert.True(StatusRules
            .Validate(new ChangeCustomerStatusRequest(CustomerStatus.Suspended, TransitionReasonCode.UnpaidBalance))
            .IsValid);

    [Fact]
    public void A_complete_premise_passes() =>
        Assert.True(LocationRules.Validate(AValidLocation()).IsValid);

    [Theory]
    [InlineData("Address.Line1")]
    [InlineData("Address.City")]
    [InlineData("Address.Region")]
    [InlineData("Address.Country")]
    public void A_premise_missing_a_required_address_part_is_rejected(string field)
    {
        var address = AValidLocation().Address;

        address = field switch
        {
            "Address.Line1" => address with { Line1 = "" },
            "Address.City" => address with { City = "" },
            "Address.Region" => address with { Region = "" },
            _ => address with { Country = "" },
        };

        Assert.Equal([field], FailedFieldsOf(LocationRules, AValidLocation() with { Address = address }));
    }

    [Fact]
    public void A_premise_with_no_address_at_all_is_rejected() =>
        Assert.Equal(["Address"], FailedFieldsOf(LocationRules, AValidLocation() with { Address = null! }));

    [Fact]
    public void Opening_an_account_needs_both_a_customer_and_a_premise() =>
        Assert.Equal(
            ["CustomerId", "ServiceLocationId"],
            FailedFieldsOf(OpenAccountRules, new OpenServiceAccountRequest(Guid.Empty, Guid.Empty)));

    [Fact]
    public void A_complete_account_opening_passes() =>
        Assert.True(OpenAccountRules
            .Validate(new OpenServiceAccountRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), "Requested at the counter"))
            .IsValid);

    [Fact]
    public void A_transition_needs_nothing_but_a_reason_that_fits()
    {
        // Whether the move is legal depends on where the account is now, which the validator cannot
        // see — that answer is a 409 from the aggregate, so an empty body is valid here.
        Assert.True(TransitionRules.Validate(new ServiceAccountTransitionRequest()).IsValid);

        Assert.Equal(
            ["Reason"],
            FailedFieldsOf(TransitionRules, new ServiceAccountTransitionRequest(new string('x', ServiceAccount.ReasonLength + 1))));
    }
}
