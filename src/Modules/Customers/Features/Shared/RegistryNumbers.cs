using System.Globalization;

namespace GridCore.Modules.Customers.Features.Shared;

/// <summary>
/// How a customer account number and a service location code are shaped: a fixed prefix and a
/// zero-padded ordinal, e.g. <c>C-000001</c> and <c>L-000042</c>.
/// </summary>
/// <remarks>
/// The padding is not decoration. Fixed-width numbers sort lexically in the same order they sort
/// numerically, which is what lets <see cref="IRegistryNumberGenerator"/> find the highest one
/// issued with an <c>ORDER BY</c> the database can answer from the unique index — identically on
/// Postgres and on the fast tier's SQLite, with no provider-specific SQL.
/// </remarks>
public static class RegistryNumbers
{
    /// <summary>Prefix of a customer account number.</summary>
    public const string CustomerPrefix = "C-";

    /// <summary>Prefix of a service location code.</summary>
    public const string ServiceLocationPrefix = "L-";

    /// <summary>Digits an ordinal is padded to. It grows past this rather than wrapping.</summary>
    public const int Digits = 6;

    /// <summary>Longest number stored, leaving room for an ordinal well past <see cref="Digits"/>.</summary>
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
}
