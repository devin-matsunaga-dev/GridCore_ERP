using GridCore.Modules.Customers.Features.Contacts;
using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.UnitTests.Contacts;

/// <summary>
/// The contact aggregate on its own — no database, no container, no host. Every rule about the
/// <i>set</i> of methods lives here, which is why it can be argued with in microseconds:
/// "exactly one primary per kind" is arithmetic over a list, not a database constraint that happens
/// to hold.
/// </summary>
public class CustomerContactTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid ACustomer = Guid.CreateVersion7(Now);

    private static CustomerContact AContact() =>
        CustomerContact.Add(ACustomer, "Rosa Sablan", "Spouse", Now);

    [Fact]
    public void A_contact_starts_unauthorised_to_discuss_the_account()
    {
        var contact = AContact();

        // The flag answers "may I tell this caller what the bill is". Its safe value is no, and a
        // default of yes would make every contact ever added a disclosure nobody decided on.
        Assert.False(contact.IsAuthorisedToDiscuss);
        Assert.Equal("Rosa Sablan", contact.Name);
        Assert.Equal("Spouse", contact.Relationship);
        Assert.Empty(contact.Methods);
    }

    [Fact]
    public void A_contact_needs_a_name() =>
        Assert.Throws<RegistryValidationException>(() => CustomerContact.Add(ACustomer, "   ", "Spouse", Now));

    [Fact]
    public void A_contact_needs_a_customer() =>
        Assert.Throws<RegistryValidationException>(() => CustomerContact.Add(Guid.Empty, "Rosa Sablan", null, Now));

    [Fact]
    public void The_first_method_of_a_kind_is_primary_whether_or_not_it_was_asked_for()
    {
        var contact = AContact();

        var method = contact.AddMethod(ContactMethodKind.Mobile, "+1-670-285-1180", isPrimary: false, Now);

        // A lone number nothing points at is how a screen ends up showing a contact with no
        // telephone beside their name: the rule has to hold for a kind with one method too.
        Assert.True(method.IsPrimary);
        Assert.Same(method, contact.PrimaryFor(ContactMethodKind.Mobile));
    }

    [Fact]
    public void Promoting_a_second_method_demotes_the_first()
    {
        var contact = AContact();

        var first = contact.AddMethod(ContactMethodKind.Phone, "+1-670-532-0114", isPrimary: false, Now);
        var second = contact.AddMethod(ContactMethodKind.Phone, "+1-670-532-9987", isPrimary: false, Now);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);

        contact.MakeMethodPrimary(second.Id);

        Assert.False(first.IsPrimary);
        Assert.True(second.IsPrimary);
        Assert.Single(contact.Methods, method => method.Kind == ContactMethodKind.Phone && method.IsPrimary);
    }

    [Fact]
    public void Adding_a_method_as_primary_demotes_the_one_that_held_the_place()
    {
        var contact = AContact();

        var first = contact.AddMethod(ContactMethodKind.Email, "rosa@example.com", isPrimary: false, Now);
        var second = contact.AddMethod(ContactMethodKind.Email, "rosa.sablan@example.com", isPrimary: true, Now);

        Assert.False(first.IsPrimary);
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public void Each_kind_keeps_its_own_primary()
    {
        var contact = AContact();

        var phone = contact.AddMethod(ContactMethodKind.Phone, "+1-670-532-0114", isPrimary: false, Now);
        var mobile = contact.AddMethod(ContactMethodKind.Mobile, "+1-670-285-1180", isPrimary: false, Now);
        var email = contact.AddMethod(ContactMethodKind.Email, "rosa@example.com", isPrimary: false, Now);

        // Phone and Mobile are two kinds, not one with a flag — promoting a mobile must not take the
        // place a landline holds, or an outage SMS and a call would be fighting over one column.
        Assert.True(phone.IsPrimary);
        Assert.True(mobile.IsPrimary);
        Assert.True(email.IsPrimary);
    }

    [Fact]
    public void Removing_the_primary_hands_the_place_to_the_oldest_method_left()
    {
        var contact = AContact();

        var oldest = contact.AddMethod(ContactMethodKind.Phone, "+1-670-532-0114", isPrimary: false, Now);
        var newest = contact.AddMethod(ContactMethodKind.Phone, "+1-670-532-9987", isPrimary: false, Now.AddDays(1));
        var promoted = contact.AddMethod(ContactMethodKind.Phone, "+1-670-532-4400", isPrimary: true, Now.AddDays(2));

        contact.RemoveMethod(promoted.Id);

        // The longest-standing number, not the most recently typed: it is the one the utility has
        // been reaching this person on, and a silent promotion of the newest is the surprising answer.
        Assert.True(oldest.IsPrimary);
        Assert.False(newest.IsPrimary);
    }

    [Fact]
    public void Removing_the_last_method_of_a_kind_leaves_that_kind_without_a_primary()
    {
        var contact = AContact();

        var only = contact.AddMethod(ContactMethodKind.Email, "rosa@example.com", isPrimary: false, Now);

        contact.RemoveMethod(only.Id);

        // The rule is one primary per kind the contact HAS. A kind with nothing in it correctly has
        // no primary, which is not the same failure as a kind with two.
        Assert.Null(contact.PrimaryFor(ContactMethodKind.Email));
        Assert.Empty(contact.Methods);
    }

    [Fact]
    public void The_same_value_cannot_be_recorded_twice_for_one_kind()
    {
        var contact = AContact();

        contact.AddMethod(ContactMethodKind.Email, "rosa@example.com", isPrimary: false, Now);

        Assert.Throws<RegistryValidationException>(() =>
            contact.AddMethod(ContactMethodKind.Email, "ROSA@example.com", isPrimary: false, Now));
    }

    [Fact]
    public void One_value_may_be_both_a_phone_and_a_mobile()
    {
        var contact = AContact();

        contact.AddMethod(ContactMethodKind.Phone, "+1-670-285-1180", isPrimary: false, Now);

        // The clash is per kind, deliberately: a one-person business whose landline is diverted to a
        // mobile is an ordinary state of affairs, not a data-entry slip.
        var mobile = contact.AddMethod(ContactMethodKind.Mobile, "+1-670-285-1180", isPrimary: false, Now);

        Assert.True(mobile.IsPrimary);
        Assert.Equal(2, contact.Methods.Count);
    }

    [Fact]
    public void A_method_needs_a_value()
    {
        var contact = AContact();

        Assert.Throws<RegistryValidationException>(() =>
            contact.AddMethod(ContactMethodKind.Phone, "   ", isPrimary: false, Now));
    }

    [Fact]
    public void An_undeclared_kind_is_refused()
    {
        var contact = AContact();

        Assert.Throws<RegistryValidationException>(() =>
            contact.AddMethod((ContactMethodKind)42, "+1-670-532-0114", isPrimary: false, Now));
    }

    [Fact]
    public void Correcting_a_method_keeps_its_kind_and_its_primary_place()
    {
        var contact = AContact();

        var method = contact.AddMethod(ContactMethodKind.Mobile, "+1-670-285-1180", isPrimary: false, Now);

        contact.CorrectMethod(method.Id, "+1-670-285-1181");

        Assert.Equal("+1-670-285-1181", method.Value);
        Assert.Equal(ContactMethodKind.Mobile, method.Kind);
        Assert.True(method.IsPrimary);
    }

    [Fact]
    public void Correcting_a_method_into_a_value_the_contact_already_holds_is_refused()
    {
        var contact = AContact();

        contact.AddMethod(ContactMethodKind.Phone, "+1-670-532-0114", isPrimary: false, Now);
        var second = contact.AddMethod(ContactMethodKind.Phone, "+1-670-532-9987", isPrimary: false, Now);

        Assert.Throws<RegistryValidationException>(() => contact.CorrectMethod(second.Id, "+1-670-532-0114"));
    }

    [Fact]
    public void A_method_this_contact_does_not_hold_is_a_workflow_conflict()
    {
        var contact = AContact();

        Assert.Throws<RegistryWorkflowException>(() => contact.MakeMethodPrimary(Guid.CreateVersion7(Now)));
        Assert.Throws<RegistryWorkflowException>(() => contact.RemoveMethod(Guid.CreateVersion7(Now)));
    }

    [Fact]
    public void A_rejected_correction_leaves_the_contact_exactly_as_it_was()
    {
        var contact = AContact();

        Assert.Throws<RegistryValidationException>(() => contact.UpdateDetails("  ", "Landlord"));

        // Guarded before the first assignment, so a refused edit cannot half-apply — the rule
        // Customer.UpdateDetails already follows.
        Assert.Equal("Rosa Sablan", contact.Name);
        Assert.Equal("Spouse", contact.Relationship);
    }

    [Fact]
    public void A_long_email_is_capped_rather_than_throwing()
    {
        var contact = AContact();

        var method = contact.AddMethod(ContactMethodKind.Email, new string('a', 400), isPrimary: false, Now);

        Assert.Equal(ContactMethod.MaxLengthFor(ContactMethodKind.Email), method.Value.Length);
    }

    [Fact]
    public void A_phone_is_capped_shorter_than_an_email()
    {
        // Per kind rather than one width for all: a phone column that quietly accepts 300 characters
        // of pasted signature block is a phone column the search will be handed one day.
        Assert.True(ContactMethod.MaxLengthFor(ContactMethodKind.Phone) < ContactMethod.MaxLengthFor(ContactMethodKind.Email));
        Assert.Equal(ContactMethod.MaxLengthFor(ContactMethodKind.Phone), ContactMethod.MaxLengthFor(ContactMethodKind.Mobile));
    }
}
