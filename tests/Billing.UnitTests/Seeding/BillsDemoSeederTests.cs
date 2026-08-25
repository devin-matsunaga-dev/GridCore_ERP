using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Seeding;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using GridCore.Platform.Monetary;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Billing.UnitTests.Seeding;

/// <summary>
/// The demo world's bills. Every one goes through the real rate engine, the real selector and the
/// real aggregate, so an impossible demo figure fails here rather than shipping a number nothing
/// explains — the call <c>MetersDemoSeeder</c> and <c>MeterReadingsDemoSeeder</c> both make.
/// </summary>
public class BillsDemoSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A demo world in miniature: a premise with an account and a year of monthly cycles behind it,
    /// laid down through the two directory fakes exactly as Metering and Customers would.
    /// </summary>
    private static BillingTestHost SeededWorld(string? commercialAccountNumber = null)
    {
        var host = new BillingTestHost(new FakeClock(Now));

        var premise = Guid.CreateVersion7();

        host.Accounts.Add(premise, accountNumber: commercialAccountNumber ?? "A-000001");

        var thisMonth = new DateOnly(Now.Year, Now.Month, 1);

        for (var cycle = BillsDemoSeeder.Cycles; cycle >= 1; cycle--)
        {
            var readAt = thisMonth.AddMonths(-cycle);

            host.Readings.Add(
                premise,
                600m,
                readAt.ToString("yyyy-MM", null),
                new DateTimeOffset(readAt.Year, readAt.Month, 1, 9, 0, 0, TimeSpan.Zero));
        }

        return host;
    }

    /// <summary>
    /// A demo world with two served premises, which is what the corrections need — one bill is
    /// credited and another charged, and they are deliberately not on the same account.
    /// </summary>
    private static BillingTestHost ATwoAccountWorld()
    {
        var host = new BillingTestHost(new FakeClock(Now));

        var thisMonth = new DateOnly(Now.Year, Now.Month, 1);

        foreach (var ordinal in new[] { 1, 2 })
        {
            var premise = Guid.CreateVersion7();

            host.Accounts.Add(premise, accountNumber: $"A-00000{ordinal}");

            for (var cycle = BillsDemoSeeder.Cycles; cycle >= 1; cycle--)
            {
                var readAt = thisMonth.AddMonths(-cycle);

                host.Readings.Add(
                    premise,
                    600m,
                    readAt.ToString("yyyy-MM", null),
                    new DateTimeOffset(readAt.Year, readAt.Month, 1, 9, 0, 0, TimeSpan.Zero));
            }
        }

        return host;
    }

    private static Task SeedAsync(BillingTestHost host) =>
        host.InScopeAsync(async services =>
        {
            await services.GetRequiredService<BillsDemoSeeder>().SeedAsync(default);
            await services.GetRequiredService<Platform.Data.IUnitOfWork>().ExecuteAsync(_ => Task.CompletedTask);

            return true;
        });

    [Fact]
    public async Task The_seeder_bills_every_cycle_it_finds()
    {
        using var host = SeededWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        Assert.Equal(BillsDemoSeeder.Cycles, await context.Bills.CountAsync());
    }

    [Fact]
    public async Task Every_seeded_bill_adds_up_to_its_own_lines()
    {
        // The money guard across the whole demo world. A seeded figure nothing explains is worse
        // than no demo data at all.
        using var host = SeededWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        var bills = await context.Bills.Include(bill => bill.Lines).ToListAsync();

        Assert.All(bills, bill => Assert.Equal(bill.TotalAmount, Money.Total(bill.Lines.Select(line => line.Amount))));
        Assert.All(bills, bill => Assert.True(Money.IsRounded(bill.TotalAmount)));
    }

    [Fact]
    public async Task The_demo_world_opens_with_bills_in_several_states()
    {
        // A billing officer opens the demo with work to do: drafts to issue and overdue money to
        // chase. Every one of these is a real transition through the real aggregate.
        using var host = SeededWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        var statuses = await context.Bills
            .GroupBy(bill => bill.Status)
            .Select(group => group.Key)
            .ToListAsync();

        Assert.Contains(BillStatus.Draft, statuses);
        Assert.Contains(BillStatus.Overdue, statuses);
    }

    [Fact]
    public async Task Nothing_in_the_demo_world_is_paid()
    {
        // PartiallyPaid and Paid are reachable — Bill.RecordPayment exists and is unit-tested — but
        // a payment invented here would be money with no record in the Payments module of where it
        // came from. WP-2.5 owns paying these bills.
        using var host = SeededWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        Assert.Equal(0, await context.Bills.CountAsync(bill => bill.AmountPaid != 0m));
        Assert.Equal(0, await context.Bills.CountAsync(bill =>
            bill.Status == BillStatus.Paid || bill.Status == BillStatus.PartiallyPaid));
    }

    [Fact]
    public async Task The_seeded_bills_straddle_the_tariff_revision()
    {
        // The point of shipping two versions of the residential tariff: the demo world has bills on
        // both sides of the repricing, priced on the rates that were in force at the time.
        using var host = SeededWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        var versions = await context.Bills
            .Select(bill => bill.RatePlanEffectiveFrom)
            .Distinct()
            .ToListAsync();

        Assert.Contains(DefaultRatePlans.OriginalEffectiveFrom, versions);
        Assert.Contains(DefaultRatePlans.ResidentialRevisionFrom, versions);

        // And a later bill for the same consumption really does cost more.
        var before = await context.Bills
            .Where(bill => bill.RatePlanEffectiveFrom == DefaultRatePlans.OriginalEffectiveFrom)
            .Select(bill => bill.TotalAmount)
            .FirstAsync();

        var after = await context.Bills
            .Where(bill => bill.RatePlanEffectiveFrom == DefaultRatePlans.ResidentialRevisionFrom)
            .Select(bill => bill.TotalAmount)
            .FirstAsync();

        Assert.True(after > before);
    }

    [Fact]
    public async Task The_commercial_account_is_put_on_the_commercial_tariff()
    {
        // So the demo world shows a tariff somebody chose beside the accounts that fall back to the
        // default — and so both tariff shapes appear on real bills.
        using var host = SeededWorld(BillsDemoSeeder.CommercialAccountNumber);

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        Assert.Equal(1, await context.AccountRatePlans.CountAsync());
        Assert.Equal(
            DefaultRatePlans.CommercialStandard,
            await context.AccountRatePlans.Select(row => row.RatePlanCode).SingleAsync());

        Assert.All(
            await context.Bills.Select(bill => bill.RatePlanCode).ToListAsync(),
            code => Assert.Equal(DefaultRatePlans.CommercialStandard, code));
    }

    [Fact]
    public async Task Seeded_bills_are_attributed_to_a_demo_colleague_who_holds_no_permissions()
    {
        // The demo: prefix cannot collide with an identity-provider subject, and the actor holds no
        // permissions at all — so nothing can be authorised as a demo colleague (WP-0.8).
        using var host = SeededWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        Assert.All(
            await context.Bills.Select(bill => bill.ActorId).ToListAsync(),
            actorId => Assert.StartsWith(DemoActor.IdPrefix, actorId, StringComparison.Ordinal));

        Assert.False(BillsDemoSeeder.Officer.HasPermission(Platform.Security.Permissions.Billing.Generate));
    }

    [Fact]
    public async Task Bill_numbers_start_at_one_so_a_real_run_continues_the_series()
    {
        // The seeder assigns its own numbers rather than calling the generator: its rows are not
        // visible to a query inside the seeding transaction (WP-1.1's lesson).
        using var host = SeededWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        var numbers = await context.Bills.Select(bill => bill.BillNumber).OrderBy(number => number).ToListAsync();

        Assert.Equal("BIL-000001", numbers[0]);
        Assert.Equal(numbers.Count, numbers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_world_with_no_readings_seeds_nothing_and_does_not_throw()
    {
        // Not an error: a database seeded with no customers has no premises, so no meters, so no
        // readings and no bills. The seeder returns rather than failing startup.
        using var host = new BillingTestHost(new FakeClock(Now));

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        Assert.Equal(0, await context.Bills.CountAsync());
    }

    [Fact]
    public async Task Two_seeded_bills_carry_a_correction_one_each_way()
    {
        // WP-2.4's demo data. Both go through the real Bill.Adjust, so a correction the aggregate
        // would refuse fails here rather than shipping.
        using var host = ATwoAccountWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        var adjustments = await context.BillAdjustments.OrderBy(adjustment => adjustment.Id).ToListAsync();

        Assert.Equal(2, adjustments.Count);
        Assert.Equal([BillAdjustmentKind.Credit, BillAdjustmentKind.Charge], adjustments.Select(entry => entry.Kind));

        // Signed the way the money moves, on a bill each, and every one saying why.
        Assert.True(adjustments[0].Amount < 0m);
        Assert.True(adjustments[1].Amount > 0m);
        Assert.Distinct(adjustments.Select(entry => entry.BillId));
        Assert.All(adjustments, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));
        Assert.All(adjustments, entry => Assert.Equal(BillsDemoSeeder.Officer.UserId, entry.ActorId));
    }

    [Fact]
    public async Task A_corrected_demo_bill_still_prints_what_it_was_calculated_at()
    {
        // The point of the whole work package, in the demo world a reviewer opens: the document says
        // what it always said, and what is owed has moved.
        using var host = ATwoAccountWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        var corrected = await context.Bills
            .Include(bill => bill.Lines)
            .Where(bill => bill.AdjustmentTotal != 0m)
            .ToListAsync();

        Assert.Equal(2, corrected.Count);

        Assert.All(corrected, bill =>
        {
            Assert.Equal(bill.TotalAmount, Money.Total(bill.Lines.Select(line => line.Amount)));
            Assert.NotEqual(bill.TotalAmount, bill.AmountDue);
            Assert.True(bill.IsOutstanding);
        });
    }

    [Fact]
    public async Task A_demo_world_too_small_to_correct_two_bills_seeds_none()
    {
        // The single-account world every other test here uses. A seeder must not be the thing that
        // decides a small demo world is a failure.
        using var host = SeededWorld();

        await SeedAsync(host);

        await using var context = host.NewBillingContext();

        Assert.Equal(0, await context.BillAdjustments.CountAsync());
        Assert.NotEqual(0, await context.Bills.CountAsync());
    }

    [Fact]
    public void The_seeders_name_is_its_dedupe_key_and_never_changes() =>
        // A rename seeds a second year of bills onto a database that has already been seeded.
        Assert.Equal("billing.bills", new BillsDemoSeeder(null!, null!, null!, TimeProvider.System).Name);
}
