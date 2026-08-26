namespace GridCore.Modules.Customers.Features.Profile;

/// <summary>
/// How a customer's bill reaches them. Stored by name, never by ordinal.
/// </summary>
/// <remarks>
/// <b>WP-2.11 records the preference and delivers nothing.</b> Nothing in GridCore posts or emails a
/// bill yet; this is the hook the billing-deepening pass reads when delivery is built, which is
/// exactly what WORK_PACKAGES.md asks of this package. A channel stored against a customer whose
/// bills are all collected at the counter is still the right answer to "what would we do if we sent
/// one".
/// </remarks>
public enum BillDeliveryChannel
{
    /// <summary>Printed and posted to the mailing address.</summary>
    Post,

    /// <summary>Emailed to the customer's email address.</summary>
    Email,

    /// <summary>Both — the customer wants the paper copy and the email.</summary>
    Both,
}

/// <summary>
/// The language a customer is written and spoken to in.
/// </summary>
/// <remarks>
/// A declared list rather than free text or a locale code: a preference nothing can render into is
/// worse than no preference, and the set of languages a utility actually produces a notice in is
/// small, deliberate and changes by decision rather than by typing. Adding one is a line here — the
/// column stores the name, so no migration follows.
/// </remarks>
public enum CommunicationLanguage
{
    /// <summary>English.</summary>
    English,

    /// <summary>Chamorro.</summary>
    Chamorro,

    /// <summary>Carolinian.</summary>
    Carolinian,
}
