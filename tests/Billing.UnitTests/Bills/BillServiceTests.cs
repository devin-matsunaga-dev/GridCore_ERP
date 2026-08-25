using GridCore.Contracts.Events;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Monetary;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.UnitTests.Bills;

/// <summary>
/// The billing register over the real EF model, on SQLite in memory (CONVENTIONS.md rule C). The
/// customers and metering schemas are absent: both arrive through <c>Contracts</c> directories, so
/// two whole modules stand in as fakes and a billing run costs no container.
/// </summary>
public class BillServiceTests
{
    private const string Cycle = "2026-08";

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReadAt = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private static BillingTestHost NewHost() =>
        new(new FakeClock(Now), new FakeCurrentUser("auth0|officer", "A billing officer"));

    private static Guid Premise() => Guid.CreateVersion7();

    [Fact]
    public async Task A_run_raises_one_draft_bill_per_billable_reading()
    {
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        var bill = Assert.Single(run.Bills);

        Assert.Equal(BillStatus.Draft, bill.Status);
        Assert.Equal(Cycle, bill.CycleCode);
        Assert.Equal(750m, bill.Consumption);
        Assert.Empty(run.Skipped);

        // Committed, not merely built: the run's unit of work is what makes the bill, its audit
        // entry and everything else one transaction.
        await using var context = host.NewBillingContext();

        Assert.Equal(1, await context.Bills.CountAsync());
        Assert.Equal(bill.Lines.Count, await context.BillLines.CountAsync());
    }

    [Fact]
    public async Task A_run_bills_the_period_the_meter_actually_measured()
    {
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt, periodDays: 31);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        var bill = Assert.Single(run.Bills);

        Assert.Equal(new DateOnly(2026, 7, 31), bill.PeriodStart);
        Assert.Equal(new DateOnly(2026, 8, 31), bill.PeriodEnd);
    }

    [Fact]
    public async Task A_run_prices_on_the_tariff_in_force_at_the_end_of_the_period_not_today()
    {
        // The effective-dating rule where it matters most. These two cycles are billed on the same
        // day; the June one must still come out on the January rates.
        using var host = NewHost();

        var june = Premise();
        var august = Premise();

        host.Accounts.Add(june);
        host.Accounts.Add(august);
        host.Readings.Add(june, 750m, "2026-06", new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero));
        host.Readings.Add(august, 750m, Cycle, ReadAt);

        var earlier = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput("2026-06")));
        var later = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Equal(DefaultRatePlans.OriginalEffectiveFrom, Assert.Single(earlier.Bills).RatePlanEffectiveFrom);
        Assert.Equal(DefaultRatePlans.ResidentialRevisionFrom, Assert.Single(later.Bills).RatePlanEffectiveFrom);

        // And the repricing is visible in the money, not just in the metadata.
        Assert.True(later.Bills[0].TotalAmount > earlier.Bills[0].TotalAmount);
    }

    [Fact]
    public async Task An_account_on_its_own_tariff_is_billed_on_that_one()
    {
        using var host = NewHost();

        var premise = Premise();
        var account = host.Accounts.Add(premise);

        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        await host.WithTariffsAsync(tariffs => tariffs.AssignAsync(account.Id, DefaultRatePlans.CommercialStandard));

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Equal(DefaultRatePlans.CommercialStandard, Assert.Single(run.Bills).RatePlanCode);
    }

    [Fact]
    public async Task An_account_nobody_has_assigned_is_billed_on_the_default()
    {
        // No row is an answer, not an omission: a migrated database with no billing setup at all
        // still produces correct bills.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Equal(DefaultRatePlans.DefaultCode, Assert.Single(run.Bills).RatePlanCode);
    }

    [Theory]
    [InlineData("HighUsage", "Reading is on the exception worklist (HighUsage)")]
    [InlineData("ZeroUsage", "Reading is on the exception worklist (ZeroUsage)")]
    public async Task A_reading_on_the_exception_worklist_is_not_billed(string code, string reason)
    {
        // Billing a flagged reading unseen is how a transposed digit reaches a customer as a demand
        // for four thousand dollars. The worklist is worked by hand first, and the run says so.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 4_000m, Cycle, ReadAt, exceptionCode: code);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Empty(run.Bills);
        Assert.Equal(reason, Assert.Single(run.Skipped).Reason);
    }

    [Fact]
    public async Task A_missing_read_is_not_billed()
    {
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, null, Cycle, ReadAt, exceptionCode: "MissingRead");

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Empty(run.Bills);
        Assert.Single(run.Skipped);
    }

    [Fact]
    public async Task A_metered_premise_with_no_open_account_is_not_billed()
    {
        // WP-2.1 seeds exactly this case on purpose: a meter is fitted to a place, so a new build
        // can be metered before anybody is billed there.
        using var host = NewHost();

        var premise = Premise();

        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Empty(run.Bills);
        Assert.Equal("No open service account at the premise", Assert.Single(run.Skipped).Reason);
    }

    [Fact]
    public async Task A_closed_account_does_not_hold_its_premise_so_nothing_is_billed_there()
    {
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise, status: "Closed");
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Empty(run.Bills);
        Assert.Equal("No open service account at the premise", Assert.Single(run.Skipped).Reason);
    }

    [Fact]
    public async Task An_account_that_was_never_energised_is_not_billed()
    {
        // Opened but never switched on. Nothing was supplied under this account, so the units on the
        // meter at its premise are not its units to be charged for.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise, status: "Pending", energised: false, accountNumber: "A-000099");
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Empty(run.Bills);
        Assert.Equal("Service account A-000099 has never been energised", Assert.Single(run.Skipped).Reason);
    }

    [Fact]
    public async Task A_disconnected_account_is_still_billed_for_what_it_used_before_the_cut()
    {
        // A disconnection leaves a balance and a premise allocated (WP-1.2). The units consumed
        // before the supply was cut are still owed.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise, status: "Disconnected");
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Single(run.Bills);
    }

    [Fact]
    public async Task A_reading_with_no_measured_period_is_not_billed()
    {
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.AddWithoutPeriod(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Equal("Reading covers no measured period", Assert.Single(run.Skipped).Reason);
    }

    [Fact]
    public async Task Re_running_a_cycle_skips_what_is_already_billed_rather_than_refusing_the_run()
    {
        // Deliberately unlike WP-2.2's reading cycle, which answers 409 on a re-run. Re-running a
        // billing cycle after clearing its exception worklist is ordinary work, so the accounts
        // already billed are skipped by name and the rest go through.
        using var host = NewHost();

        var first = Premise();
        var second = Premise();

        host.Accounts.Add(first);
        host.Accounts.Add(second);
        host.Readings.Add(first, 750m, Cycle, ReadAt);
        host.Readings.Add(second, 900m, Cycle, ReadAt, exceptionCode: "HighUsage");

        var before = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Single(before.Bills);

        var again = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Empty(again.Bills);
        Assert.Equal(2, again.SkippedCount);
        Assert.Contains("Already billed for this cycle", again.Skipped.Select(skipped => skipped.Reason));

        await using var context = host.NewBillingContext();

        Assert.Equal(1, await context.Bills.CountAsync());
    }

    [Fact]
    public async Task Two_meters_at_one_premise_in_one_cycle_raise_one_bill()
    {
        // The in-run half of the same guard. Without it the second reading would raise a bill the
        // unique index refuses at commit, losing the WHOLE run instead of one reading.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);
        host.Readings.Add(premise, 100m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Single(run.Bills);
        Assert.Equal("Already billed for this cycle", Assert.Single(run.Skipped).Reason);
    }

    [Fact]
    public async Task Bill_numbers_run_in_sequence_across_a_whole_batch()
    {
        // The bug this is here for: bills added to the context are invisible to the query that
        // issues the next number, so a run that asked once per bill would hand out BIL-000001 three
        // times and lose two of them to the unique index.
        using var host = NewHost();

        foreach (var _ in Enumerable.Range(0, 3))
        {
            var premise = Premise();

            host.Accounts.Add(premise);
            host.Readings.Add(premise, 750m, Cycle, ReadAt);
        }

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Equal(["BIL-000001", "BIL-000002", "BIL-000003"], run.Bills.Select(bill => bill.BillNumber));
    }

    [Fact]
    public async Task A_later_run_continues_the_number_series()
    {
        using var host = NewHost();

        var first = Premise();
        var second = Premise();

        host.Accounts.Add(first);
        host.Accounts.Add(second);
        host.Readings.Add(first, 750m, Cycle, ReadAt);
        host.Readings.Add(second, 750m, "2026-09", new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero));

        await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        var later = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput("2026-09")));

        Assert.Equal("BIL-000002", Assert.Single(later.Bills).BillNumber);
    }

    [Fact]
    public async Task An_unread_cycle_bills_nothing_and_is_not_an_error()
    {
        // Not a conflict: an unread cycle is one nobody has run yet, and telling the caller
        // "nothing to bill" beats a 409 they cannot act on.
        using var host = NewHost();

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput("2099-01")));

        Assert.Empty(run.Bills);
        Assert.Empty(run.Skipped);
    }

    [Fact]
    public async Task A_run_with_no_cycle_code_is_refused()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<BillingValidationException>(() =>
            host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput("   "))));
    }

    [Fact]
    public async Task A_run_leaves_one_audit_entry_naming_the_cycle()
    {
        // ONE entry for the run, not one per bill — WP-2.2's call about a reading cycle. What an
        // auditor asks is "who billed the August cycle and what came out of it".
        using var host = NewHost();

        var billed = Premise();
        var unbilled = Premise();

        host.Accounts.Add(billed);
        host.Readings.Add(billed, 750m, Cycle, ReadAt);
        host.Readings.Add(unbilled, 750m, Cycle, ReadAt);

        await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        await using var context = host.NewPlatformContext();

        var entry = await context.AuditEntries.SingleAsync(audit => audit.Action == AuditActions.BillingRunExecuted);

        Assert.Equal(AuditEntityTypes.BillingRun, entry.EntityType);
        Assert.Equal(Cycle, entry.EntityId);
        Assert.Equal("auth0|officer", entry.UserId);

        // The reasons are in the entry, so an auditor can see what was NOT billed and why.
        Assert.Contains("No open service account", entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_publishes_nothing()
    {
        // A draft is not a fact anybody outside Billing needs. Finance posts the receivable when a
        // bill is ISSUED, which is the act that makes it money the utility is owed.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Empty(host.Events.Published);
    }

    [Fact]
    public async Task Issuing_a_bill_publishes_BillIssued_for_Finance()
    {
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));
        var draft = run.Bills[0];

        var issued = await host.WithBillsAsync(bills => bills.IssueAsync(draft.Id, new IssueBillInput()));

        var @event = host.Events.Single<BillIssued>();

        Assert.Equal(issued.Id, @event.BillId);
        Assert.Equal(issued.BillNumber, @event.BillNumber);
        Assert.Equal(issued.ServiceAccountId, @event.ServiceAccountId);
        Assert.Equal(issued.CustomerId, @event.CustomerId);
        Assert.Equal(issued.TotalAmount, @event.Amount);
        Assert.Equal(issued.Currency, @event.Currency);
        Assert.Equal(issued.PeriodStart, @event.PeriodStart);
        Assert.Equal(issued.PeriodEnd, @event.PeriodEnd);
        Assert.Equal(issued.DueDate, @event.DueDate);
    }

    [Fact]
    public async Task Issuing_defaults_the_due_date_to_the_standard_term()
    {
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));
        var issued = await host.WithBillsAsync(bills => bills.IssueAsync(run.Bills[0].Id, new IssueBillInput()));

        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        Assert.Equal(today, issued.IssuedOn);
        Assert.Equal(today.AddDays(BillingTerms.DueDays), issued.DueDate);
    }

    [Fact]
    public async Task Issuing_is_audited_with_the_state_before_and_after()
    {
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        await host.WithBillsAsync(bills => bills.IssueAsync(run.Bills[0].Id, new IssueBillInput()));

        await using var context = host.NewPlatformContext();

        var entry = await context.AuditEntries.SingleAsync(audit => audit.Action == AuditActions.BillIssued);

        Assert.Equal(AuditEntityTypes.Bill, entry.EntityType);
        Assert.Contains("Draft", entry.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("Issued", entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Issuing_a_bill_that_is_not_a_draft_is_refused()
    {
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        await host.WithBillsAsync(bills => bills.IssueAsync(run.Bills[0].Id, new IssueBillInput()));

        await Assert.ThrowsAsync<BillingWorkflowException>(() =>
            host.WithBillsAsync(bills => bills.IssueAsync(run.Bills[0].Id, new IssueBillInput())));

        // Nothing was published the second time — the outbox row and the state change share a
        // transaction, so a refused write publishes nothing.
        Assert.Single(host.Events.Published);
    }

    [Fact]
    public async Task Issuing_a_bill_that_does_not_exist_is_a_404()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<BillNotFoundException>(() =>
            host.WithBillsAsync(bills => bills.IssueAsync(Guid.CreateVersion7(), new IssueBillInput())));
    }

    [Fact]
    public async Task Cancelling_a_bill_is_audited_and_publishes_nothing()
    {
        // No event: Finance posted a receivable on BillIssued and reversing it is a journal decision
        // WP-2.6 owns. An event raised now would be one nothing consumes.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        await host.WithBillsAsync(bills => bills.IssueAsync(run.Bills[0].Id, new IssueBillInput()));

        var cancelled = await host.WithBillsAsync(bills =>
            bills.CancelAsync(run.Bills[0].Id, new CancelBillInput("Reading disputed; re-read arranged.")));

        Assert.Equal(BillStatus.Cancelled, cancelled.Status);
        Assert.Single(host.Events.Published);

        await using var context = host.NewPlatformContext();

        Assert.True(await context.AuditEntries.AnyAsync(audit => audit.Action == AuditActions.BillCancelled));
    }

    [Fact]
    public async Task An_overdue_review_moves_only_what_is_past_due_and_still_owed()
    {
        using var host = NewHost();

        var late = Premise();
        var current = Premise();

        host.Accounts.Add(late);
        host.Accounts.Add(current);
        host.Readings.Add(late, 750m, Cycle, ReadAt);
        host.Readings.Add(current, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        // One issued long ago and one issued today; only the first can be overdue.
        await host.WithBillsAsync(bills => bills.IssueAsync(
            run.Bills[0].Id,
            new IssueBillInput(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 22))));

        await host.WithBillsAsync(bills => bills.IssueAsync(run.Bills[1].Id, new IssueBillInput()));

        var review = await host.WithBillsAsync(bills => bills.ReviewOverdueAsync(new OverdueReviewInput()));

        Assert.Equal(1, review.MarkedOverdue);
        Assert.Equal(run.Bills[0].Id, review.Bills[0].Id);
        Assert.Equal(review.Bills[0].Balance, review.TotalOverdue);

        await using var context = host.NewBillingContext();

        Assert.Equal(1, await context.Bills.CountAsync(bill => bill.Status == BillStatus.Overdue));
        Assert.Equal(1, await context.Bills.CountAsync(bill => bill.Status == BillStatus.Issued));
    }

    [Fact]
    public async Task An_overdue_review_leaves_drafts_alone()
    {
        // A draft was never sent, so it cannot be late. The database filter and the aggregate both
        // say so; this proves the pair agree.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        var review = await host.WithBillsAsync(bills =>
            bills.ReviewOverdueAsync(new OverdueReviewInput(new DateOnly(2030, 1, 1))));

        Assert.Equal(0, review.MarkedOverdue);
    }

    [Fact]
    public async Task An_overdue_review_is_audited_even_when_it_finds_nothing()
    {
        using var host = NewHost();

        await host.WithBillsAsync(bills => bills.ReviewOverdueAsync(new OverdueReviewInput()));

        await using var context = host.NewPlatformContext();

        var entry = await context.AuditEntries.SingleAsync(audit => audit.Action == AuditActions.BillOverdueReviewed);

        Assert.Equal(AuditEntityTypes.BillOverdueReview, entry.EntityType);
        Assert.Equal("2026-09-02", entry.EntityId);
    }

    [Fact]
    public async Task The_register_filters_but_does_not_sort_or_page()
    {
        using var host = NewHost();

        var mine = Premise();
        var theirs = Premise();

        var account = host.Accounts.Add(mine);

        host.Accounts.Add(theirs);
        host.Readings.Add(mine, 750m, Cycle, ReadAt);
        host.Readings.Add(theirs, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Equal(2, run.Raised);

        var byAccount = await host.WithBillsAsync(bills => bills.ListAsync(new BillQuery(ServiceAccountId: account.Id)));
        var byStatus = await host.WithBillsAsync(bills => bills.ListAsync(new BillQuery(Status: BillStatus.Draft)));
        var byCycle = await host.WithBillsAsync(bills => bills.ListAsync(new BillQuery(CycleCode: Cycle)));
        var outstanding = await host.WithBillsAsync(bills => bills.ListAsync(new BillQuery(OutstandingOnly: true)));

        Assert.Equal(account.Id, Assert.Single(byAccount).ServiceAccountId);
        Assert.Equal(2, byStatus.Count);
        Assert.Equal(2, byCycle.Count);

        // Nothing is outstanding until something is issued.
        Assert.Empty(outstanding);
    }

    [Fact]
    public async Task A_list_does_not_load_the_lines_and_a_single_bill_does()
    {
        // A page of fifty bills does not want two hundred lines it will not render.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 1_500m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        var listed = Assert.Single(await host.WithBillsAsync(bills => bills.ListAsync(new BillQuery())));
        var found = await host.WithBillsAsync(bills => bills.FindAsync(run.Bills[0].Id));

        Assert.Empty(listed.Lines);
        Assert.Equal(4, found!.Lines.Count);
        Assert.Equal([1, 2, 3, 4], found.Lines.Select(line => line.Sequence));
    }

    [Fact]
    public async Task A_bill_that_does_not_exist_is_null_rather_than_a_throw()
    {
        using var host = NewHost();

        Assert.Null(await host.WithBillsAsync(bills => bills.FindAsync(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task Everything_the_run_reads_goes_through_the_two_contract_seams()
    {
        // The boundary rule, asserted rather than assumed. Billing has never heard of a metering or
        // a customers schema; if it ever did, this host would not have one to read.
        using var host = NewHost();

        var premise = Premise();

        host.Accounts.Add(premise);
        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Equal([Cycle], host.Readings.Cycles);
        Assert.Contains(premise, host.Accounts.Lookups);
    }

    [Fact]
    public async Task A_run_over_many_premises_makes_one_account_lookup()
    {
        // Batched, the shape WP-2.1 established for premises: one boundary call per run rather than
        // one per meter.
        using var host = NewHost();

        foreach (var _ in Enumerable.Range(0, 5))
        {
            var premise = Premise();

            host.Accounts.Add(premise);
            host.Readings.Add(premise, 750m, Cycle, ReadAt);
        }

        await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Equal(5, host.Accounts.Lookups.Count);
    }

    [Fact]
    public async Task Every_bill_a_run_produces_adds_up_to_its_own_lines()
    {
        // The money guard at the level a run owes it, across a batch with different consumptions.
        using var host = NewHost();

        foreach (var consumption in new[] { 0m, 1m, 499m, 500m, 1_001m, 12_345.678m })
        {
            var premise = Premise();

            host.Accounts.Add(premise);
            host.Readings.Add(premise, consumption, Cycle, ReadAt);
        }

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Equal(6, run.Raised);
        Assert.All(run.Bills, bill => Assert.Equal(bill.TotalAmount, Money.Total(bill.Lines.Select(line => line.Amount))));
        Assert.Equal(Money.Total(run.Bills.Select(bill => bill.TotalAmount)), run.TotalBilled);
    }
}
