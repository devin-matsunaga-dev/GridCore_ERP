using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.Shared;
using GridCore.Platform.Monetary;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Finance.Features.Reports;

/// <summary>What a caller filters the receivables ledger by.</summary>
/// <param name="AsOf">Read up to this accounting date, inclusive. Defaults to everything.</param>
/// <param name="ServiceAccountId">Only this service account.</param>
/// <param name="CustomerId">Only this customer.</param>
/// <param name="OutstandingOnly">Only rows that still owe something.</param>
public sealed record ReceivablesQuery(
    DateOnly? AsOf = null,
    Guid? ServiceAccountId = null,
    Guid? CustomerId = null,
    bool OutstandingOnly = false);

/// <summary>The two reports the ledger answers: what it stands at, and who owes it.</summary>
public interface IFinanceReportService
{
    /// <summary>Every account, its debits and credits, and whether the two columns agree.</summary>
    Task<TrialBalance> TrialBalanceAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>The receivables subsidiary ledger — who owes the control account's balance.</summary>
    Task<Receivables> ReceivablesAsync(ReceivablesQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The ledger's reports, read straight off <c>finance.journal_lines</c>.</summary>
/// <remarks>
/// <para>
/// <b>Nothing is stored and nothing is cached.</b> An account's balance is the sum of its lines,
/// computed here on demand — never a running column that could drift from the entries meant to
/// explain it. That is the call <see cref="Account.NormalBalance"/> already made, applied to money.
/// </para>
/// <para>
/// <b>The database sums; this class shapes.</b> Each query groups in SQL and returns one flat row
/// per account or per party, and the report is assembled in memory over a handful of them. The
/// receivables read resolves its control account from the chart <i>first</i> and then filters lines
/// on the resulting id, rather than reaching through a navigation inside a group-by: it is one
/// extra round trip for SQL simple enough to translate identically on Postgres and on the fast
/// tier's SQLite, which is the lesson WP-2.3's directories paid for.
/// </para>
/// </remarks>
public sealed class FinanceReportService(FinanceDbContext database) : IFinanceReportService
{
    /// <summary>The date a report reads up to when a caller names none — i.e. the whole ledger.</summary>
    public static readonly DateOnly Forever = DateOnly.MaxValue;

    /// <inheritdoc />
    public async Task<TrialBalance> TrialBalanceAsync(
        DateOnly? asOf = null,
        CancellationToken cancellationToken = default)
    {
        var upTo = asOf ?? Forever;

        // One row per account that has been posted to, summed in the database.
        var totals = await database.JournalEntries
            .AsNoTracking()
            .Where(entry => entry.PostedOn <= upTo)
            .SelectMany(entry => entry.Lines)
            .GroupBy(line => line.AccountId)
            .Select(group => new AccountTotals(
                group.Key,
                group.Sum(line => line.Debit),
                group.Sum(line => line.Credit),
                group.Count()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byAccount = totals.ToDictionary(total => total.AccountId);

        // Every account in the chart, whether or not anything was posted to it: the chart is the
        // report's shape and the ledger only fills it in.
        var accounts = await database.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = accounts
            .Select(account =>
            {
                var total = byAccount.GetValueOrDefault(account.Id);

                return new TrialBalanceRow(
                    account.Code,
                    account.Name,
                    account.Type,
                    account.NormalBalance,
                    total?.Debits ?? Money.Zero,
                    total?.Credits ?? Money.Zero,
                    total?.LineCount ?? 0);
            })
            .ToList();

        return new TrialBalance(upTo, rows);
    }

    /// <inheritdoc />
    public async Task<Receivables> ReceivablesAsync(
        ReceivablesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var upTo = query.AsOf ?? Forever;

        var controlAccountId = await database.Accounts
            .AsNoTracking()
            .Where(account => account.Code == FinanceAccounts.AccountsReceivable)
            .Select(account => account.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (controlAccountId == Guid.Empty)
        {
            // The chart ships by migration, so this means a database that has never been migrated
            // rather than a report with nothing to say — and an empty AR view would be the wrong
            // answer to give somebody about money they are owed.
            throw new FinanceValidationException(
                $"The chart of accounts has no receivables account ({FinanceAccounts.AccountsReceivable}), "
                + "so there is no control account to read. The chart ships by migration.");
        }

        var entries = database.JournalEntries.AsNoTracking().Where(entry => entry.PostedOn <= upTo);

        if (query.ServiceAccountId is { } serviceAccountId)
        {
            entries = entries.Where(entry => entry.ServiceAccountId == serviceAccountId);
        }

        if (query.CustomerId is { } customerId)
        {
            entries = entries.Where(entry => entry.CustomerId == customerId);
        }

        var totals = await entries
            .SelectMany(
                entry => entry.Lines.Where(line => line.AccountId == controlAccountId),
                (entry, line) => new
                {
                    entry.ServiceAccountId,
                    entry.CustomerId,
                    entry.PostedOn,
                    line.Debit,
                    line.Credit,
                })
            .GroupBy(row => new { row.ServiceAccountId, row.CustomerId })
            .Select(group => new PartyTotals(
                group.Key.ServiceAccountId,
                group.Key.CustomerId,
                group.Sum(row => row.Debit),
                group.Sum(row => row.Credit),
                group.Count(),
                group.Max(row => row.PostedOn)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = totals
            .Select(total => new ReceivableRow(
                total.ServiceAccountId,
                total.CustomerId,
                total.Debits,
                total.Credits,
                total.PostingCount,
                total.LastPostedOn))
            .Where(row => !query.OutstandingOnly || row.Outstanding != Money.Zero)

            // Most owed first: an AR worklist is read from the top, and the row nobody needs to act
            // on is the one that has been settled.
            .OrderByDescending(row => row.Outstanding)
            .ThenBy(row => row.ServiceAccountId)
            .ToList();

        return new Receivables(upTo, FinanceAccounts.AccountsReceivable, rows);
    }

    /// <summary>One account's sums, as the database returns them.</summary>
    private sealed record AccountTotals(Guid AccountId, decimal Debits, decimal Credits, int LineCount);

    /// <summary>One party's receivables sums, as the database returns them.</summary>
    private sealed record PartyTotals(
        Guid? ServiceAccountId,
        Guid? CustomerId,
        decimal Debits,
        decimal Credits,
        int PostingCount,
        DateOnly LastPostedOn);
}
