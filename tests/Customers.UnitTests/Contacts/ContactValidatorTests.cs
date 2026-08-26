using GridCore.Modules.Customers.Features.Contacts;
using GridCore.Modules.Customers.Features.Profile;
using GridCore.Modules.Customers.Features.ServiceLocations;

namespace GridCore.Modules.Customers.UnitTests.Contacts;

/// <summary>
/// The edge rules. Format lives here and structure lives in the aggregate — the split
/// <c>CustomerDetailsValidator</c> already makes, so a seeder calling the service directly cannot
/// write what a request could not.
/// </summary>
public class ContactValidatorTests
{
    private static readonly CreateContactRequestValidator Create = new();
    private static readonly UpdateContactRequestValidator Update = new();
    private static readonly ContactMethodRequestValidator Method = new();
    private static readonly UpdateCustomerProfileRequestValidator Profile = new();

    [Fact]
    public void A_contact_needs_a_name() =>
        Assert.False(Create.Validate(new CreateContactRequest("   ")).IsValid);

    [Fact]
    public void A_contact_with_a_name_is_enough() =>
        // Relationship and methods are optional: a rep who has a name and nothing else has still
        // learnt something worth recording, and a form that refused it would lose it.
        Assert.True(Create.Validate(new CreateContactRequest("Rosa Sablan")).IsValid);

    [Fact]
    public void A_relationship_longer_than_the_column_is_refused() =>
        Assert.False(Create.Validate(new CreateContactRequest("Rosa Sablan", new string('x', CustomerContact.RelationshipLength + 1))).IsValid);

    [Fact]
    public void An_email_method_must_look_like_an_email() =>
        Assert.False(Method.Validate(new ContactMethodRequest(ContactMethodKind.Email, "not-an-address")).IsValid);

    [Fact]
    public void A_phone_method_is_not_held_to_the_email_rule() =>
        // A telephone number is not an email address, and running the email rule over every kind is
        // how "+1-670-532-0114" gets refused for missing an @.
        Assert.True(Method.Validate(new ContactMethodRequest(ContactMethodKind.Phone, "+1-670-532-0114")).IsValid);

    [Fact]
    public void A_method_needs_a_value() =>
        Assert.False(Method.Validate(new ContactMethodRequest(ContactMethodKind.Mobile, "  ")).IsValid);

    [Fact]
    public void A_phone_longer_than_the_phone_column_is_refused() =>
        // Per kind, not one width for all: this string fits an email column and would silently be
        // truncated into a phone one.
        Assert.False(Method.Validate(new ContactMethodRequest(ContactMethodKind.Phone, new string('9', 200))).IsValid);

    [Fact]
    public void An_undeclared_kind_is_refused() =>
        Assert.False(Method.Validate(new ContactMethodRequest((ContactMethodKind)42, "+1-670-532-0114")).IsValid);

    [Fact]
    public void A_method_on_an_intake_gets_the_same_rules_it_would_get_on_its_own() =>
        Assert.False(Create.Validate(new CreateContactRequest(
            "Rosa Sablan",
            Methods: [new ContactMethodRequest(ContactMethodKind.Email, "not-an-address")])).IsValid);

    [Fact]
    public void The_authorised_flag_is_not_a_validation_question() =>
        // Whether the caller may move it is a permission question the service answers with a 403. A
        // validator refusing it would report a 400 for a request that is not malformed at all.
        Assert.True(Update.Validate(new UpdateContactRequest("Rosa Sablan", "Spouse", IsAuthorisedToDiscuss: true)).IsValid);

    [Fact]
    public void A_profile_with_no_mailing_address_is_valid() =>
        // Absent means "post follows the service address" — the state this resource exists to carry.
        Assert.True(Profile.Validate(new UpdateCustomerProfileRequest(
            BillDeliveryChannel.Post, true, true, CommunicationLanguage.English)).IsValid);

    [Fact]
    public void A_mailing_address_that_is_present_must_be_complete() =>
        Assert.False(Profile.Validate(new UpdateCustomerProfileRequest(
            BillDeliveryChannel.Post,
            true,
            true,
            CommunicationLanguage.English,
            new AddressPayload("PO Box 501", City: "", Region: "Rota", Country: "MP"))).IsValid);

    [Fact]
    public void An_undeclared_channel_or_language_is_refused()
    {
        Assert.False(Profile.Validate(new UpdateCustomerProfileRequest(
            (BillDeliveryChannel)9, true, true, CommunicationLanguage.English)).IsValid);

        Assert.False(Profile.Validate(new UpdateCustomerProfileRequest(
            BillDeliveryChannel.Post, true, true, (CommunicationLanguage)9)).IsValid);
    }

    [Fact]
    public void Whether_email_delivery_is_possible_is_not_a_validation_question() =>
        // It depends on whether the customer has an email on file, which the request cannot see and
        // CustomerProfileService can — the same boundary WP-2.8 drew around the deposit.
        Assert.True(Profile.Validate(new UpdateCustomerProfileRequest(
            BillDeliveryChannel.Email, true, true, CommunicationLanguage.English)).IsValid);
}
