namespace GridCore.Modules.Customers.Features.Contacts;

/// <summary>
/// How a contact can be reached. Stored by name, never by ordinal.
/// </summary>
/// <remarks>
/// <b>Phone and Mobile are two kinds, not one with a flag.</b> "One primary per type" is the rule
/// WP-2.11 exists to enforce, and a contact with a landline and a mobile has a primary of each —
/// collapsing them would force a rep to choose which of two numbers that both ring is <i>the</i>
/// number, and would lose the distinction an outage SMS depends on.
/// </remarks>
public enum ContactMethodKind
{
    /// <summary>A number that rings a place — home, office, switchboard.</summary>
    Phone,

    /// <summary>A number that rings a person, and the only kind a text message can reach.</summary>
    Mobile,

    /// <summary>An email address.</summary>
    Email,
}
