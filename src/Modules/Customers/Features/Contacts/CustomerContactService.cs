using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Contacts;

/// <summary>What a caller supplies to add a contact.</summary>
/// <param name="Name">Who they are.</param>
/// <param name="Relationship">What they are to the customer.</param>
/// <param name="IsAuthorisedToDiscuss">Whether a rep may discuss the account with them.</param>
/// <param name="Methods">How to reach them, if any are known already.</param>
public sealed record AddCustomerContactInput(
    string Name,
    string? Relationship = null,
    bool IsAuthorisedToDiscuss = false,
    IReadOnlyList<AddContactMethodInput>? Methods = null);

/// <summary>What a caller supplies to correct a contact.</summary>
/// <param name="Name">Who they are.</param>
/// <param name="Relationship">What they are to the customer.</param>
/// <param name="IsAuthorisedToDiscuss">Whether a rep may discuss the account with them.</param>
public sealed record UpdateCustomerContactInput(
    string Name,
    string? Relationship,
    bool IsAuthorisedToDiscuss);

/// <summary>What a caller supplies to record a way of reaching a contact.</summary>
/// <param name="Kind">Phone, mobile or email.</param>
/// <param name="Value">The number or address.</param>
/// <param name="IsPrimary">Whether it takes the primary place for its kind, demoting whatever held it.</param>
public sealed record AddContactMethodInput(ContactMethodKind Kind, string Value, bool IsPrimary = false);

/// <summary>The contacts a customer's account may be discussed with. The module's own surface.</summary>
public interface ICustomerContactService
{
    /// <summary>Every contact on a customer, with their methods. Oldest first.</summary>
    Task<IReadOnlyList<CustomerContact>> ListAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>One contact, or <see langword="null"/> if there is no such id.</summary>
    Task<CustomerContact?> FindAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>Adds a contact to a customer.</summary>
    Task<CustomerContact> AddAsync(Guid customerId, AddCustomerContactInput input, CancellationToken cancellationToken = default);

    /// <summary>Corrects a contact's details, and grants or withdraws their right to discuss the account.</summary>
    Task<CustomerContact> UpdateAsync(Guid contactId, UpdateCustomerContactInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes a contact and every method on it.</summary>
    Task RemoveAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>Records another way of reaching a contact.</summary>
    Task<CustomerContact> AddMethodAsync(Guid contactId, AddContactMethodInput input, CancellationToken cancellationToken = default);

    /// <summary>Corrects a method's number or address.</summary>
    Task<CustomerContact> CorrectMethodAsync(Guid contactId, Guid methodId, string value, CancellationToken cancellationToken = default);

    /// <summary>Makes a method the one to try first for its kind, demoting whichever held that place.</summary>
    Task<CustomerContact> MakeMethodPrimaryAsync(Guid contactId, Guid methodId, CancellationToken cancellationToken = default);

    /// <summary>Drops a method, handing the primary place on if it held it.</summary>
    Task<CustomerContact> RemoveMethodAsync(Guid contactId, Guid methodId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The contact register over the customers schema.
/// </summary>
/// <remarks>
/// <para>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls <c>SaveChanges</c>
/// itself, so the contact row and its audit entry commit together — invariant 1, the same shape
/// <c>CustomerService</c> follows. There are no events: a contact is a fact about how to reach
/// somebody and no other module acts on one, so publishing would be inventing a consumer.
/// </para>
/// <para>
/// <b>The authorised-to-discuss gate lives here, not on the route</b> (WP-2.11, owner's call).
/// Marking somebody authorised is a disclosure decision, so it needs
/// <see cref="Permissions.Customers.Authorise"/> on top of the <see cref="Permissions.Customers.Write"/>
/// the route already demands — but only when the flag actually <i>moves</i>. Whether a request moves
/// it is a fact about the body compared against what is stored, which routing cannot see; and a rep
/// without the permission must still be able to correct a spelling on a contact somebody else
/// authorised. This is the shape WP-2.8's deposit gate established, for the same reason.
/// </para>
/// </remarks>
public sealed class CustomerContactService(
    CustomersDbContext database,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : ICustomerContactService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CustomerContact>> ListAsync(Guid customerId, CancellationToken cancellationToken = default) =>
        await Contacts()
            .AsNoTracking()
            .Where(contact => contact.CustomerId == customerId)
            .OrderBy(contact => contact.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<CustomerContact?> FindAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        Contacts().AsNoTracking().FirstOrDefaultAsync(contact => contact.Id == contactId, cancellationToken);

    /// <inheritdoc />
    public Task<CustomerContact> AddAsync(Guid customerId, AddCustomerContactInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                // A contact against a customer who does not exist is an orphan row nothing will ever
                // read, so it is a 404 rather than a foreign-key error at commit time.
                if (!await database.Customers.AnyAsync(customer => customer.Id == customerId, ct).ConfigureAwait(false))
                {
                    throw new CustomerNotFoundException(customerId);
                }

                if (input.IsAuthorisedToDiscuss)
                {
                    RequireAuthorisePermission();
                }

                var now = clock.GetUtcNow();
                var contact = CustomerContact.Add(customerId, input.Name, input.Relationship, now);

                foreach (var method in input.Methods ?? [])
                {
                    contact.AddMethod(method.Kind, method.Value, method.IsPrimary, now);
                }

                if (input.IsAuthorisedToDiscuss)
                {
                    contact.SetAuthorisedToDiscuss(true);
                }

                database.CustomerContacts.Add(contact);

                audit.Record(
                    AuditActions.CustomerContactCreated,
                    AuditEntityTypes.CustomerContact,
                    contact.Id.ToString(),
                    before: null,
                    after: CustomerContactSnapshot.Of(contact));

                if (input.IsAuthorisedToDiscuss)
                {
                    RecordAuthorisation(contact);
                }

                return contact;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CustomerContact> UpdateAsync(Guid contactId, UpdateCustomerContactInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return MutateAsync(
            contactId,
            contact =>
            {
                // Only a MOVE needs the permission. A rep correcting a misspelt name on a contact
                // somebody else authorised is not making a disclosure decision, and refusing them
                // would make the narrower permission a broader one in practice.
                var moved = contact.IsAuthorisedToDiscuss != input.IsAuthorisedToDiscuss;

                if (moved)
                {
                    RequireAuthorisePermission();
                }

                contact.UpdateDetails(input.Name, input.Relationship);
                contact.SetAuthorisedToDiscuss(input.IsAuthorisedToDiscuss);

                return moved;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var contact = await RequireContactAsync(contactId, ct).ConfigureAwait(false);

                database.CustomerContacts.Remove(contact);

                // The trail keeps what was removed: "who was on this account before" is exactly the
                // question a dispute asks, and a deleted row cannot answer it.
                audit.Record(
                    AuditActions.CustomerContactRemoved,
                    AuditEntityTypes.CustomerContact,
                    contact.Id.ToString(),
                    before: CustomerContactSnapshot.Of(contact),
                    after: null);

                return contact;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<CustomerContact> AddMethodAsync(Guid contactId, AddContactMethodInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return MutateAsync(
            contactId,
            contact =>
            {
                contact.AddMethod(input.Kind, input.Value, input.IsPrimary, clock.GetUtcNow());

                return false;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CustomerContact> CorrectMethodAsync(Guid contactId, Guid methodId, string value, CancellationToken cancellationToken = default) =>
        MutateAsync(
            contactId,
            contact =>
            {
                contact.CorrectMethod(methodId, value);

                return false;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<CustomerContact> MakeMethodPrimaryAsync(Guid contactId, Guid methodId, CancellationToken cancellationToken = default) =>
        MutateAsync(
            contactId,
            contact =>
            {
                contact.MakeMethodPrimary(methodId);

                return false;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<CustomerContact> RemoveMethodAsync(Guid contactId, Guid methodId, CancellationToken cancellationToken = default) =>
        MutateAsync(
            contactId,
            contact =>
            {
                contact.RemoveMethod(methodId);

                return false;
            },
            cancellationToken);

    /// <summary>
    /// Loads the contact, applies <paramref name="mutate"/> and audits the before/after.
    /// </summary>
    /// <remarks>
    /// A method change is audited against the CONTACT, not as an entity of its own — the shape
    /// <c>AuditEntityTypes</c> already states for a meter's history lines and a bill's adjustments.
    /// The question somebody asks of this trail is "what happened to this contact", and a snapshot
    /// carrying the whole method set answers it in one entry, including which one is primary.
    /// <paramref name="mutate"/> answers whether the disclosure flag moved, which earns a second entry.
    /// </remarks>
    private Task<CustomerContact> MutateAsync(Guid contactId, Func<CustomerContact, bool> mutate, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var contact = await RequireContactAsync(contactId, ct).ConfigureAwait(false);
                var before = CustomerContactSnapshot.Of(contact);

                var authorisationMoved = mutate(contact);

                audit.Record(
                    AuditActions.CustomerContactUpdated,
                    AuditEntityTypes.CustomerContact,
                    contact.Id.ToString(),
                    before,
                    CustomerContactSnapshot.Of(contact));

                if (authorisationMoved)
                {
                    RecordAuthorisation(contact);
                }

                return contact;
            },
            cancellationToken);

    private async Task<CustomerContact> RequireContactAsync(Guid contactId, CancellationToken cancellationToken) =>
        await Contacts().FirstOrDefaultAsync(contact => contact.Id == contactId, cancellationToken).ConfigureAwait(false)
        ?? throw new CustomerContactNotFoundException(contactId);

    private IQueryable<CustomerContact> Contacts() =>
        database.CustomerContacts.Include(contact => contact.Methods);

    private void RequireAuthorisePermission()
    {
        if (currentUser.HasPermission(Permissions.Customers.Authorise))
        {
            return;
        }

        throw new RegistryPermissionException(
            $"Marking a contact authorised to discuss the account requires the '{Permissions.Customers.Authorise}' permission. "
            + "Record the contact without it and have somebody who holds it authorise them.");
    }

    /// <summary>
    /// Invariant 5: a sensitive act is permission-gated AND audited in its own right. The contact's
    /// own entry already carries the flag in its before/after; this one exists so "who was given the
    /// right to discuss this account, and when" is a filter on an action name rather than a diff
    /// somebody has to read every update entry to find.
    /// </summary>
    private void RecordAuthorisation(CustomerContact contact) =>
        audit.Record(
            AuditActions.CustomerContactAuthorised,
            AuditEntityTypes.CustomerContact,
            contact.Id.ToString(),
            before: null,
            after: new ContactAuthorisationSnapshot(
                contact.Id,
                contact.CustomerId,
                contact.Name,
                contact.IsAuthorisedToDiscuss));
}

/// <summary>
/// The before/after shape a contact is audited as — a dedicated record rather than the entity, so
/// changing the entity later cannot silently change the meaning of historic audit entries.
/// </summary>
/// <param name="Id">Which contact.</param>
/// <param name="CustomerId">Whose account they are a contact on.</param>
/// <param name="Name">Who they are.</param>
/// <param name="Relationship">What they are to the customer.</param>
/// <param name="IsAuthorisedToDiscuss">Whether the account may be discussed with them.</param>
/// <param name="Methods">How to reach them, at the time of the snapshot.</param>
public sealed record CustomerContactSnapshot(
    Guid Id,
    Guid CustomerId,
    string Name,
    string? Relationship,
    bool IsAuthorisedToDiscuss,
    IReadOnlyList<ContactMethodSnapshot> Methods)
{
    /// <summary>Takes a snapshot of <paramref name="contact"/> as it stands.</summary>
    public static CustomerContactSnapshot Of(CustomerContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        return new CustomerContactSnapshot(
            contact.Id,
            contact.CustomerId,
            contact.Name,
            contact.Relationship,
            contact.IsAuthorisedToDiscuss,
            [.. contact.Methods.Select(ContactMethodSnapshot.Of)]);
    }
}

/// <summary>One line of a contact's method set, as the audit trail holds it.</summary>
/// <param name="Id">Which method.</param>
/// <param name="Kind">Phone, mobile or email.</param>
/// <param name="Value">The number or address.</param>
/// <param name="IsPrimary">Whether it was the one to try first for its kind.</param>
public sealed record ContactMethodSnapshot(Guid Id, ContactMethodKind Kind, string Value, bool IsPrimary)
{
    /// <summary>Takes a snapshot of <paramref name="method"/> as it stands.</summary>
    public static ContactMethodSnapshot Of(ContactMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);

        return new ContactMethodSnapshot(method.Id, method.Kind, method.Value, method.IsPrimary);
    }
}

/// <summary>What the disclosure entry records: who was granted or refused the right, and by implication when.</summary>
/// <param name="Id">Which contact.</param>
/// <param name="CustomerId">Whose account.</param>
/// <param name="Name">Who they are.</param>
/// <param name="IsAuthorisedToDiscuss">Where the flag landed.</param>
public sealed record ContactAuthorisationSnapshot(Guid Id, Guid CustomerId, string Name, bool IsAuthorisedToDiscuss);
