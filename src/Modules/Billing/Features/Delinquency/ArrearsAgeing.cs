using GridCore.Contracts.Directories;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Billing.Features.Delinquency;

/// <summary>One band of the debtors' ageing, as GridCore publishes it.</summary>
/// <param name="Label">What it is called on a screen and in a report.</param>
/// <param name="FromDays">The fewest days past due that fall in it.</param>
/// <param name="ToDays">The most, or <see langword="null"/> on the open-ended oldest band.</param>
public sealed record ArrearsBand(string Label, int FromDays, int? ToDays)
{
    /// <summary>Whether a bill <paramref name="daysPastDue"/> days late falls in this band.</summary>
    public bool Holds(int daysPastDue) =>
        daysPastDue >= FromDays && (ToDays is not { } upper || daysPastDue <= upper);
}

/// <summary>
/// The debtors' ageing: how an account's outstanding bills are sorted into age bands, and how the
/// arrears figure everything else in WP-2.19 turns on is arrived at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and that is the point.</b> The 1% late charge, the dunning sequence, the disconnection
/// threshold and the statutory deposit offset all read the figures this produces, so every boundary
/// case — the bill due today, the bill due yesterday, the bill with nothing left on it — is provable
/// in the fast tier with no database anywhere near it (CONVENTIONS.md rule C).
/// </para>
/// <para>
/// <b>Due today is not late.</b> A bill is past due when its due date is strictly before the day
/// being aged against, which is the same predicate <c>BillService.ReviewOverdueAsync</c> moves a
/// bill to <see cref="Bills.BillStatus.Overdue"/> on. A customer has the whole of the due day to
/// pay, and an ageing that disagreed with the status the register itself sets would make "why is
/// this bill overdue but not in arrears" a real question.
/// </para>
/// <para>
/// <b>The bands live here rather than in Contracts.</b> The seam publishes an
/// <see cref="ArrearsBucket"/> with a label and a range because a screen has to render one; deciding
/// which bands exist is a policy of the register that owns the bills, and a second module free to
/// pick its own would age the same debt two ways.
/// </para>
/// </remarks>
public static class ArrearsAgeing
{
    /// <summary>The band a bill that is not yet due falls in. First, so an ageing reads left to right.</summary>
    public static readonly ArrearsBand NotYetDue = new("Not yet due", 0, 0);

    /// <summary>
    /// Every band, youngest first. Contiguous and exhaustive over the non-negative day counts by
    /// construction: the boundaries are the ordinary utility ageing, and the last band is open.
    /// </summary>
    public static IReadOnlyList<ArrearsBand> Bands { get; } =
    [
        NotYetDue,
        new("1-30 days", 1, 30),
        new("31-60 days", 31, 60),
        new("61-90 days", 61, 90),
        new("Over 90 days", 91, null),
    ];

    /// <summary>
    /// How many days late a bill due on <paramref name="dueDate"/> is on <paramref name="asOf"/>.
    /// </summary>
    /// <remarks>
    /// <b>Never negative.</b> A bill that is not yet due is nought days past due, not minus nine —
    /// a signed answer here would sum into a band, and "days early" is not an age.
    /// </remarks>
    public static int DaysPastDue(DateOnly? dueDate, DateOnly asOf) =>
        dueDate is { } due ? Math.Max(0, asOf.DayNumber - due.DayNumber) : 0;

    /// <summary>Whether a bill due on <paramref name="dueDate"/> is late on <paramref name="asOf"/>.</summary>
    /// <remarks>
    /// A bill with no due date is never late. It is a draft, and this module refuses to age one —
    /// but the guard is here rather than only in the query, because a nullable column that means
    /// "not yet asked for" must not silently become "overdue since the epoch".
    /// </remarks>
    public static bool IsPastDue(DateOnly? dueDate, DateOnly asOf) => dueDate is { } due && due < asOf;

    /// <summary>Ages one outstanding bill against <paramref name="asOf"/>.</summary>
    /// <param name="id">Identifier of the bill.</param>
    /// <param name="billNumber">The number printed on it.</param>
    /// <param name="dueDate">The day it fell due.</param>
    /// <param name="balance">What is still owed on it.</param>
    /// <param name="asOf">The day being aged against.</param>
    public static ArrearsBill Line(Guid id, string billNumber, DateOnly? dueDate, decimal balance, DateOnly asOf) =>
        new(id, billNumber, dueDate, balance, DaysPastDue(dueDate, asOf), IsPastDue(dueDate, asOf));

    /// <summary>
    /// Composes the whole picture from <paramref name="bills"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bills with nothing left on them are dropped, not aged at nought.</b> A bill settled to the
    /// cent is still <c>PartiallyPaid</c> until the register moves it, and carrying it would put a
    /// zero row in an ageing and a bill number in front of a rep who has nothing to say about it.
    /// </para>
    /// <para>
    /// Totals are <see cref="Money.Total"/> over figures the register already rounded — nothing here
    /// rounds, because every balance arrived from a stored column that is exact to the cent.
    /// </para>
    /// </remarks>
    /// <param name="serviceAccountId">The account owing it.</param>
    /// <param name="currency">ISO 4217 code every amount is expressed in.</param>
    /// <param name="asOf">The day to age against.</param>
    /// <param name="bills">The account's outstanding bills, already aged by <see cref="Line"/>.</param>
    public static AccountArrears Compose(
        Guid serviceAccountId,
        string currency,
        DateOnly asOf,
        IEnumerable<ArrearsBill> bills)
    {
        ArgumentNullException.ThrowIfNull(bills);

        var owed = bills
            .Where(bill => bill.Balance > Money.Zero)
            .OrderBy(bill => bill.DueDate ?? DateOnly.MaxValue)
            .ThenBy(bill => bill.BillNumber, StringComparer.Ordinal)
            .ToList();

        var pastDue = owed.Where(bill => bill.IsPastDue).ToList();

        var buckets = Bands
            .Select(band => new ArrearsBucket(
                band.Label,
                band.FromDays,
                band.ToDays,
                Money.Total(
                    owed
                        .Where(bill => band == NotYetDue ? !bill.IsPastDue : bill.IsPastDue && band.Holds(bill.DaysPastDue))
                        .Select(bill => bill.Balance))))
            .ToList();

        return new AccountArrears(
            serviceAccountId,
            currency,
            asOf,
            Money.Total(owed.Select(bill => bill.Balance)),
            Money.Total(pastDue.Select(bill => bill.Balance)),
            Money.Total(owed.Where(bill => !bill.IsPastDue).Select(bill => bill.Balance)),

            // The OLDEST past-due bill, which is what a dunning step and a statutory waiting period
            // are both measured from. The list is ordered by due date, so it is the first of them.
            pastDue.Count is 0 ? null : pastDue[0].DueDate,
            pastDue.Count is 0 ? 0 : pastDue[0].DaysPastDue,
            buckets,
            owed);
    }
}
