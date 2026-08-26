using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.Features.ServiceLocations;

namespace GridCore.Modules.Customers.Features.Profile;

/// <summary>
/// The parts of a customer record a rep maintains that are about <i>reaching</i> them rather than
/// about who they are: where post goes, and how they want to be written to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A table of its own, keyed by the customer.</b> Not columns on <c>customers.customers</c>: that
/// row is read by the registry list, by the 360 and by both stages of the WP-2.9 search, and none of
/// those wants ten more columns or the join to fetch them. A customer with no row here is a customer
/// on the defaults, which is the honest state — a preference nobody has expressed is not a stored
/// preference, and the row appears the first time a rep saves one.
/// </para>
/// <para>
/// <b>A null <see cref="MailingAddress"/> is not "no address" — it is "the service address".</b>
/// The override is the whole point of the column: post goes to where service is delivered until
/// somebody says otherwise, and clearing it puts the customer back on that default rather than
/// leaving the utility with nowhere to send a bill. Which service address that is comes from
/// <see cref="CustomerProfileService"/>, because it depends on the accounts and this row cannot see
/// them.
/// </para>
/// </remarks>
public sealed class CustomerProfile
{
    private CustomerProfile()
    {
        // EF materialisation.
    }

    /// <summary>The customer this profile belongs to. Also the primary key — one profile, one customer.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>
    /// Where post goes when it is not the service address, or <see langword="null"/> while it is.
    /// </summary>
    public Address? MailingAddress { get; private set; }

    /// <summary>How the bill reaches them.</summary>
    public BillDeliveryChannel BillDeliveryChannel { get; private set; }

    /// <summary>Whether they want to be told about planned and unplanned outages.</summary>
    public bool OutageNotices { get; private set; }

    /// <summary>Whether they want reminders before a bill goes to collections.</summary>
    public bool DunningNotices { get; private set; }

    /// <summary>What language to write and speak to them in.</summary>
    public CommunicationLanguage PreferredLanguage { get; private set; }

    /// <summary>When the preferences were last saved.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Whether post goes somewhere other than the premise service is delivered to.</summary>
    public bool HasMailingAddressOverride => MailingAddress is not null;

    /// <summary>
    /// The profile a customer has before anybody has saved one.
    /// </summary>
    /// <remarks>
    /// <b>Post, and both notice types on.</b> A utility that has just registered a customer has an
    /// address and may well have no email, so post is the only channel it can honestly promise; and
    /// a customer silently opted out of the warning that precedes disconnection is the one default
    /// nobody could defend. Silence means "tell me", and the screen is where it is turned off.
    /// </remarks>
    public static CustomerProfile Default(Guid customerId, DateTimeOffset now)
    {
        if (customerId == Guid.Empty)
        {
            throw new RegistryValidationException("'customerId' is required for a customer profile.");
        }

        return new CustomerProfile
        {
            CustomerId = customerId,
            MailingAddress = null,
            BillDeliveryChannel = BillDeliveryChannel.Post,
            OutageNotices = true,
            DunningNotices = true,
            PreferredLanguage = CommunicationLanguage.English,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Saves the profile. A <see langword="null"/> <paramref name="mailingAddress"/> clears the
    /// override rather than clearing the address — see the remarks on <see cref="MailingAddress"/>.
    /// </summary>
    /// <exception cref="RegistryValidationException">A stored enum is not one GridCore declares.</exception>
    public void Update(
        Address? mailingAddress,
        BillDeliveryChannel billDeliveryChannel,
        bool outageNotices,
        bool dunningNotices,
        CommunicationLanguage preferredLanguage,
        DateTimeOffset now)
    {
        RequireDeclared(billDeliveryChannel);
        RequireDeclared(preferredLanguage);

        MailingAddress = mailingAddress;
        BillDeliveryChannel = billDeliveryChannel;
        OutageNotices = outageNotices;
        DunningNotices = dunningNotices;
        PreferredLanguage = preferredLanguage;
        UpdatedAt = now;
    }

    private static void RequireDeclared<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        // A value cast from an unmapped integer would be stored by name as a number and read back
        // as nothing anyone can act on — the rule CustomerConfiguration states for every stored enum.
        if (!Enum.IsDefined(value))
        {
            throw new RegistryValidationException($"'{value}' is not a {typeof(TEnum).Name} GridCore declares.");
        }
    }
}
