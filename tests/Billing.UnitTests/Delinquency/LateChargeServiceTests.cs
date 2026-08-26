using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Delinquency;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Billing.UnitTests.Delinquency;

/// <summary>
/// The late-charge run (WP-2.19): one per cent a month of what is <b>past due</b>, once per bill per
/// period, raised as an ordinary WP-2.16 fee. The billing schema and the platform schema share one
/// SQLite connection here, so a charge, its assessment row and the run's audit entry really do
/// commit together.
/// </summary>
public class LateChargeServiceTests
{
    private const string Cycle = "2026-06";

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReadAt = new(2026, 6, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);
    private static readonly DateOnly Period = new(2026, 9, 1);

    /// <summary>Whoever took the money in a fixture. The tests are about the charge, not the cashier.</summary>
    private static readonly RegistryActor Payer = new("auth0|cashier", "A cashier");

    private static BillingTestHost NewHost() =>
        new(new FakeClock(Now), new FakeCurrentUser("auth0|clerk", "A billing officer"));

    /// <summary>
    /// Issues a bill against a fresh account and leaves it past due, handing back the bill.
    /// </summary>
    /// <remarks>
    /// The bill is raised by the real cycle run rather than built by hand, so what the late charge is
    /// taken on is a balance the rate engine actually produced — which is the figure the assertions
    /// below are about.
    /// </remarks>
    private static async Task<Bill> APastDueBillAsync(BillingTestHost host, DateOnly dueDate, decimal? payment = null)
    {
        var premise = Guid.CreateVersion7();
        var account = host.Accounts.Add(premise);

        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));
        var draft = run.Bills.Single(bill => bill.ServiceAccountId == account.Id);

        var issued = await host.WithBillsAsync(bills =>
            bills.IssueAsync(draft.Id, new IssueBillInput(dueDate.AddDays(-21), dueDate)));

        if (payment is { } paid)
        {
            // Part-paid through the register's own method, so the balance the run reads is the one
            // WP-2.4 defines: printed total plus corrections, less what has been paid.
            await host.InScopeAsync(async services =>
            {
                var context = services.GetRequiredService<Data.BillingDbContext>();
                var bill = await context.Bills.SingleAsync(candidate => candidate.Id == issued.Id);

                bill.RecordPayment(paid, Payer, Now, "Part payment at the counter.");

                await context.SaveChangesAsync();

                return true;
            });
        }

        return issued;
    }

    [Fact]
    public async Task The_one_per_cent_is_taken_on_the_past_due_BALANCE_and_not_on_the_bill_total()
    {
        // WORK_PACKAGES.md asks for exactly this, and it is the difference the whole rate basis
        // exists to express: a bill whose total is one figure and whose balance is another must be
        // charged on the second.
        using var host = NewHost();

        var bill = await APastDueBillAsync(host, Today.AddDays(-40), payment: 60.00m);

        var run = await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));

        var assessment = Assert.Single(run.Assessed);

        await using var context = host.NewBillingContext();

        // Re-read, because the entity the fixture handed back predates the part payment it made.
        var current = await context.Bills.SingleAsync(candidate => candidate.Id == bill.Id);

        Assert.True(current.TotalAmount > current.Balance, "The fixture must leave the two figures different.");
        Assert.Equal(current.Balance, assessment.BasisAmount);
        Assert.NotEqual(current.TotalAmount, assessment.BasisAmount);
        Assert.Equal(decimal.Round(current.Balance * 0.01m, 2, MidpointRounding.AwayFromZero), assessment.Amount);
    }

    [Fact]
    public async Task Running_the_job_twice_charges_once()
    {
        // The verify list's headline. The assessment row is the idempotency; the unique index is what
        // makes it a fact about the database rather than a property of whichever run looked first.
        using var host = NewHost();

        await APastDueBillAsync(host, Today.AddDays(-40));

        var first = await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));
        var second = await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));

        Assert.Equal(1, first.ChargedCount);
        Assert.Equal(0, second.ChargedCount);

        var skipped = Assert.Single(second.Skipped);

        Assert.Contains("Already charged", skipped.Reason, StringComparison.Ordinal);

        await using var context = host.NewBillingContext();

        Assert.Equal(1, await context.LateChargeAssessments.CountAsync());
        Assert.Equal(1, await context.AccountCharges.CountAsync(charge => charge.Code == FeeCode.LateCharge));
    }

    [Fact]
    public async Task A_bill_still_unpaid_the_following_month_is_charged_again()
    {
        // "One per cent PER MONTH". The idempotency is per bill per period, not per bill — a bill
        // three months late is charged three times, and a register that refused the second would be
        // charging a flat fee with extra steps.
        using var host = NewHost();

        await APastDueBillAsync(host, Today.AddDays(-40));

        var september = await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));
        var october = await host.WithLateChargesAsync(charges =>
            charges.RunAsync(new LateChargeRunInput(new DateOnly(2026, 10, 5))));

        Assert.Equal(1, september.ChargedCount);
        Assert.Equal(1, october.ChargedCount);

        Assert.Equal(Period, september.PeriodStart);
        Assert.Equal(new DateOnly(2026, 10, 1), october.PeriodStart);
    }

    [Fact]
    public async Task A_bill_that_is_not_yet_due_is_never_charged()
    {
        using var host = NewHost();

        await APastDueBillAsync(host, Today.AddDays(14));

        var run = await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));

        Assert.Empty(run.Assessed);
        Assert.Empty(run.Skipped);
    }

    [Fact]
    public async Task The_charge_stamps_the_schedule_row_the_rate_and_the_basis()
    {
        // The owner's requirement, stated three columns at a time: schedule row, rate used, basis
        // charged on, and the figure that came out. Together they reproduce the amount years later
        // without re-running an arrears query over a register that has moved on.
        using var host = NewHost();

        await APastDueBillAsync(host, Today.AddDays(-40));

        await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));

        var row = FeeSchedules.InForceOn(FeeCode.LateCharge, Today)!;

        await using var context = host.NewBillingContext();

        var charge = await context.AccountCharges.SingleAsync(candidate => candidate.Code == FeeCode.LateCharge);
        var assessment = await context.LateChargeAssessments.SingleAsync();

        Assert.Equal(FeeBasis.Rate, charge.Basis);
        Assert.Equal(row.Id, charge.FeeScheduleId);
        Assert.Equal(FeeSchedules.LateChargeMonthlyRate, charge.Rate);
        Assert.NotNull(charge.BasisAmount);
        Assert.Equal(charge.BasisAmount!.Value * FeeSchedules.LateChargeMonthlyRate, charge.Amount, 2);

        // And the assessment says the same thing, so "was this bill charged for September" is
        // answerable without joining to a charge that may since have been withdrawn.
        Assert.Equal(charge.Id, assessment.AccountChargeId);
        Assert.Equal(charge.Rate, assessment.Rate);
        Assert.Equal(charge.BasisAmount, assessment.BasisAmount);
        Assert.Equal(charge.Amount, assessment.Amount);
        Assert.Equal(row.Id, assessment.FeeScheduleId);
    }

    [Fact]
    public async Task A_charge_that_rounds_away_to_nothing_is_skipped_and_no_assessment_is_written()
    {
        // One per cent of forty cents is four tenths of a cent. AccountCharge.Raise refuses a line
        // reading 0.00, and writing an assessment anyway would write the balance off — the bill is
        // simply charged next month, once it has grown past the rounding floor.
        using var host = NewHost();

        var bill = await APastDueBillAsync(host, Today.AddDays(-40));

        await host.InScopeAsync(async services =>
        {
            var context = services.GetRequiredService<Data.BillingDbContext>();
            var current = await context.Bills.SingleAsync(candidate => candidate.Id == bill.Id);

            current.RecordPayment(current.Balance - 0.40m, Payer, Now, "All but forty cents.");

            await context.SaveChangesAsync();

            return true;
        });

        var run = await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));

        Assert.Empty(run.Assessed);
        Assert.Contains("rounds to nothing", Assert.Single(run.Skipped).Reason, StringComparison.Ordinal);

        await using var context = host.NewBillingContext();

        Assert.Equal(0, await context.LateChargeAssessments.CountAsync());
    }

    [Fact]
    public async Task Each_past_due_bill_is_charged_on_its_own_balance_rather_than_the_account_total()
    {
        // Idempotency is "per bill per period" and so is the arithmetic: a customer two bills behind
        // is late twice over, each for its own figure, and one charge on the account total would be
        // a figure no line of the ageing accounts for.
        using var host = NewHost();

        await APastDueBillAsync(host, Today.AddDays(-40));
        await APastDueBillAsync(host, Today.AddDays(-70));

        var run = await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));

        Assert.Equal(2, run.ChargedCount);
        Assert.Equal(run.TotalCharged, run.Assessed.Sum(assessment => assessment.Amount));
    }

    [Fact]
    public async Task A_run_can_be_narrowed_to_one_account()
    {
        using var host = NewHost();

        var mine = await APastDueBillAsync(host, Today.AddDays(-40));
        await APastDueBillAsync(host, Today.AddDays(-70));

        var run = await host.WithLateChargesAsync(charges =>
            charges.RunAsync(new LateChargeRunInput(Today, mine.ServiceAccountId)));

        Assert.Equal(mine.Id, Assert.Single(run.Assessed).BillId);
    }

    [Fact]
    public async Task The_run_writes_one_audit_entry_naming_the_period_and_the_rate()
    {
        // One entry for the run, the shape a billing run and an overdue review already take. Each
        // charge it raised carries its own AccountChargeRaised entry.
        using var host = NewHost();

        await APastDueBillAsync(host, Today.AddDays(-40));

        var run = await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));

        await using var context = host.NewPlatformContext();

        var entry = await context.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.LateChargeRun);

        Assert.Equal(AuditEntityTypes.LateChargeRun, entry.EntityType);
        Assert.Equal("2026-09", entry.EntityId);
        // 0.01 rather than 0.0100: the snapshot is JSON and a decimal serialises without its
        // trailing zeroes. What matters is that the rate the run took is in the entry at all.
        Assert.Contains("\"rate\":0.01", entry.AfterJson!, StringComparison.Ordinal);
        Assert.Contains(run.TotalCharged.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_the_late_charges_without_permission_is_refused()
    {
        // THE FAILURE PATH. Refused before a single bill is read — the run raises a published fee
        // against every past-due account on the island, and billing.charge is what names that act.
        using var host = NewHost();

        await APastDueBillAsync(host, Today.AddDays(-40));

        var clerk = FakeCurrentUser.Holding(Permissions.Billing.Read);

        var refusal = await Assert.ThrowsAsync<BillingPermissionException>(() =>
            host.AsAsync(clerk, charges => charges.RunAsync(new LateChargeRunInput(Today))));

        Assert.Contains(Permissions.Billing.Charge, refusal.Message, StringComparison.Ordinal);

        await using var context = host.NewBillingContext();

        Assert.Equal(0, await context.LateChargeAssessments.CountAsync());
    }

    [Fact]
    public async Task A_rate_fee_cannot_be_raised_from_the_desk_because_there_is_no_basis_to_raise_it_on()
    {
        // The counterpart of "there is no amount field". A rate fee has no figure until something is
        // charged on it, and nothing a rep could type would be anything but an invented balance.
        using var host = NewHost();

        var account = host.Accounts.Add(Guid.CreateVersion7());

        var refusal = await Assert.ThrowsAsync<BillingValidationException>(() =>
            host.WithChargesAsync(charges => charges.RaiseAsync(
                new RaiseChargeInput(account.Id, FeeCode.LateCharge, "Typed at the counter."))));

        Assert.Contains("balance to charge on", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flat_fee_refuses_a_basis()
    {
        // The other half of the same rule: a caller that supplied one was expecting arithmetic that
        // is not going to happen, and answering with the published figure regardless would hide it.
        using var host = NewHost();

        var account = host.Accounts.Add(Guid.CreateVersion7());

        var refusal = await Assert.ThrowsAsync<BillingValidationException>(() =>
            host.WithChargesAsync(charges => charges.RaiseAsync(
                new RaiseChargeInput(account.Id, FeeCode.Reconnection, "Supply restored.", Today, BasisAmount: 200.00m))));

        Assert.Contains("flat fee", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_late_charge_lands_on_the_next_cycle_bill_like_any_other_fee()
    {
        // It is an ordinary WP-2.16 account charge, which is the point of raising it through
        // IAccountChargeService rather than writing one here: it lands on a bill, it can be withdrawn,
        // and Finance credits fee revenue for it.
        using var host = NewHost();

        var bill = await APastDueBillAsync(host, Today.AddDays(-40));

        await host.WithLateChargesAsync(charges => charges.RunAsync(new LateChargeRunInput(Today)));

        await using var context = host.NewBillingContext();

        var charge = await context.AccountCharges.SingleAsync(candidate => candidate.Code == FeeCode.LateCharge);

        Assert.Equal(AccountChargeStatus.Pending, charge.Status);
        Assert.Equal(bill.ServiceAccountId, charge.ServiceAccountId);
        Assert.Contains("September 2026", charge.Reason, StringComparison.Ordinal);
    }
}
