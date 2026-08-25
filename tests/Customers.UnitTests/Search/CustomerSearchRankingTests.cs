using GridCore.Modules.Customers.Features.Search;

namespace GridCore.Modules.Customers.UnitTests.Search;

/// <summary>
/// Which answer is the best answer. Pure, so "an exact account-number hit comes first" is a fast
/// test rather than a claim about SQL.
/// </summary>
public class CustomerSearchRankingTests
{
    private static readonly Guid Ana = Guid.Parse("019196c0-0000-7000-8000-00000000000a");
    private static readonly Guid Ben = Guid.Parse("019196c0-0000-7000-8000-00000000000b");
    private static readonly Guid Cara = Guid.Parse("019196c0-0000-7000-8000-00000000000c");

    private static CustomerSearchCandidate Candidate(
        Guid customer,
        CustomerMatchKind kind,
        bool exact = false,
        string value = "value") =>
        new(customer, kind, exact, value);

    [Fact]
    public void An_exact_account_number_hit_comes_first()
    {
        var ranked = CustomerSearchRanking.Rank(
        [
            Candidate(Ana, CustomerMatchKind.Name, exact: true, value: "Ana Cruz"),
            Candidate(Ben, CustomerMatchKind.AccountNumber, exact: true, value: "C-000012"),
            Candidate(Cara, CustomerMatchKind.Phone, exact: true, value: "670-285-1234"),
        ]);

        Assert.Equal(Ben, ranked[0].CustomerId);
        Assert.Equal(CustomerMatchKind.AccountNumber, ranked[0].Kind);
    }

    [Fact]
    public void Precedence_outranks_exactness()
    {
        // A partial account-number match beats an exact address one, deliberately: somebody typing an
        // account number is telling you which account they mean, and somebody typing a street is not.
        var ranked = CustomerSearchRanking.Rank(
        [
            Candidate(Ana, CustomerMatchKind.Address, exact: true, value: "12 Beach St"),
            Candidate(Ben, CustomerMatchKind.AccountNumber, exact: false, value: "C-000120"),
        ]);

        Assert.Equal(Ben, ranked[0].CustomerId);
    }

    [Fact]
    public void Exact_beats_partial_within_one_kind()
    {
        var ranked = CustomerSearchRanking.Rank(
        [
            Candidate(Ana, CustomerMatchKind.Name, exact: false, value: "Ana Cruz Holdings"),
            Candidate(Ben, CustomerMatchKind.Name, exact: true, value: "Zeta Cruz"),
        ]);

        Assert.Equal(Ben, ranked[0].CustomerId);
    }

    [Fact]
    public void The_full_precedence_order_is_account_meter_phone_name_address()
    {
        var ranked = CustomerSearchRanking.Rank(
        [
            Candidate(Guid.CreateVersion7(), CustomerMatchKind.Address),
            Candidate(Guid.CreateVersion7(), CustomerMatchKind.Name),
            Candidate(Guid.CreateVersion7(), CustomerMatchKind.Phone),
            Candidate(Guid.CreateVersion7(), CustomerMatchKind.MeterNumber),
            Candidate(Guid.CreateVersion7(), CustomerMatchKind.AccountNumber),
        ]);

        Assert.Equal(
            [
                CustomerMatchKind.AccountNumber,
                CustomerMatchKind.MeterNumber,
                CustomerMatchKind.Phone,
                CustomerMatchKind.Name,
                CustomerMatchKind.Address,
            ],
            ranked.Select(candidate => candidate.Kind).ToList());
    }

    [Fact]
    public void A_customer_appears_once_however_many_ways_they_matched()
    {
        // Two meters at two premises, plus the name — one rep, one row, and the row says the best
        // reason rather than the first one found.
        var ranked = CustomerSearchRanking.Rank(
        [
            Candidate(Ana, CustomerMatchKind.Name, exact: true, value: "Ana Cruz"),
            Candidate(Ana, CustomerMatchKind.MeterNumber, exact: false, value: "MTR-000120"),
            Candidate(Ana, CustomerMatchKind.MeterNumber, exact: true, value: "MTR-000012"),
        ]);

        var only = Assert.Single(ranked);

        Assert.Equal(CustomerMatchKind.MeterNumber, only.Kind);
        Assert.True(only.IsExact);
        Assert.Equal("MTR-000012", only.MatchedValue);
    }

    [Fact]
    public void Rows_of_equal_standing_order_by_the_value_that_matched()
    {
        // Names alphabetically, account numbers numerically, addresses down the street — one rule
        // that reads correctly for every kind.
        var ranked = CustomerSearchRanking.Rank(
        [
            Candidate(Ana, CustomerMatchKind.Name, value: "Zeta Cruz"),
            Candidate(Ben, CustomerMatchKind.Name, value: "Ana Cruz"),
            Candidate(Cara, CustomerMatchKind.Name, value: "Manuel Cruz"),
        ]);

        Assert.Equal(["Ana Cruz", "Manuel Cruz", "Zeta Cruz"], ranked.Select(candidate => candidate.MatchedValue).ToList());
    }

    [Fact]
    public void The_order_is_total_so_paging_cannot_show_or_skip_a_row_twice()
    {
        // Same kind, same exactness, same matched value: without the id tie-break the order would be
        // whatever the database happened to return, and page two could repeat a row from page one.
        var candidates = new[]
        {
            Candidate(Cara, CustomerMatchKind.Name, value: "Cruz Family"),
            Candidate(Ana, CustomerMatchKind.Name, value: "Cruz Family"),
            Candidate(Ben, CustomerMatchKind.Name, value: "Cruz Family"),
        };

        Assert.Equal(
            [Ana, Ben, Cara],
            CustomerSearchRanking.Rank(candidates).Select(candidate => candidate.CustomerId).ToList());

        Assert.Equal(
            CustomerSearchRanking.Rank(candidates).Select(candidate => candidate.CustomerId).ToList(),
            CustomerSearchRanking.Rank(candidates.Reverse()).Select(candidate => candidate.CustomerId).ToList());
    }

    [Fact]
    public void Nothing_in_ranks_to_nothing_out() =>
        Assert.Empty(CustomerSearchRanking.Rank([]));
}
