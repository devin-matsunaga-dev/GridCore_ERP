// Namespace deliberately NOT "GridCore.Platform.Money": a type whose name matches its own
// namespace is unreachable by simple name from anywhere under GridCore.Platform, because the
// namespace wins the lookup. Every other Platform folder already avoids this — Registry has no
// Registry, Audit has no Audit.
namespace GridCore.Platform.Monetary;

/// <summary>
/// The one place GridCore rounds money. CONVENTIONS.md asks for exactly this — "money
/// <see langword="decimal"/>; centralize rounding in one helper" — and until WP-2.3 there was
/// nothing to round, so every value finer than its column was refused instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rounding is half-away-from-zero, not banker's.</b> <c>decimal.Round</c> defaults to
/// <see cref="MidpointRounding.ToEven"/>, which is the right choice for a long series of
/// measurements and the wrong one for a bill: a customer checking the arithmetic on the back of the
/// envelope rounds 0.125 up to 0.13, and a utility that answered 0.12 would be explaining itself
/// for the rest of the call. The demonstrable rule beats the statistically neutral one wherever a
/// person has to agree with the answer.
/// </para>
/// <para>
/// <b>Rounding happens at the line, not at the total.</b> A bill is a list of amounts and a sum; if
/// the lines were rounded only after being added, the printed bill would not add up to its own
/// total. So each charge is rounded as it is computed and the total is the sum of what is printed —
/// see <c>RateEngine</c>, where that is the whole reason the two are separate steps.
/// </para>
/// <para>
/// <b>This does not license silent truncation.</b> A value arriving from outside that is finer than
/// a cent is still refused rather than rounded (WP-1.1's deposit, WP-1.3's coordinate, WP-1.4's
/// quantity, WP-2.1's installation reading): rounding is for figures GridCore <i>computes</i>, and
/// refusal is for figures somebody <i>typed</i>. <see cref="IsRounded"/> is how a guard asks.
/// </para>
/// </remarks>
public static class Money
{
    /// <summary>Decimal places money carries — the cent. Matches every <c>numeric(18,2)</c> column.</summary>
    public const int DecimalPlaces = 2;

    /// <summary>Total digits a money column stores.</summary>
    public const int Precision = 18;

    /// <summary>Nothing.</summary>
    public const decimal Zero = 0m;

    /// <summary>
    /// <paramref name="amount"/> to the cent, halves away from zero.
    /// </summary>
    public static decimal Round(decimal amount) =>
        decimal.Round(amount, DecimalPlaces, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Whether <paramref name="amount"/> is already exact to the cent — what a guard asks before
    /// refusing a value somebody typed rather than quietly rounding it.
    /// </summary>
    public static bool IsRounded(decimal amount) => Round(amount) == amount;

    /// <summary>
    /// The sum of <paramref name="amounts"/>, each of which is already rounded. Adding cents is
    /// exact in <see langword="decimal"/>, so this rounds nothing — it exists so a total is never
    /// written as a <c>Sum()</c> that a later reader has to check for a hidden rounding step.
    /// </summary>
    public static decimal Total(IEnumerable<decimal> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);

        var total = Zero;

        foreach (var amount in amounts)
        {
            total += amount;
        }

        return total;
    }
}
