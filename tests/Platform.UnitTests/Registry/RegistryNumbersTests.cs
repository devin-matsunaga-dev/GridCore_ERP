using GridCore.Platform.Registry;

namespace GridCore.Platform.UnitTests.Registry;

/// <summary>
/// The shape every registry number shares, on its own. Which letters a module issues under is that
/// module's business and is tested there; this is the padding, the parsing and the ordering that
/// all of them depend on.
/// </summary>
public class RegistryNumbersTests
{
    private const string Prefix = "X-";

    [Theory]
    [InlineData(1, "X-000001")]
    [InlineData(42, "X-000042")]
    [InlineData(999_999, "X-999999")]
    public void An_ordinal_is_padded_to_a_fixed_width(long ordinal, string expected) =>
        Assert.Equal(expected, RegistryNumbers.Format(Prefix, ordinal));

    [Fact]
    public void A_longer_prefix_still_fits_the_stored_width() =>
        // Assets issue AST-000001, two characters longer than the customer registry's C-000001.
        // MaxLength has to hold the longest prefix any module picks plus a grown ordinal.
        Assert.True(RegistryNumbers.Format("AST-", 1_000_000).Length <= RegistryNumbers.MaxLength);

    [Fact]
    public void A_number_from_another_series_does_not_continue_this_one() =>
        Assert.Null(RegistryNumbers.OrdinalOf(Prefix, "Y-000042"));

    [Fact]
    public void A_series_past_the_padding_grows_rather_than_wrapping() =>
        // Six digits is a demo-sized utility, not a limit. What must never happen is the millionth
        // number colliding with an earlier one.
        Assert.Equal("X-1000000", RegistryNumbers.Format(Prefix, 1_000_000));

    [Fact]
    public void Fixed_width_numbers_sort_lexically_in_issue_order()
    {
        // This is what lets a generator find the highest number issued with an ORDER BY the
        // database can answer from the unique index, identically on Postgres and on SQLite.
        var issued = Enumerable.Range(1, 250)
            .Select(ordinal => RegistryNumbers.Format(Prefix, ordinal))
            .ToList();

        Assert.Equal(issued, issued.OrderBy(number => number, StringComparer.Ordinal));
    }

    [Fact]
    public void The_ordinal_can_be_read_back_out() =>
        Assert.Equal(42, RegistryNumbers.OrdinalOf(Prefix, "X-000042"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Y-000042")]
    [InlineData("X-")]
    [InlineData("X-00A042")]
    [InlineData("X--00042")]
    [InlineData("X-000000")]
    [InlineData("LEGACY-4711")]
    public void A_number_of_another_shape_has_no_ordinal(string? number) =>
        // Failure path: a legacy or hand-entered number must not be counted as the highest issued,
        // which would either restart the series or push it somewhere arbitrary.
        Assert.Null(RegistryNumbers.OrdinalOf(Prefix, number));

    [Fact]
    public void An_ordinal_below_one_cannot_be_formatted() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryNumbers.Format(Prefix, 0));

    [Fact]
    public void An_empty_series_starts_at_one() =>
        Assert.Equal("X-000001", RegistryNumbers.After(Prefix, highestIssued: null));

    [Fact]
    public void The_next_number_follows_the_highest_issued() =>
        Assert.Equal("X-000043", RegistryNumbers.After(Prefix, "X-000042"));

    [Fact]
    public void A_legacy_number_does_not_move_the_series() =>
        // Failure path: the generator reads the lexically highest row, and a hand-entered number of
        // another shape sorting above the real ones must not restart the series at two.
        Assert.Equal("X-000001", RegistryNumbers.After(Prefix, "LEGACY-4711"));
}
