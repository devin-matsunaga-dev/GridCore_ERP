using GridCore.Contracts.Events;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using Chart = GridCore.Modules.Finance.Features.ChartOfAccounts.ChartOfAccounts;

namespace GridCore.Modules.Finance.UnitTests.ChartOfAccounts;

/// <summary>
/// The chart is reference data seeded by a migration, so it is defined in code once and asserted
/// here. The assertion that matters most is the last one: every account the event seam posts to
/// must actually exist, which is the loop WP-0.5 left open when it named placeholder codes.
/// </summary>
public class ChartOfAccountsTests
{
    [Fact]
    public void Account_codes_are_unique()
    {
        var codes = Chart.All.Select(account => account.Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Account_ids_are_unique()
    {
        // Two accounts sharing a derived id would mean the migration inserted one and silently
        // dropped the other on the primary key.
        var ids = Chart.All.Select(account => account.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void An_account_id_is_the_same_every_time_the_chart_is_built()
    {
        // EF compares seeded rows against the model snapshot: an id that moved would rewrite the
        // whole chart on the next `migrations add`.
        var expected = Account.Reference(FinanceAccounts.AccountsReceivable, "Accounts receivable", AccountType.Asset);

        Assert.Equal(expected.Id, Chart.Require(FinanceAccounts.AccountsReceivable).Id);
    }

    [Theory]
    [InlineData(AccountType.Asset, NormalBalance.Debit)]
    [InlineData(AccountType.Expense, NormalBalance.Debit)]
    [InlineData(AccountType.Liability, NormalBalance.Credit)]
    [InlineData(AccountType.Equity, NormalBalance.Credit)]
    [InlineData(AccountType.Revenue, NormalBalance.Credit)]
    public void Normal_balance_follows_the_account_type(AccountType type, NormalBalance expected)
    {
        Assert.Equal(expected, Account.NormalBalanceOf(type));
    }

    [Fact]
    public void The_chart_covers_all_five_account_types()
    {
        // A trial balance with no equity or no expenses is not a trial balance.
        Assert.Equal(
            Enum.GetValues<AccountType>().ToHashSet(),
            Chart.All.Select(account => account.Type).ToHashSet());
    }

    [Fact]
    public void Every_named_account_code_exists_in_the_chart()
    {
        var declared = typeof(FinanceAccounts)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        var missing = declared.Where(code => !Chart.Contains(code)).ToList();

        Assert.True(
            missing.Count == 0,
            $"FinanceAccounts names codes the chart does not contain: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_account_the_event_seam_posts_to_exists_in_the_chart()
    {
        // The loop WP-0.5 left open. A posting that named an account nobody had opened would be
        // discovered when the ledger became real (WP-2.6), by which time it is a data problem.
        var occurredAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        JournalPostingIntent[] postings =
        [
            FinancePostings.From(BillIssued.For(
                occurredAt,
                billId: Guid.CreateVersion7(occurredAt),
                billNumber: "B-000123",
                serviceAccountId: Guid.CreateVersion7(occurredAt),
                customerId: Guid.CreateVersion7(occurredAt),
                periodStart: new DateOnly(2026, 7, 1),
                periodEnd: new DateOnly(2026, 7, 31),
                dueDate: new DateOnly(2026, 8, 20),
                amount: 120.45m,
                currency: "USD")),
            FinancePostings.From(PaymentApproved.For(
                occurredAt,
                paymentId: Guid.CreateVersion7(occurredAt),
                serviceAccountId: Guid.CreateVersion7(occurredAt),
                customerId: Guid.CreateVersion7(occurredAt),
                billId: Guid.CreateVersion7(occurredAt),
                amount: 120.45m,
                currency: "USD",
                method: "card",
                providerReference: "SIM-8842")),
            FinancePostings.From(GoodsReceived.For(
                occurredAt,
                receiptId: Guid.CreateVersion7(occurredAt),
                purchaseOrderId: Guid.CreateVersion7(occurredAt),
                warehouseId: Guid.CreateVersion7(occurredAt),
                vendorId: Guid.CreateVersion7(occurredAt),
                currency: "USD",
                lines: [new GoodsReceivedLine(Guid.CreateVersion7(occurredAt), "TRF-100", 3m, 249.99m)])),
        ];

        var codes = postings.SelectMany(posting => posting.Lines).Select(line => line.AccountCode).Distinct();

        Assert.All(codes, code => Assert.True(
            Chart.Contains(code),
            $"The seam posts to account '{code}', which the chart does not contain."));
    }

    [Fact]
    public void Asking_for_an_account_that_does_not_exist_throws()
    {
        // Failure path: a posting to an unknown account is a defect in the mapping, and a journal
        // entry is the wrong place to find one.
        Assert.Throws<KeyNotFoundException>(() => Chart.Require("9999"));
    }

    [Fact]
    public void An_account_with_no_code_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Account.Reference("  ", "Nameless", AccountType.Asset));
    }

    [Fact]
    public void An_over_long_account_code_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Account.Reference(new string('1', Account.CodeLength + 1), "Too long", AccountType.Asset));
    }
}
