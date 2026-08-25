using GridCore.Platform.Monetary;

namespace GridCore.Modules.Finance.Features.Reports;

/// <summary>What one service account owes, as the receivables ledger sees it.</summary>
/// <param name="ServiceAccountId">The account, or <see langword="null"/> for postings against none.</param>
/// <param name="CustomerId">The customer behind it, where the postings named one.</param>
/// <param name="Charged">Everything debited to receivables for them — bills issued, corrections upward.</param>
/// <param name="Settled">Everything credited — payments taken, credits allowed.</param>
/// <param name="PostingCount">How many receivables lines are behind those two figures.</param>
/// <param name="LastPostedOn">The accounting date of the most recent of them.</param>
public sealed record ReceivableRow(
    Guid? ServiceAccountId,
    Guid? CustomerId,
    decimal Charged,
    decimal Settled,
    int PostingCount,
    DateOnly LastPostedOn)
{
    /// <summary>
    /// What is still owed. Negative means the utility holds money it has not applied — an
    /// overpayment or a credit past zero, which WP-2.3 and WP-2.4 both said Finance would hold.
    /// </summary>
    public decimal Outstanding => Charged - Settled;
}

/// <summary>
/// The accounts receivable subsidiary ledger: who owes the balance the control account carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built from the ledger, not from Billing.</b> Finance is downstream of everyone and reads no
/// other module's tables, so this is the receivables control account's own lines grouped by the
/// service account on their entry. That is why <c>JournalPostingIntent</c> carries the party at
/// all.
/// </para>
/// <para>
/// <b>It reconciles with the trial balance by construction</b>, which is the assertion worth making
/// about a subsidiary ledger: <see cref="TotalOutstanding"/> is the same set of lines the trial
/// balance's receivables row sums, only grouped. A subsidiary ledger that disagreed with its
/// control account would be the first thing an auditor found.
/// </para>
/// </remarks>
/// <param name="AsOf">The accounting date read up to, inclusive.</param>
/// <param name="ControlAccountCode">The receivables account these lines were read from.</param>
/// <param name="Rows">One row per service account with receivables activity, most owed first.</param>
public sealed record Receivables(DateOnly AsOf, string ControlAccountCode, IReadOnlyList<ReceivableRow> Rows)
{
    /// <summary>Everything ever charged to receivables in the period read.</summary>
    public decimal TotalCharged => Money.Total(Rows.Select(row => row.Charged));

    /// <summary>Everything ever settled against it.</summary>
    public decimal TotalSettled => Money.Total(Rows.Select(row => row.Settled));

    /// <summary>What the utility is owed in total — the receivables control account's balance.</summary>
    public decimal TotalOutstanding => TotalCharged - TotalSettled;

    /// <summary>
    /// What is owed by nobody in particular: receivables postings that named no service account.
    /// </summary>
    /// <remarks>
    /// Zero today — every posting that touches receivables comes from a bill or a payment, and both
    /// name their account. It is reported rather than assumed away because the day a manual journal
    /// or a bad-debt write-off lands on receivables without a party, the difference between the
    /// control account and the sum of the customers has to show up somewhere a person will see it.
    /// </remarks>
    public decimal Unallocated => Money.Total(
        Rows.Where(row => row.ServiceAccountId is null).Select(row => row.Outstanding));
}
