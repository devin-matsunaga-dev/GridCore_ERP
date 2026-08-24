using GridCore.Platform.Registry;

namespace GridCore.Platform.UnitTests.Registry;

/// <summary>
/// How every registry treats free text. One helper rather than a copy per module, so a description,
/// a reason and a name cannot drift into storing "  " differently from each other.
/// </summary>
public class RegistryTextTests
{
    [Fact]
    public void Surrounding_whitespace_is_trimmed() =>
        Assert.Equal("Songsong feeder", RegistryText.Clean("  Songsong feeder\t\n", 64));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void Nothing_but_whitespace_becomes_null(string? value) =>
        // A column holding "  " reads as a value somebody typed. Null says nobody did.
        Assert.Null(RegistryText.Clean(value, 64));

    [Fact]
    public void Text_longer_than_the_column_is_capped_rather_than_rejected() =>
        // Failure path the other way round: a caller pasting a page of notes gets them truncated,
        // not a 500 out of the column's width.
        Assert.Equal(new string('a', 32), RegistryText.Clean(new string('a', 500), 32));

    [Fact]
    public void Text_is_trimmed_before_it_is_measured() =>
        Assert.Equal("abc", RegistryText.Clean("   abc   ", 3));

    [Fact]
    public void A_column_width_below_one_is_a_programming_error() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryText.Clean("anything", 0));
}
