using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Contacts;

/// <summary>
/// One way of reaching a contact — a number or an address — and whether it is the one to try first
/// for its kind.
/// </summary>
/// <remarks>
/// A child of <see cref="CustomerContact"/> and nothing else: every mutator is <c>internal</c> and
/// called only from the contact, which is what makes "exactly one primary per kind" enforceable at
/// all. A method that could be promoted from outside the aggregate would be a method that could be
/// promoted without demoting its sibling.
/// </remarks>
public sealed class ContactMethod
{
    /// <summary>Longest value stored. The email maximum, being the longest of the three kinds.</summary>
    public const int ValueLength = Customer.EmailLength;

    private ContactMethod()
    {
        // EF materialisation.
        Value = string.Empty;
    }

    /// <summary>Identifier of this method. Guid v7, so the key index already orders it by age.</summary>
    public Guid Id { get; private init; }

    /// <summary>The contact this method reaches.</summary>
    public Guid CustomerContactId { get; private init; }

    /// <summary>Phone, mobile or email.</summary>
    public ContactMethodKind Kind { get; private init; }

    /// <summary>The number or address, as the rep typed it — punctuation and all.</summary>
    public string Value { get; private set; }

    /// <summary>Whether this is the one to try first among the contact's methods of this kind.</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>When it was first recorded.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>The longest value a method of <paramref name="kind"/> may carry.</summary>
    /// <remarks>
    /// Per kind rather than one width for all, so a phone column cannot quietly accept 300
    /// characters of pasted signature block and a phone search cannot be handed one.
    /// </remarks>
    public static int MaxLengthFor(ContactMethodKind kind) =>
        kind is ContactMethodKind.Email ? Customer.EmailLength : Customer.PhoneLength;

    /// <summary>Records a method. Called only by <see cref="CustomerContact"/>.</summary>
    /// <exception cref="RegistryValidationException">The kind is not one GridCore declares, or the value is blank.</exception>
    internal static ContactMethod For(Guid customerContactId, ContactMethodKind kind, string value, DateTimeOffset now)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new RegistryValidationException($"'{kind}' is not a {nameof(ContactMethodKind)} GridCore declares.");
        }

        return new ContactMethod
        {
            Id = Guid.CreateVersion7(now),
            CustomerContactId = customerContactId,
            Kind = kind,
            Value = Clean(value, kind),
            RecordedAt = now,
        };
    }

    /// <summary>Corrects the number or address, leaving the kind and the primary flag alone.</summary>
    /// <exception cref="RegistryValidationException">The value is blank.</exception>
    internal void Correct(string value) => Value = Clean(value, Kind);

    /// <summary>Makes this the method to try first for its kind.</summary>
    internal void Promote() => IsPrimary = true;

    /// <summary>Steps this method down, because another of its kind was promoted.</summary>
    internal void Demote() => IsPrimary = false;

    private static string Clean(string value, ContactMethodKind kind) =>
        RegistryText.Clean(value, MaxLengthFor(kind))
        ?? throw new RegistryValidationException($"A {kind} contact method needs a value.");
}
