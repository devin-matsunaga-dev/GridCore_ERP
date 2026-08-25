using GridCore.Modules.Customers.Features.Search;

namespace GridCore.Modules.Customers.UnitTests.Search;

/// <summary>
/// The normalisations every comparison in the CSR search box runs both sides through. Pure, so
/// these are the fastest tests in the work package and the ones that hold the interesting rules.
/// </summary>
public class SearchTextTests
{
    [Theory]
    [InlineData("  Ana   CRUZ ", "ana cruz")]
    [InlineData("Cruz", "cruz")]
    [InlineData("\tSongsong\n", "songsong")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalising_lower_cases_and_collapses_whitespace(string? input, string expected) =>
        Assert.Equal(expected, SearchText.Normalise(input));

    [Theory]
    [InlineData("(670) 285-1234", "6702851234")]
    [InlineData("670.285.1234", "6702851234")]
    [InlineData("+1 670 285 1234", "16702851234")]
    [InlineData("670/285/1234", "6702851234")]
    [InlineData("no digits here", "")]
    [InlineData(null, "")]
    public void A_phone_compares_as_its_digits_alone(string? input, string expected) =>
        Assert.Equal(expected, SearchText.Digits(input));

    [Fact]
    public void Every_character_listed_as_phone_punctuation_is_actually_dropped() =>
        // The list is what the SQL side is built from, so a character on it that Digits kept would be
        // a character the two halves disagreed about.
        Assert.Equal(
            string.Empty,
            SearchText.Digits(string.Concat(SearchText.PhonePunctuation)));

    [Theory]
    [InlineData("12 Beach St", "12 beach street")]
    [InlineData("12 Beach Street", "12 beach street")]
    [InlineData("12 Beach St.", "12 beach street")]
    [InlineData("12  BEACH   ST", "12 beach street")]
    [InlineData("45 As Matuis Rd", "45 as matuis road")]
    [InlineData("45 As Matuis Road", "45 as matuis road")]
    [InlineData("9 Tatachog Ave, Apt 4", "9 tatachog avenue apartment 4")]
    [InlineData("9 Tatachog Avenue Apartment 4", "9 tatachog avenue apartment 4")]
    public void An_abbreviated_street_type_normalises_to_the_written_out_one(string input, string expected) =>
        Assert.Equal(expected, SearchText.NormaliseAddress(input));

    [Fact]
    public void St_and_Saint_are_one_equivalence_class_on_purpose()
    {
        // The canonical token is arbitrary and never shown; what matters is that the two spellings
        // compare equal, so a rep who types "St Joseph" finds "Saint Joseph" and the other way about.
        // The cost is a slight over-match — "Saint Joseph Street" also equals "St Joseph St" — which
        // is harmless, because the row renders the address as it is stored.
        Assert.Equal(
            SearchText.NormaliseAddress("St Joseph Rd"),
            SearchText.NormaliseAddress("Saint Joseph Road"));

        Assert.Equal(
            SearchText.NormaliseAddress("12 Beach St"),
            SearchText.NormaliseAddress("12 Beach Street"));
    }

    [Fact]
    public void Punctuation_separates_tokens_rather_than_joining_them() =>
        Assert.Equal(
            SearchText.NormaliseAddress("12 Beach St, Songsong, Rota"),
            SearchText.NormaliseAddress("12 Beach Street  Songsong   Rota"));

    [Theory]
    [InlineData("12 Beach St", "beach")]
    [InlineData("12 Beach Street", "beach")]
    [InlineData("As Matuis Rd", "matuis")]
    [InlineData("Songsong", "songsong")]
    public void The_narrowing_token_is_the_longest_word_that_survives_normalisation(string input, string expected) =>
        // The candidate query runs against the stored columns, which are not normalised. So it can
        // only be narrowed by a token that means the same on both sides — which rules out every
        // abbreviation and every bare number.
        Assert.Equal(expected, SearchText.MostSelectiveToken(input));

    [Fact]
    public void A_house_number_narrows_badly_but_it_is_better_than_nothing() =>
        // It does at least appear in the stored column, and the second stage re-checks every
        // candidate properly anyway.
        Assert.Equal("12", SearchText.MostSelectiveToken("12 St"));

    [Fact]
    public void A_street_type_is_never_the_fallback_token() =>
        // "St" normalises to "street", a word the stored address may not contain at all — narrowing
        // by it would find nothing and look like a search that was working.
        Assert.Equal(string.Empty, SearchText.MostSelectiveToken("St"));

    [Fact]
    public void There_is_no_token_to_narrow_an_empty_address_by() =>
        Assert.Equal(string.Empty, SearchText.MostSelectiveToken("  ,  "));
}
