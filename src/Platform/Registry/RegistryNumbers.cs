using System.Globalization;

namespace GridCore.Platform.Registry;

/// <summary>
/// The shape every GridCore registry number shares: a module-supplied prefix and a zero-padded
/// ordinal, e.g. <c>C-000001</c>, <c>L-000042</c>, <c>AST-000007</c>.
/// </summary>
/// <remarks>
/// <para>
/// The padding is not decoration. Fixed-width numbers sort lexically in the same order they sort
/// numerically, which is what lets a generator find the highest one issued with an <c>ORDER BY</c>
/// the database can answer from the unique index — identically on Postgres and on the fast tier's
/// SQLite, with no provider-specific SQL. See <see cref="RegistryNumberSeries"/>.
/// </para>
/// <para>
/// Prefixes are <b>not</b> declared here. The shape is the platform's; which letters stand for a
/// customer, a premise or an asset is the owning module's business, and a platform that knew them
/// would be a platform that has to change every time a registry lands.
/// </para>
/// </remarks>
public static class RegistryNumbers
{
    /// <summary>Digits an ordinal is padded to. It grows past this rather than wrapping.</summary>
    public const int Digits = 6;

    /// <summary>Longest number stored, leaving room for a longer prefix and an ordinal well past <see cref="Digits"/>.</summary>
    public const int MaxLength = 24;

    /// <summary>Renders <paramref name="ordinal"/> as a number under <paramref name="prefix"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is not positive.</exception>
    public static string Format(string prefix, long ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);

        return prefix + ordinal.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    /// <summary>
    /// The ordinal inside <paramref name="number"/>, or <see langword="null"/> when it is not a
    /// number of this shape — a hand-entered legacy number, say, which must not be counted as the
    /// highest one issued.
    /// </summary>
    public static long? OrdinalOf(string prefix, string? number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        if (number is null || !number.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return long.TryParse(number[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            && ordinal > 0
                ? ordinal
                : null;
    }

    /// <summary>
    /// The number that follows <paramref name="highestIssued"/> in the <paramref name="prefix"/>
    /// series — the first one when nothing of this shape has been issued yet.
    /// </summary>
    public static string After(string prefix, string? highestIssued) =>
        Format(prefix, (OrdinalOf(prefix, highestIssued) ?? 0) + 1);
}
