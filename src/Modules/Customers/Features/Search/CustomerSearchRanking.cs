namespace GridCore.Modules.Customers.Features.Search;

/// <summary>
/// One reason a customer came back from a search: what matched, whether it matched exactly, and the
/// context worth showing on the row beside it.
/// </summary>
/// <remarks>
/// A customer can produce several of these in one search — a name match and an address match, or
/// two meters at two premises. <see cref="CustomerSearchRanking"/> is what reduces them to the one
/// the rep should be told about.
/// </remarks>
/// <param name="CustomerId">Who matched.</param>
/// <param name="Kind">Which field matched.</param>
/// <param name="IsExact">Whether the whole field matched, rather than part of it.</param>
/// <param name="MatchedValue">The stored value that matched, as stored — never the normalised form.</param>
/// <param name="ServiceAccountNumber">The account the match came through, where it came through one.</param>
/// <param name="ServiceAddress">The premise the match came through, where it came through one.</param>
/// <param name="MeterNumber">The meter the match came through, where it came through one.</param>
public sealed record CustomerSearchCandidate(
    Guid CustomerId,
    CustomerMatchKind Kind,
    bool IsExact,
    string MatchedValue,
    string? ServiceAccountNumber = null,
    string? ServiceAddress = null,
    string? MeterNumber = null);

/// <summary>
/// Turns the candidates the stage-one queries gathered into the ordered, one-row-per-customer list
/// a rep reads.
/// </summary>
/// <remarks>
/// <para>
/// Pure and total: no database, no services, no clock. Everything about <i>which answer is the best
/// answer</i> is decided here, which is what makes "an exact account-number hit comes first" a fast
/// test rather than a claim about SQL.
/// </para>
/// <para>
/// The order is <b>kind precedence, then exact before partial, then the matched value, then the
/// id</b>. Precedence outranks exactness on purpose: an exact address match is still a worse answer
/// than a partial account-number one, because somebody typing an account number is telling you
/// which account they mean and somebody typing a street is not. Matched value orders within a kind,
/// which reads correctly for every kind — names alphabetically, account and meter numbers
/// numerically, addresses down the street — and the id is the tie-break that makes the whole order
/// total, so paging can never show or skip the same row twice.
/// </para>
/// </remarks>
public static class CustomerSearchRanking
{
    /// <summary>
    /// The best candidate for each customer, in the order a result list shows them.
    /// </summary>
    public static IReadOnlyList<CustomerSearchCandidate> Rank(IEnumerable<CustomerSearchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .GroupBy(candidate => candidate.CustomerId)

            // Ordered before First rather than reduced with a Min, so the choice inside a group uses
            // exactly the comparison the list is sorted by. Two ways of saying "best" is one way too
            // many.
            .Select(group => group.Order(Comparer).First())
            .Order(Comparer)
            .ToList();
    }

    /// <summary>
    /// The one comparison, shared by the choice within a customer and the order of the list.
    /// </summary>
    /// <remarks>
    /// A comparer rather than a tuple key, because the string leg has to be <b>ordinal</b>: a
    /// culture-sensitive comparison would order a result list differently on a server whose locale
    /// somebody changed, and paging over an order that is not stable shows and skips rows at random.
    /// </remarks>
    private static readonly IComparer<CustomerSearchCandidate> Comparer =
        Comparer<CustomerSearchCandidate>.Create(static (left, right) =>
        {
            var byKind = left.Kind.CompareTo(right.Kind);
            if (byKind is not 0)
            {
                return byKind;
            }

            // Exact first, so the false that means "exact" has to sort ahead of the true.
            var byExactness = (!left.IsExact).CompareTo(!right.IsExact);
            if (byExactness is not 0)
            {
                return byExactness;
            }

            var byValue = string.Compare(left.MatchedValue, right.MatchedValue, StringComparison.OrdinalIgnoreCase);

            return byValue is not 0 ? byValue : left.CustomerId.CompareTo(right.CustomerId);
        });
}
