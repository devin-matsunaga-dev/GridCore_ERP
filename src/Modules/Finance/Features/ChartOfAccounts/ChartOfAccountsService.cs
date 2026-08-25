using GridCore.Modules.Finance.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Finance.Features.ChartOfAccounts;

/// <summary>The chart of accounts, read-only.</summary>
/// <remarks>
/// There is no create, no edit and no delete, and there never will be here: accounts are reference
/// data shipped by migration (invariant 7 and 8 between them), so adding one is a migration rather
/// than a POST. A ledger whose accounts could be typed in at run time is a ledger whose historic
/// entries point at accounts that have since been renamed into something else.
/// </remarks>
public interface IChartOfAccountsService
{
    /// <summary>Every account, in code order.</summary>
    Task<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The chart as the database holds it, rather than as <see cref="ChartOfAccounts"/> declares it.
/// </summary>
/// <remarks>
/// Deliberately the rows and not the static list. The two agree — a fast test asserts it — but the
/// static list is what the migration was generated <i>from</i>, and an endpoint that served it
/// would answer happily on a database the migration had never reached. Reading the table is how a
/// caller finds that out.
/// </remarks>
public sealed class ChartOfAccountsService(FinanceDbContext database) : IChartOfAccountsService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken = default) =>
        await database.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
