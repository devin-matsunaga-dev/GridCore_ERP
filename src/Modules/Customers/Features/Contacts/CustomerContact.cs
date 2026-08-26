using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Contacts;

/// <summary>
/// Somebody a rep may speak to about a customer's account — a spouse, a landlord, a property
/// manager, the office that pays the bills — with the numbers and addresses that reach them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a replacement for the customer's own details.</b> <see cref="Customer.ContactName"/>,
/// <see cref="Customer.Email"/> and <see cref="Customer.Phone"/> stay exactly what they were: who
/// to ask for and how to reach the customer themselves. These are the <i>additional</i> people, and
/// the WP-2.9 search still matches against the customer's own columns — a decision recorded in
/// DECISIONS.md, and the reason this is a separate table rather than a migration of those three.
/// </para>
/// <para>
/// <b>An aggregate root, not a child of <see cref="Customer"/>.</b> It names a customer by id, the
/// way <c>ServiceAccount</c> does, so nothing that reads a customer today has to start including a
/// collection it does not want — the registry list, the search's two-stage narrow and the 360's
/// customer query are untouched by this work package.
/// </para>
/// <para>
/// <b>The methods are its children and only its children.</b> Every rule about them — one primary
/// per kind, no duplicate value within a kind, a removed primary handing over rather than leaving
/// the kind headless — is enforced here, because the rules are about the <i>set</i> and no single
/// method can see the set.
/// </para>
/// </remarks>
public sealed class CustomerContact
{
    /// <summary>Longest contact name stored.</summary>
    public const int NameLength = Customer.NameLength;

    /// <summary>Longest relationship stored.</summary>
    public const int RelationshipLength = 64;

    private readonly List<ContactMethod> _methods = [];

    private CustomerContact()
    {
        // EF materialisation.
        Name = string.Empty;
    }

    /// <summary>Identifier of this contact. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The customer whose account this contact may be spoken to about.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>Who they are.</summary>
    public string Name { get; private set; }

    /// <summary>
    /// What they are to the customer — spouse, landlord, site manager. Free text rather than a
    /// declared list: the relationships a utility meets are open-ended, and a rep forced to file a
    /// caller's brother-in-law under "Other" has recorded less than the word they would have typed.
    /// WP-2.13's interaction types are a fixed list because a report counts them; nobody counts these.
    /// </summary>
    public string? Relationship { get; private set; }

    /// <summary>
    /// Whether a rep may discuss the account with this person.
    /// </summary>
    /// <remarks>
    /// <b>Off until somebody with the permission says otherwise.</b> The flag is the utility's
    /// answer to "may I tell this caller what the bill is", so its safe value is no. The gate is in
    /// <c>CustomerContactService</c> rather than here: the aggregate cannot see who is asking, and a
    /// seeder or a migration writing a contact is not a rep making a disclosure decision.
    /// </remarks>
    public bool IsAuthorisedToDiscuss { get; private set; }

    /// <summary>When the contact was added.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>How to reach them, oldest first.</summary>
    public IReadOnlyList<ContactMethod> Methods => _methods;

    /// <summary>Adds a contact against <paramref name="customerId"/>.</summary>
    /// <exception cref="RegistryValidationException">The customer id is empty, or the name is blank.</exception>
    public static CustomerContact Add(Guid customerId, string name, string? relationship, DateTimeOffset now)
    {
        if (customerId == Guid.Empty)
        {
            throw new RegistryValidationException("'customerId' is required to add a contact.");
        }

        return new CustomerContact
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customerId,
            Name = RequireName(name),
            Relationship = RegistryText.Clean(relationship, RelationshipLength),
            RecordedAt = now,
        };
    }

    /// <summary>Corrects who the contact is and what they are to the customer.</summary>
    /// <exception cref="RegistryValidationException">The name is blank.</exception>
    public void UpdateDetails(string name, string? relationship)
    {
        // Guarded before the first assignment, so a rejected correction leaves the contact exactly
        // as it was rather than half-applied — the rule Customer.UpdateDetails already follows.
        var cleaned = RequireName(name);

        Name = cleaned;
        Relationship = RegistryText.Clean(relationship, RelationshipLength);
    }

    /// <summary>
    /// Grants or withdraws the right to discuss the account with this person. Whether the caller
    /// <i>may</i> is <c>CustomerContactService</c>'s question, not this one's.
    /// </summary>
    public void SetAuthorisedToDiscuss(bool authorised) => IsAuthorisedToDiscuss = authorised;

    /// <summary>
    /// Records another way of reaching this contact.
    /// </summary>
    /// <remarks>
    /// The first method of a kind is primary whether or not the caller asked for it. "Exactly one
    /// primary per kind" has to hold for a kind with one method too, and a lone number that nothing
    /// points at is how a screen ends up showing a contact with no telephone number beside their name.
    /// </remarks>
    /// <exception cref="RegistryValidationException">The kind is undeclared, the value is blank, or the contact already holds that value for that kind.</exception>
    public ContactMethod AddMethod(ContactMethodKind kind, string value, bool isPrimary, DateTimeOffset now)
    {
        var method = ContactMethod.For(Id, kind, value, now);

        RequireNotDuplicated(kind, method.Value, exceptId: null);

        _methods.Add(method);

        if (isPrimary || _methods.Count(existing => existing.Kind == kind) == 1)
        {
            MakePrimary(method);
        }

        return method;
    }

    /// <summary>Corrects a method's number or address — a mistyped digit, a changed mailbox.</summary>
    /// <exception cref="RegistryValidationException">The value is blank, or duplicates another method of the same kind.</exception>
    /// <exception cref="RegistryWorkflowException">This contact holds no such method.</exception>
    public ContactMethod CorrectMethod(Guid methodId, string value)
    {
        var method = RequireMethod(methodId);
        var cleaned = RegistryText.Clean(value, ContactMethod.MaxLengthFor(method.Kind))
            ?? throw new RegistryValidationException($"A {method.Kind} contact method needs a value.");

        RequireNotDuplicated(method.Kind, cleaned, exceptId: methodId);

        method.Correct(cleaned);

        return method;
    }

    /// <summary>
    /// Makes <paramref name="methodId"/> the one to try first for its kind, stepping down whichever
    /// method held that place. This is the promotion WORK_PACKAGES.md asks to be tested: promoting a
    /// second demotes the first, in one act, so there is no instant at which a kind has two.
    /// </summary>
    /// <exception cref="RegistryWorkflowException">This contact holds no such method.</exception>
    public ContactMethod MakeMethodPrimary(Guid methodId)
    {
        var method = RequireMethod(methodId);

        MakePrimary(method);

        return method;
    }

    /// <summary>
    /// Drops a method.
    /// </summary>
    /// <remarks>
    /// Removing the primary hands the place to the <b>oldest</b> method left of that kind rather
    /// than the newest: the longest-standing number is the one the utility has been reaching this
    /// person on, and a silent promotion of whatever was typed most recently is the surprising
    /// answer. A kind left with nothing has no primary, which is correct — the rule is one primary
    /// per kind the contact <i>has</i>.
    /// </remarks>
    /// <exception cref="RegistryWorkflowException">This contact holds no such method.</exception>
    public void RemoveMethod(Guid methodId)
    {
        var method = RequireMethod(methodId);

        _methods.Remove(method);

        if (!method.IsPrimary)
        {
            return;
        }

        // Ids are Guid v7, so ordering by id is ordering by the day it was recorded.
        var successor = _methods
            .Where(remaining => remaining.Kind == method.Kind)
            .OrderBy(remaining => remaining.Id)
            .FirstOrDefault();

        successor?.Promote();
    }

    /// <summary>The method to try first for <paramref name="kind"/>, or <see langword="null"/> if there is none of that kind.</summary>
    public ContactMethod? PrimaryFor(ContactMethodKind kind) =>
        _methods.FirstOrDefault(method => method.Kind == kind && method.IsPrimary);

    private void MakePrimary(ContactMethod method)
    {
        foreach (var sibling in _methods.Where(existing => existing.Kind == method.Kind && existing.Id != method.Id))
        {
            sibling.Demote();
        }

        method.Promote();
    }

    private ContactMethod RequireMethod(Guid methodId) =>
        _methods.FirstOrDefault(method => method.Id == methodId)
        ?? throw new RegistryWorkflowException($"Contact '{Id}' holds no contact method '{methodId}'.");

    private void RequireNotDuplicated(ContactMethodKind kind, string value, Guid? exceptId)
    {
        // Compared literally, case aside. Two spellings of one telephone number are a comparison
        // WP-2.9's SearchText makes for searching, and deliberately not one made here: a rep who
        // records the same number twice in two formats has made a mess, while a rep refused a
        // number because the punctuation resembles another one has been told a lie.
        var clash = _methods.Any(existing =>
            existing.Kind == kind
            && existing.Id != exceptId
            && string.Equals(existing.Value, value, StringComparison.OrdinalIgnoreCase));

        if (clash)
        {
            throw new RegistryValidationException($"This contact already has '{value}' as a {kind} contact method.");
        }
    }

    private static string RequireName(string name) =>
        RegistryText.Clean(name, NameLength)
        ?? throw new RegistryValidationException("'name' is required to add a contact.");
}
