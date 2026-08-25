namespace GridCore.Modules.Customers.Features.Search;

/// <summary>
/// What a search result matched on — the label a row carries, so a rep can see <i>why</i> a
/// customer came back rather than guessing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declaration order is match precedence</b> (owner's call): account number, then meter number,
/// then phone, then name, then address. Stable identifiers outrank descriptive fields, which is how
/// every utility and CRM desk works — somebody who quotes an account number knows which account
/// they mean, and somebody who types a name does not. <see cref="CustomerSearchRanking"/> sorts on
/// the enum value itself, so reordering these members reorders every result list in the product.
/// </para>
/// <para>
/// Stored nowhere and never persisted: this is a fact about one answer to one query, not about a
/// customer. That is why it may be ordered by declaration without WP-0.4's stored-enum rule
/// applying to it.
/// </para>
/// </remarks>
public enum CustomerMatchKind
{
    /// <summary>The customer's own account number, e.g. <c>C-000012</c>.</summary>
    AccountNumber,

    /// <summary>The number on a meter fitted at a premise this customer is served at.</summary>
    MeterNumber,

    /// <summary>The telephone number on the customer record, compared with punctuation stripped.</summary>
    Phone,

    /// <summary>The customer's name.</summary>
    Name,

    /// <summary>The service address of a premise this customer is served at.</summary>
    Address,
}
