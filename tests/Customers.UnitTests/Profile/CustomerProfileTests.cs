using GridCore.Modules.Customers.Features.Profile;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.UnitTests.Profile;

/// <summary>The profile aggregate on its own — the defaults it starts on, and what it refuses.</summary>
public class CustomerProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid ACustomer = Guid.CreateVersion7(Now);

    private static Address AnAddress() =>
        Address.Create("PO Box 501", "Songsong", "Rota", "MP", postalCode: "96951");

    [Fact]
    public void A_new_profile_is_post_with_both_notices_on()
    {
        var profile = CustomerProfile.Default(ACustomer, Now);

        // Post because it is the only channel a utility can honestly promise a customer who may have
        // no email; both notices on because a customer silently opted out of the warning that
        // precedes disconnection is the one default nobody could defend.
        Assert.Equal(BillDeliveryChannel.Post, profile.BillDeliveryChannel);
        Assert.True(profile.OutageNotices);
        Assert.True(profile.DunningNotices);
        Assert.Equal(CommunicationLanguage.English, profile.PreferredLanguage);
        Assert.Null(profile.MailingAddress);
        Assert.False(profile.HasMailingAddressOverride);
    }

    [Fact]
    public void A_profile_needs_a_customer() =>
        Assert.Throws<RegistryValidationException>(() => CustomerProfile.Default(Guid.Empty, Now));

    [Fact]
    public void Saving_a_mailing_address_makes_it_an_override()
    {
        var profile = CustomerProfile.Default(ACustomer, Now);

        profile.Update(AnAddress(), BillDeliveryChannel.Post, true, true, CommunicationLanguage.Chamorro, Now);

        Assert.True(profile.HasMailingAddressOverride);
        Assert.Equal("PO Box 501", profile.MailingAddress!.Line1);
        Assert.Equal(CommunicationLanguage.Chamorro, profile.PreferredLanguage);
    }

    [Fact]
    public void Clearing_the_mailing_address_clears_the_override_not_the_address()
    {
        var profile = CustomerProfile.Default(ACustomer, Now);

        profile.Update(AnAddress(), BillDeliveryChannel.Post, true, true, CommunicationLanguage.English, Now);
        profile.Update(null, BillDeliveryChannel.Post, true, true, CommunicationLanguage.English, Now.AddDays(1));

        // Null here means "post follows the service address" — which one that is belongs to
        // CustomerProfileService, because this row cannot see the accounts.
        Assert.Null(profile.MailingAddress);
        Assert.False(profile.HasMailingAddressOverride);
    }

    [Fact]
    public void An_undeclared_channel_is_refused()
    {
        var profile = CustomerProfile.Default(ACustomer, Now);

        Assert.Throws<RegistryValidationException>(() =>
            profile.Update(null, (BillDeliveryChannel)9, true, true, CommunicationLanguage.English, Now));
    }

    [Fact]
    public void An_undeclared_language_is_refused()
    {
        var profile = CustomerProfile.Default(ACustomer, Now);

        Assert.Throws<RegistryValidationException>(() =>
            profile.Update(null, BillDeliveryChannel.Post, true, true, (CommunicationLanguage)9, Now));
    }

    [Fact]
    public void A_refused_save_leaves_the_profile_exactly_as_it_was()
    {
        var profile = CustomerProfile.Default(ACustomer, Now);

        profile.Update(AnAddress(), BillDeliveryChannel.Post, true, true, CommunicationLanguage.English, Now);

        Assert.Throws<RegistryValidationException>(() =>
            profile.Update(null, (BillDeliveryChannel)9, false, false, CommunicationLanguage.English, Now.AddDays(1)));

        Assert.True(profile.HasMailingAddressOverride);
        Assert.True(profile.OutageNotices);
        Assert.Equal(Now, profile.UpdatedAt);
    }
}
