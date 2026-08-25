using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Billing.UnitTests.Bills;

/// <summary>
/// The billing register as Payments is allowed to see it (WP-2.5). What matters here is the balance:
/// it is the figure a payment is checked against, and getting it from the printed total instead
/// would let a customer be charged for money a credit had already taken off.
/// </summary>
public sealed class BillDirectoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Now);
    private readonly BillingTestHost _host;

    public BillDirectoryTests() => _host = new BillingTestHost(_clock, new FakeCurrentUser("clerk-1", "Ana Reyes"));

    public void Dispose() => _host.Dispose();

    private IBillDirectory Directory(IServiceProvider services) =>
        new BillDirectory(services.GetRequiredService<BillingDbContext>());

    private Task<TResult> WithDirectoryAsync<TResult>(Func<IBillDirectory, Task<TResult>> work) =>
        _host.InScopeAsync(services => work(Directory(services)));

    /// <summary>Raises a bill on a fresh premise and hands it back as a draft.</summary>
    private async Task<Bill> ADraftAsync(string cycleCode)
    {
        var location = Guid.CreateVersion7(_clock.GetUtcNow());

        _host.Accounts.Add(location);
        _host.Readings.Add(location, consumption: 400m, cycleCode: cycleCode, readingDate: Now.AddDays(-20));

        var run = await _host.WithBillsAsync(register => register.RunAsync(new RunBillingInput(cycleCode)));

        // A step between writes: Guid v7 takes its timestamp from the clock, so rows minted inside
        // one frozen millisecond have no defined order at all.
        _clock.Advance(TimeSpan.FromMinutes(1));

        return Assert.Single(run.Bills);
    }

    private async Task<Bill> AnIssuedBillAsync(string cycleCode = "2026-07")
    {
        var draft = await ADraftAsync(cycleCode);

        var issued = await _host.WithBillsAsync(register => register.IssueAsync(draft.Id, new IssueBillInput()));

        _clock.Advance(TimeSpan.FromMinutes(1));

        return issued;
    }

    [Fact]
    public async Task A_bill_is_summarised_with_everything_a_payment_needs_and_nothing_more()
    {
        var bill = await AnIssuedBillAsync();

        var found = await WithDirectoryAsync(directory => directory.FindAsync(bill.Id));

        Assert.NotNull(found);
        Assert.Equal(bill.BillNumber, found.BillNumber);
        Assert.Equal(bill.ServiceAccountId, found.ServiceAccountId);
        Assert.Equal(bill.CustomerId, found.CustomerId);
        Assert.Equal(bill.Currency, found.Currency);
        Assert.Equal(bill.TotalAmount, found.TotalAmount);
        Assert.Equal(bill.AmountDue, found.AmountDue);
        Assert.Equal(bill.Balance, found.Balance);
        Assert.Equal(nameof(BillStatus.Issued), found.Status);
        Assert.True(found.IsOutstanding);
        Assert.Equal(bill.DueDate, found.DueDate);
    }

    [Fact]
    public async Task An_id_that_matches_nothing_answers_null() =>
        Assert.Null(await WithDirectoryAsync(directory => directory.FindAsync(Guid.CreateVersion7())));

    [Fact]
    public async Task The_balance_is_what_a_credit_left_behind_and_not_the_printed_total()
    {
        // WP-2.4's split, and the whole reason this seam computes the figure rather than handing
        // Payments three columns and a sign convention to get right.
        var bill = await AnIssuedBillAsync();
        var credit = decimal.Round(bill.TotalAmount / 4, 2);

        await _host.WithBillsAsync(register => register.AdjustAsync(
            bill.Id,
            new AdjustBillInput(BillAdjustmentKind.Credit, credit, "Estimated read corrected.")));

        var found = await WithDirectoryAsync(directory => directory.FindAsync(bill.Id));

        Assert.NotNull(found);
        Assert.Equal(bill.TotalAmount, found.TotalAmount);
        Assert.Equal(bill.TotalAmount - credit, found.AmountDue);
        Assert.Equal(bill.TotalAmount - credit, found.Balance);
        Assert.NotEqual(found.TotalAmount, found.Balance);
    }

    [Fact]
    public async Task A_draft_is_not_outstanding_because_nobody_has_been_asked_for_the_money()
    {
        var draft = await ADraftAsync("2026-06");

        var found = await WithDirectoryAsync(directory => directory.FindAsync(draft.Id));

        Assert.NotNull(found);
        Assert.False(found.IsOutstanding);
        Assert.Equal(nameof(BillStatus.Draft), found.Status);
    }

    [Fact]
    public async Task Many_bills_are_looked_up_in_one_call()
    {
        var first = await AnIssuedBillAsync("2026-06");
        var second = await AnIssuedBillAsync("2026-07");

        var found = await WithDirectoryAsync(directory =>
            directory.FindManyAsync([first.Id, second.Id, Guid.CreateVersion7()]));

        // Ids that match nothing are simply absent — a caller rendering a list has to cope with one
        // it cannot resolve anyway.
        Assert.Equal(2, found.Count);
        Assert.True(found.ContainsKey(first.Id));
        Assert.True(found.ContainsKey(second.Id));
    }

    [Fact]
    public async Task Asking_about_no_bills_at_all_asks_the_database_nothing() =>
        Assert.Empty(await WithDirectoryAsync(directory => directory.FindManyAsync([])));

    [Fact]
    public async Task An_accounts_worklist_holds_only_what_it_still_owes()
    {
        // What a clerk taking a payment picks from. A draft is not on it, and neither is a bill
        // that has been withdrawn.
        var issued = await AnIssuedBillAsync("2026-06");
        var draft = await ADraftAsync("2026-05");
        var cancelled = await AnIssuedBillAsync("2026-04");

        await _host.WithBillsAsync(register => register.CancelAsync(
            cancelled.Id,
            new CancelBillInput("Billed against a disputed reading.")));

        var worklist = await WithDirectoryAsync(directory =>
            directory.OutstandingForAccountAsync(issued.ServiceAccountId, 50));

        Assert.Equal([issued.Id], worklist.Select(bill => bill.Id));
        Assert.DoesNotContain(worklist, bill => bill.Id == draft.Id);
        Assert.All(worklist, bill => Assert.True(bill.IsOutstanding));
    }

    [Fact]
    public async Task Another_accounts_bills_are_never_on_this_ones_worklist()
    {
        var mine = await AnIssuedBillAsync("2026-06");

        await AnIssuedBillAsync("2026-07");

        var worklist = await WithDirectoryAsync(directory =>
            directory.OutstandingForAccountAsync(mine.ServiceAccountId, 50));

        Assert.All(worklist, bill => Assert.Equal(mine.ServiceAccountId, bill.ServiceAccountId));
    }
}
