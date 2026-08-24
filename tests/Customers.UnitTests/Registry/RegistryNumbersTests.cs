using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>The account-number and location-code format, on its own.</summary>
public class RegistryNumbersTests
{
    [Theory]
    [InlineData(1, "C-000001")]
    [InlineData(42, "C-000042")]
    [InlineData(999_999, "C-999999")]
    public void An_ordinal_is_padded_to_a_fixed_width(long ordinal, string expected) =>
        Assert.Equal(expected, RegistryNumbers.Format(RegistryNumbers.CustomerPrefix, ordinal));

    [Fact]
    public void A_series_past_the_padding_grows_rather_than_wrapping() =>
        // Six digits is a demo-sized utility, not a limit. What must never happen is a seventh
        // customer's number colliding with an earlier one's.
        Assert.Equal("C-1000000", RegistryNumbers.Format(RegistryNumbers.CustomerPrefix, 1_000_000));

    [Fact]
    public void Fixed_width_numbers_sort_lexically_in_issue_order()
    {
        // This is what lets the generator find the highest number issued with an ORDER BY the
        // database can answer from the unique index, identically on Postgres and on SQLite.
        var issued = Enumerable.Range(1, 250)
            .Select(ordinal => RegistryNumbers.Format(RegistryNumbers.CustomerPrefix, ordinal))
            .ToList();

        Assert.Equal(issued, issued.OrderBy(number => number, StringComparer.Ordinal));
    }

    [Fact]
    public void The_ordinal_can_be_read_back_out() =>
        Assert.Equal(42, RegistryNumbers.OrdinalOf(RegistryNumbers.CustomerPrefix, "C-000042"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("L-000042")]
    [InlineData("C-")]
    [InlineData("C-00A042")]
    [InlineData("C--00042")]
    [InlineData("C-000000")]
    [InlineData("LEGACY-4711")]
    public void A_number_of_another_shape_has_no_ordinal(string? number) =>
        // Failure path: a legacy or hand-entered number must not be counted as the highest issued,
        // which would either restart the series or push it somewhere arbitrary.
        Assert.Null(RegistryNumbers.OrdinalOf(RegistryNumbers.CustomerPrefix, number));

    [Fact]
    public void An_ordinal_below_one_cannot_be_formatted() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryNumbers.Format(RegistryNumbers.CustomerPrefix, 0));
}
