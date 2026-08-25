using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.Registry;

/// <summary>
/// Continues a registry number series from the highest number already issued, inside the caller's
/// transaction. The one query every module's number generator would otherwise write for itself.
/// </summary>
/// <remarks>
/// <para>
/// Two registrations racing would read the same highest number and try to issue it twice. The
/// <b>unique index</b> is what makes that safe: the loser's transaction is rejected and its caller
/// gets a 409, so a duplicate number is impossible rather than merely unlikely. That is the right
/// trade for an MVP whose registrations are typed in by hand — a Postgres sequence would serialise
/// the issue, at the cost of SQL the fast tier's SQLite cannot run, and swapping a module's
/// generator implementation for one is a DI change with no domain code touched.
/// </para>
/// <para>
/// The lookup is an <c>ORDER BY … DESC LIMIT 1</c> over the unique index rather than a <c>MAX</c>
/// over a parsed substring, which works because <see cref="RegistryNumbers"/> pads to a fixed
/// width: the lexical maximum and the numeric maximum are the same string.
/// </para>
/// </remarks>
public static class RegistryNumberSeries
{
    /// <summary>
    /// The next unused number under <paramref name="prefix"/>, given <paramref name="issued"/> — a
    /// query over the column the numbers are stored in, already filtered to this prefix.
    /// </summary>
    public static async Task<string> NextAsync(
        string prefix,
        IQueryable<string> issued,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(issued);

        var highest = await issued.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return RegistryNumbers.After(prefix, highest);
    }

    /// <summary>
    /// The next <paramref name="count"/> unused numbers under <paramref name="prefix"/>, in order.
    /// </summary>
    /// <remarks>
    /// <b>A batch must be reserved in one call, not by asking <see cref="NextAsync"/> repeatedly.</b>
    /// Rows added to a context but not yet saved are invisible to a query, which goes to the
    /// database — so a run that issued ten bills by asking ten times would be handed the same number
    /// ten times and lose nine of them to the unique index. This is the same reason a demo seeder
    /// assigns its own numbers rather than calling a generator (WP-1.1), stated once here so the
    /// next module that writes a batch does not have to rediscover it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static async Task<IReadOnlyList<string>> NextManyAsync(
        string prefix,
        IQueryable<string> issued,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(issued);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count is 0)
        {
            return [];
        }

        var highest = await issued.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var ordinal = RegistryNumbers.OrdinalOf(prefix, highest) ?? 0;

        var numbers = new string[count];

        for (var index = 0; index < count; index++)
        {
            numbers[index] = RegistryNumbers.Format(prefix, ordinal + index + 1);
        }

        return numbers;
    }
}
