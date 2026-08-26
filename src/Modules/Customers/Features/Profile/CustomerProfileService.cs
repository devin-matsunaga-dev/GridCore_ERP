using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Profile;

/// <summary>What a caller supplies to save a customer's profile.</summary>
/// <param name="MailingAddress">Where post goes, or <see langword="null"/> to send it to the service address.</param>
/// <param name="BillDeliveryChannel">How the bill reaches them.</param>
/// <param name="OutageNotices">Whether they want outage notices.</param>
/// <param name="DunningNotices">Whether they want reminders before collections.</param>
/// <param name="PreferredLanguage">What language to write to them in.</param>
public sealed record UpdateCustomerProfileInput(
    Address? MailingAddress,
    BillDeliveryChannel BillDeliveryChannel,
    bool OutageNotices,
    bool DunningNotices,
    CommunicationLanguage PreferredLanguage);

/// <summary>Where a mailing address came from.</summary>
public enum MailingAddressSource
{
    /// <summary>Nowhere — the customer holds no service account, and nobody has typed an address.</summary>
    None,

    /// <summary>The premise of the customer's most recently active service account.</summary>
    ServiceAddress,

    /// <summary>An address a rep entered against the customer, deliberately different from the service address.</summary>
    Override,
}

/// <summary>
/// A customer's profile as a screen reads it: the preferences, and the mailing address <b>resolved</b>
/// — with the default beside it, so a rep can see what clearing the override would fall back to.
/// </summary>
/// <param name="CustomerId">Whose profile.</param>
/// <param name="MailingAddress">Where post actually goes, override or default, or <see langword="null"/> if there is nowhere.</param>
/// <param name="Source">Which of the two the address came from.</param>
/// <param name="ServiceAddress">The premise post falls back to, whether or not it is in use.</param>
/// <param name="ServiceLocationId">Which premise that is.</param>
/// <param name="BillDeliveryChannel">How the bill reaches them.</param>
/// <param name="OutageNotices">Whether they want outage notices.</param>
/// <param name="DunningNotices">Whether they want reminders before collections.</param>
/// <param name="PreferredLanguage">What language to write to them in.</param>
/// <param name="UpdatedAt">When the preferences were last saved, or <see langword="null"/> while they are still the defaults.</param>
public sealed record CustomerProfileView(
    Guid CustomerId,
    Address? MailingAddress,
    MailingAddressSource Source,
    Address? ServiceAddress,
    Guid? ServiceLocationId,
    BillDeliveryChannel BillDeliveryChannel,
    bool OutageNotices,
    bool DunningNotices,
    CommunicationLanguage PreferredLanguage,
    DateTimeOffset? UpdatedAt);

/// <summary>A customer's mailing address and communication preferences. The module's own surface.</summary>
public interface ICustomerProfileService
{
    /// <summary>The profile as a screen reads it, resolved against the customer's service accounts.</summary>
    Task<CustomerProfileView> GetAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>Saves the profile and returns it resolved.</summary>
    Task<CustomerProfileView> UpdateAsync(Guid customerId, UpdateCustomerProfileInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// The customer profile over the customers schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fallback is resolved here, on every read, and never stored.</b> Copying the service
/// address into the profile row at save time would freeze it: a customer who transfers to another
/// premise would keep getting post at the house they left, and nothing on the screen would say why.
/// So the column holds the override or nothing, and this service answers "where does post actually
/// go" by asking the accounts each time — see <see cref="ServiceAddressDefault"/> for which account.
/// </para>
/// <para>
/// A customer with no row here reads back as the defaults with a <see langword="null"/>
/// <see cref="CustomerProfileView.UpdatedAt"/>, which is the difference between "nobody has said"
/// and "somebody chose exactly this". The row is written the first time a rep saves.
/// </para>
/// </remarks>
public sealed class CustomerProfileService(
    CustomersDbContext database,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    TimeProvider clock) : ICustomerProfileService
{
    /// <inheritdoc />
    public async Task<CustomerProfileView> GetAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        await RequireCustomerAsync(customerId, cancellationToken).ConfigureAwait(false);

        var profile = await database.CustomerProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.CustomerId == customerId, cancellationToken)
            .ConfigureAwait(false);

        var (serviceAddress, serviceLocationId) = await ServiceAddressAsync(customerId, cancellationToken).ConfigureAwait(false);

        return Resolve(customerId, profile, serviceAddress, serviceLocationId);
    }

    /// <inheritdoc />
    public Task<CustomerProfileView> UpdateAsync(Guid customerId, UpdateCustomerProfileInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var customer = await RequireCustomerAsync(customerId, ct).ConfigureAwait(false);

                RequireDeliverable(customer, input.BillDeliveryChannel);

                var profile = await database.CustomerProfiles
                    .FirstOrDefaultAsync(candidate => candidate.CustomerId == customerId, ct)
                    .ConfigureAwait(false);

                var now = clock.GetUtcNow();
                var before = profile is null ? null : CustomerProfileSnapshot.Of(profile);

                if (profile is null)
                {
                    profile = CustomerProfile.Default(customerId, now);
                    database.CustomerProfiles.Add(profile);
                }

                profile.Update(
                    input.MailingAddress,
                    input.BillDeliveryChannel,
                    input.OutageNotices,
                    input.DunningNotices,
                    input.PreferredLanguage,
                    now);

                // Invariant 1. `before` is null on the first save, which reads correctly: there was
                // no stored profile, and recording the defaults as though somebody had chosen them
                // would make the trail claim a decision nobody made.
                audit.Record(
                    AuditActions.CustomerProfileUpdated,
                    AuditEntityTypes.CustomerProfile,
                    customerId.ToString(),
                    before,
                    CustomerProfileSnapshot.Of(profile));

                var (serviceAddress, serviceLocationId) = await ServiceAddressAsync(customerId, ct).ConfigureAwait(false);

                return Resolve(customerId, profile, serviceAddress, serviceLocationId);
            },
            cancellationToken);
    }

    /// <summary>
    /// The premise post falls back to: the most recently active account's, or nothing when the
    /// customer holds no accounts.
    /// </summary>
    private async Task<(Address? Address, Guid? ServiceLocationId)> ServiceAddressAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var accounts = await database.ServiceAccounts
            .AsNoTracking()
            .Where(account => account.CustomerId == customerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordered in memory rather than in SQL: the rule is a three-part order over a computed
        // property (`HoldsPremise` is the state machine's answer, not a column), and a customer holds
        // a handful of accounts. Keeping it pure is what lets `ServiceAddressDefault` be argued with
        // in a unit test rather than through a database.
        var account = ServiceAddressDefault.MostRecentlyActive(accounts);

        if (account is null)
        {
            return (null, null);
        }

        var location = await database.ServiceLocations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == account.ServiceLocationId, cancellationToken)
            .ConfigureAwait(false);

        return (location?.Address, location?.Id);
    }

    private static CustomerProfileView Resolve(
        Guid customerId,
        CustomerProfile? profile,
        Address? serviceAddress,
        Guid? serviceLocationId)
    {
        var mailing = profile?.MailingAddress;

        var source = mailing is not null
            ? MailingAddressSource.Override
            : serviceAddress is not null
                ? MailingAddressSource.ServiceAddress
                : MailingAddressSource.None;

        return new CustomerProfileView(
            customerId,
            mailing ?? serviceAddress,
            source,
            serviceAddress,
            serviceLocationId,
            profile?.BillDeliveryChannel ?? BillDeliveryChannel.Post,
            profile?.OutageNotices ?? true,
            profile?.DunningNotices ?? true,
            profile?.PreferredLanguage ?? CommunicationLanguage.English,
            profile?.UpdatedAt);
    }

    /// <summary>
    /// Refuses a bill channel the utility could not use.
    /// </summary>
    /// <remarks>
    /// The customer's <b>own</b> email, not a contact's: a bill goes to the person who owes it, and
    /// a landlord recorded as a contact is somebody a rep may speak to, not somebody to send an
    /// invoice. Answer #1 of this work package's brief keeps <see cref="Customer.Email"/> as the
    /// customer's own primary detail, which is what makes that check one column deep.
    /// </remarks>
    private static void RequireDeliverable(Customer customer, BillDeliveryChannel channel)
    {
        if (channel is BillDeliveryChannel.Post || !string.IsNullOrWhiteSpace(customer.Email))
        {
            return;
        }

        throw new RegistryValidationException(
            $"{customer.AccountNumber} has no email address, so bills cannot be delivered by {channel}. "
            + "Record an email on the customer first, or leave delivery on Post.");
    }

    private async Task<Customer> RequireCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        await database.Customers.AsNoTracking().FirstOrDefaultAsync(customer => customer.Id == customerId, cancellationToken).ConfigureAwait(false)
        ?? throw new CustomerNotFoundException(customerId);
}

/// <summary>
/// The before/after shape a profile is audited as. A dedicated record rather than the entity, and
/// the address is carried in parts rather than as one line — "which part of the address changed" is
/// the question a returned-post dispute asks.
/// </summary>
/// <param name="CustomerId">Whose profile.</param>
/// <param name="MailingAddress">The override, or <see langword="null"/> while post follows the service address.</param>
/// <param name="BillDeliveryChannel">How the bill reached them.</param>
/// <param name="OutageNotices">Whether outage notices were wanted.</param>
/// <param name="DunningNotices">Whether dunning notices were wanted.</param>
/// <param name="PreferredLanguage">The language they were written to in.</param>
public sealed record CustomerProfileSnapshot(
    Guid CustomerId,
    MailingAddressSnapshot? MailingAddress,
    BillDeliveryChannel BillDeliveryChannel,
    bool OutageNotices,
    bool DunningNotices,
    CommunicationLanguage PreferredLanguage)
{
    /// <summary>Takes a snapshot of <paramref name="profile"/> as it stands.</summary>
    public static CustomerProfileSnapshot Of(CustomerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new CustomerProfileSnapshot(
            profile.CustomerId,
            MailingAddressSnapshot.Of(profile.MailingAddress),
            profile.BillDeliveryChannel,
            profile.OutageNotices,
            profile.DunningNotices,
            profile.PreferredLanguage);
    }
}

/// <summary>An address as the audit trail holds it.</summary>
/// <param name="Line1">Street address.</param>
/// <param name="Line2">Unit, floor or building.</param>
/// <param name="City">Town or village.</param>
/// <param name="Region">State, province or island.</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="Country">Country.</param>
public sealed record MailingAddressSnapshot(
    string Line1,
    string? Line2,
    string City,
    string Region,
    string? PostalCode,
    string Country)
{
    /// <summary>Takes a snapshot of <paramref name="address"/>, or <see langword="null"/> if there is none.</summary>
    public static MailingAddressSnapshot? Of(Address? address) =>
        address is null
            ? null
            : new MailingAddressSnapshot(
                address.Line1,
                address.Line2,
                address.City,
                address.Region,
                address.PostalCode,
                address.Country);
}
