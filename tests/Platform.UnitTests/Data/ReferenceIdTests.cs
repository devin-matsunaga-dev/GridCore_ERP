using GridCore.Platform.Data;

namespace GridCore.Platform.UnitTests.Data;

/// <summary>
/// Reference rows are seeded by migrations, and EF compares seeded rows against the model snapshot.
/// An id that varied between model builds would therefore rewrite the chart of accounts on every
/// <c>migrations add</c> — these assertions are what makes that impossible.
/// </summary>
public class ReferenceIdTests
{
    private static readonly DateTimeOffset AuthoredAt = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_same_key_always_yields_the_same_id()
    {
        Assert.Equal(ReferenceId.For(AuthoredAt, "1100"), ReferenceId.For(AuthoredAt, "1100"));
    }

    [Fact]
    public void Different_keys_yield_different_ids()
    {
        Assert.NotEqual(ReferenceId.For(AuthoredAt, "1100"), ReferenceId.For(AuthoredAt, "1000"));
    }

    [Fact]
    public void Keys_are_case_sensitive()
    {
        // Codes are upper case by convention; a helper that quietly folded case would let two
        // reference rows share an id and only one of them would ever be inserted.
        Assert.NotEqual(ReferenceId.For(AuthoredAt, "MAIN"), ReferenceId.For(AuthoredAt, "main"));
    }

    [Fact]
    public void A_different_authoring_instant_yields_a_different_id()
    {
        Assert.NotEqual(ReferenceId.For(AuthoredAt, "1100"), ReferenceId.For(AuthoredAt.AddDays(1), "1100"));
    }

    [Fact]
    public void The_id_is_a_real_version_7_uuid()
    {
        // Not cosmetic: the version and variant bits are what make the timestamp prefix meaningful,
        // and Postgres orders uuids by those same bytes.
        var bytes = ReferenceId.For(AuthoredAt, "1100").ToByteArray(bigEndian: true);

        Assert.Equal(0x70, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Fact]
    public void The_id_carries_the_authoring_instant_so_it_sorts_chronologically()
    {
        // The whole reason not to use a random Guid: reference rows must sort beside runtime ones,
        // because the primary-key index is what every later query orders on.
        var earlier = ReferenceId.For(AuthoredAt, "zzzz");
        var later = ReferenceId.For(AuthoredAt.AddYears(1), "aaaa");

        Assert.True(earlier.CompareTo(later) < 0);
    }

    [Fact]
    public void An_empty_key_is_refused()
    {
        // Failure path: a reference row with no natural key has nothing to derive an id from, and a
        // silently shared id would collapse the whole set onto one row.
        Assert.Throws<ArgumentException>(() => ReferenceId.For(AuthoredAt, "  "));
    }
}
