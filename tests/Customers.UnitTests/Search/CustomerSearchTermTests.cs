using GridCore.Modules.Customers.Features.Search;

namespace GridCore.Modules.Customers.UnitTests.Search;

/// <summary>
/// What a rep typed, classified. The rule the whole search hangs off: an ambiguous term dispatches
/// <i>more</i> kinds rather than choosing one, because choosing wrongly means a rep who typed a real
/// account number occasionally getting nothing back.
/// </summary>
public class CustomerSearchTermTests
{
    private static IReadOnlyList<CustomerMatchKind> KindsOf(string? term) => CustomerSearchTerm.Classify(term).Kinds;

    [Theory]
    [InlineData("C-000012")]
    [InlineData("c-000012")]
    [InlineData("MTR-000007")]
    [InlineData("A-000003")]
    [InlineData("L-000009")]
    [InlineData("c12")]
    public void A_letter_prefixed_number_is_an_account_or_a_meter(string term) =>
        // GridCore issues no identifier without a letter prefix, which is what makes this decidable
        // rather than a guess. Both kinds, because the prefix is not checked here: precedence sorts
        // out which one answered.
        Assert.Equal([CustomerMatchKind.AccountNumber, CustomerMatchKind.MeterNumber], KindsOf(term));

    [Theory]
    [InlineData("6702851234")]
    [InlineData("(670) 285-1234")]
    [InlineData("670.285.1234")]
    [InlineData("+1 670 285 1234")]
    [InlineData("2851234")]
    public void A_long_enough_run_of_digits_is_a_telephone_number(string term) =>
        Assert.Equal([CustomerMatchKind.Phone], KindsOf(term));

    [Theory]
    [InlineData("12")]
    [InlineData("000012")]
    [InlineData("285-12")]
    public void A_short_run_of_digits_is_genuinely_ambiguous_and_is_dispatched_as_all_three(string term) =>
        // A rep with "12" in front of them means C-000012 far more often than somebody's telephone,
        // but not always — so all three are asked and precedence decides what comes first.
        Assert.Equal(
            [CustomerMatchKind.AccountNumber, CustomerMatchKind.MeterNumber, CustomerMatchKind.Phone],
            KindsOf(term));

    [Theory]
    [InlineData("Cruz")]
    [InlineData("ana cruz")]
    [InlineData("12 Beach St")]
    [InlineData("Songsong, Rota")]
    [InlineData("St Joseph Road")]
    public void Anything_with_a_letter_in_it_that_is_not_an_identifier_is_a_name_or_an_address(string term) =>
        Assert.Equal([CustomerMatchKind.Name, CustomerMatchKind.Address], KindsOf(term));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  , .  ")]
    public void An_empty_box_looks_for_nothing_and_is_not_an_error(string? term)
    {
        var classified = CustomerSearchTerm.Classify(term);

        Assert.True(classified.IsEmpty);
        Assert.Empty(classified.Kinds);
    }

    [Fact]
    public void Kinds_come_back_in_precedence_order_whatever_the_term()
    {
        // The ordering the ranker relies on. Asserted here as well because a classifier that returned
        // them the other way round would still pass every test above.
        foreach (var term in new[] { "C-000012", "12", "Cruz" })
        {
            var kinds = KindsOf(term);

            Assert.Equal(kinds.Order().ToList(), kinds);
        }
    }

    [Fact]
    public void The_term_carries_every_normalised_form_the_candidate_queries_need()
    {
        var classified = CustomerSearchTerm.Classify("  12 Beach St.  ");

        Assert.Equal("12 Beach St.", classified.Raw);
        Assert.Equal("12 beach st.", classified.Normalised);
        Assert.Equal("12", classified.Digits);
        Assert.Equal("12 beach street", classified.NormalisedAddress);
        Assert.Equal("beach", classified.AddressToken);
    }

    [Fact]
    public void A_long_word_with_a_number_after_it_is_not_a_registry_number() =>
        // The prefixes GridCore issues run to three letters and the rule allows four. Anything longer
        // is a word somebody typed with a number after it, and reading it as an identifier would lose
        // the name and address search that is the only thing that could answer it.
        Assert.Equal([CustomerMatchKind.Name, CustomerMatchKind.Address], KindsOf("house12"));
}
