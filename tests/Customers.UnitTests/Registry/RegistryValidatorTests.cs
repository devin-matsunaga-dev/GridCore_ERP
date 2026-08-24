using FluentValidation;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceLocations;

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

    private static CreateCustomerRequest AValidCustomer() =>
        new("Sablan Family Residence", CustomerClass.Residential, "Maria Sablan", "maria.sablan@example.com", "+1-670-532-0114", 75.00m);

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
    public void A_negative_deposit_is_rejected() =>
        Assert.Equal(["DepositHeld"], FailedFieldsOf(CustomerRules, AValidCustomer() with { DepositHeld = -0.01m }));

    [Fact]
    public void A_deposit_finer_than_a_cent_is_rejected() =>
        Assert.Equal(["DepositHeld"], FailedFieldsOf(CustomerRules, AValidCustomer() with { DepositHeld = 75.125m }));

    [Fact]
    public void A_class_that_is_not_declared_is_rejected() =>
        Assert.Equal(["Class"], FailedFieldsOf(CustomerRules, AValidCustomer() with { Class = (CustomerClass)99 }));

    [Fact]
    public void A_status_that_is_not_declared_is_rejected() =>
        Assert.Equal(["Status"], FailedFieldsOf(StatusRules, new ChangeCustomerStatusRequest((CustomerStatus)42)));

    [Fact]
    public void A_status_change_that_is_merely_illegal_passes_validation() =>
        // Deliberately: whether Prospect may become Suspended depends on where the customer is now,
        // which the validator cannot see. That is a 409 from the aggregate, not a 400 from here.
        Assert.True(StatusRules.Validate(new ChangeCustomerStatusRequest(CustomerStatus.Suspended)).IsValid);

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
}
