using GridCore.Modules.Finance.Features.ChartOfAccounts;
using Microsoft.EntityFrameworkCore;
using Chart = GridCore.Modules.Finance.Features.ChartOfAccounts.ChartOfAccounts;

namespace GridCore.Modules.Finance.UnitTests.ChartOfAccounts;

/// <summary>
/// The finance schema as EF actually builds it, on SQLite in-memory. These assertions are what
/// prove ARCHITECTURE.md invariant 8's first half: a database that has only been migrated — never
/// seeded — already has a chart of accounts.
/// </summary>
public class FinanceModelTests
{
    [Fact]
    public async Task Creating_the_schema_seeds_the_whole_chart()
    {
        using var database = new FinanceTestDatabase();

        await using var context = database.NewContext();

        var seeded = await context.Accounts.OrderBy(account => account.Code).ToListAsync();

        Assert.Equal(Chart.All.Count, seeded.Count);
        Assert.Equal(
            Chart.All.Select(account => account.Code).Order(StringComparer.Ordinal),
            seeded.Select(account => account.Code));
    }

    [Fact]
    public async Task A_seeded_account_keeps_its_type_and_derived_normal_balance()
    {
        using var database = new FinanceTestDatabase();

        await using var context = database.NewContext();

        var receivables = await context.Accounts.SingleAsync(account => account.Code == FinanceAccounts.AccountsReceivable);

        Assert.Equal(AccountType.Asset, receivables.Type);
        Assert.Equal(NormalBalance.Debit, receivables.NormalBalance);
        Assert.Equal("Accounts receivable", receivables.Name);
    }

    [Fact]
    public async Task Two_accounts_cannot_share_a_code()
    {
        // Failure path: the code is what a posting names, so a duplicate would make "1100" mean two
        // different accounts. The database refuses it, not just the code that builds the chart.
        using var database = new FinanceTestDatabase();

        database.Context.Accounts.Add(Account.Reference("1100", "A second receivables account", AccountType.Asset));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }
}
