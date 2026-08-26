using GridCore.Contracts.Services;
using GridCore.Contracts.Events;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Platform.Monetary;
using GridCore.Platform.Seeding;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GridCore.IntegrationTests;

/// <summary>
/// The revenue cycle's first half against real Postgres: a customer, a premise, an account, a meter,
/// a reading cycle and a bill raised from it — four modules, four schemas, one transaction each.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves the tiered arithmetic, the effective dating, the state machine and every
/// skip rule with no infrastructure at all — that is where nearly all of WP-2.3's cases live. What
/// only a container can show is what the <b>seams</b> do: that Billing really can raise a bill
/// against an account and a reading it reaches through <c>Contracts</c> interfaces implemented by
/// two other modules over two other schemas, and that Postgres itself guarantees the rest — exact
/// <c>numeric(18,2)</c> money, and one bill per account per cycle.
/// </para>
/// <para>
/// This is the cross-module effect WP-2.3 adds, so CONVENTIONS.md's "new cross-module effect → one
/// integration test" is what this file is. WP-2.4 adds the second one — a correction to an issued
/// bill, staged for Finance in the same transaction as the entry and the audit row.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BillingRegistryTests(GateFixture fixture) : IAsyncLifetime
{
    private const string Cycle = "2026-08";

    /// <summary>
    /// The credit these tests apply. Comfortably under the smallest bill the simulated cycle can
    /// produce: every bill carries the tariff's monthly service charge, which is 12.50 on the oldest
    /// published version and higher on every later one. A credit larger than the balance is refused,
    /// and that guard is the fast tier's to prove.
    /// </summary>
    private const decimal Credit = 5.00m;

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A premise with an energised account and a meter on it — the state a billing run starts from,
    /// assembled through three modules' own services exactly as the application would.
    /// </summary>
    private async Task<(Guid AccountId, Guid MeterId)> AServedPremiseAsync(
        string customerName,
        string line1,
        string serialNumber,
        decimal installationReading)
    {
        Guid premise;
        Guid customer;

        await using (var scope = fixture.CreateScope())
        {
            customer = (await scope.ServiceProvider.GetRequiredService<ICustomerService>()
                .RegisterAsync(new RegisterCustomerInput(customerName, CustomerClass.Residential, "Maria Sablan")))
                .Id;

            premise = (await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
                .RegisterAsync(new ServiceLocationInput(
                    Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"),
                    "Meter on the north wall",
                    IsActive: true,
                    null)))
                .Id;
        }

        Guid account;

        await using (var scope = fixture.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

            account = (await accounts.OpenAsync(new OpenServiceAccountInput(customer, premise, ServiceType.Electricity, "Requested at the counter"))).Id;

            // Energised: an account that never was is deliberately not billed for the units on the
            // meter at its premise.
            await accounts.StartServiceAsync(account, "Connected.");
        }

        await using (var scope = fixture.CreateScope())
        {
            var meters = scope.ServiceProvider.GetRequiredService<IMeterService>();

            var meter = await meters.RegisterAsync(
                new RegisterMeterInput(serialNumber, MeterType.SinglePhase, Manufacturer: "Sensus"));

            await meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise, installationReading));

            return (account, meter.Meter.Id);
        }
    }

    private async Task ARecordedReadingAsync(Guid meterId, decimal reading)
    {
        await using var scope = fixture.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
            .RecordAsync(meterId, new RecordReadingInput(reading, Note: "Read off the card"));
    }

    [Fact]
    public async Task A_bill_is_raised_through_the_seams_against_another_modules_account_and_reading()
    {
        // The headline of the work package, end to end across four schemas.
        var (account, meter) = await AServedPremiseAsync(
            "Sablan Family Residence",
            "128 As Nieves Road",
            "SEN-2300101",
            installationReading: 14_820.000m);

        await ARecordedReadingAsync(meter, 15_420.000m);

        // A manual reading carries no cycle code, so the run needs one of its own: read the meter as
        // part of a cycle.
        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput(Cycle, Seed: 4471));
        }

        BillingRunResult run;

        await using (var scope = fixture.CreateScope())
        {
            run = await scope.ServiceProvider.GetRequiredService<IBillService>().RunAsync(new RunBillingInput(Cycle));
        }

        var bill = Assert.Single(run.Bills);

        Assert.Equal(account, bill.ServiceAccountId);
        Assert.Equal(BillStatus.Draft, bill.Status);

        // Everything a bill needs to be read is on it, resolved across the boundary once and stamped.
        Assert.StartsWith("A-", bill.AccountNumber, StringComparison.Ordinal);
        Assert.Equal("Sablan Family Residence", bill.CustomerName);
        Assert.StartsWith("MTR-", bill.MeterNumber, StringComparison.Ordinal);

        await using (var scope = fixture.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

            var stored = await billing.Bills
                .Include(candidate => candidate.Lines)
                .SingleAsync(candidate => candidate.Id == bill.Id);

            // numeric(18,2) round-trips the money exactly, and the document still adds up after it
            // has been through the database.
            Assert.Equal(bill.TotalAmount, stored.TotalAmount);
            Assert.Equal(stored.TotalAmount, Money.Total(stored.Lines.Select(line => line.Amount)));

            // And numeric(18,3) round-trips the units it was raised from.
            Assert.Equal(bill.Consumption, stored.Consumption);
        }
    }

    [Fact]
    public async Task An_account_cannot_be_billed_twice_for_one_cycle()
    {
        // ux_bills_account_cycle, past the service's own pre-check. The index is what makes it
        // impossible rather than merely unlikely — a re-run that raced the check would otherwise
        // double a customer's charges.
        var (account, meter) = await AServedPremiseAsync(
            "Camacho Store",
            "9 Chalan Kanoa Street",
            "SEN-2300102",
            installationReading: 3_000.000m);

        await ARecordedReadingAsync(meter, 3_500.000m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput(Cycle, Seed: 4471));
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBillService>().RunAsync(new RunBillingInput(Cycle));
        }

        await using (var scope = fixture.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

            var first = await billing.Bills.AsNoTracking().FirstAsync(bill => bill.CycleCode == Cycle);

            // Inserted straight past the service, exactly as a second concurrent run would.
            var duplicate = await billing.Bills
                .AsNoTracking()
                .Where(bill => bill.Id == first.Id)
                .Include(bill => bill.Lines)
                .SingleAsync();

            billing.Database.SetCommandTimeout(TimeSpan.FromSeconds(10));

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                billing.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "billing"."bills"
                        (id, bill_number, service_account_id, account_number, customer_id, customer_name,
                         service_location_id, rate_plan_id, rate_plan_code, rate_plan_name, rate_plan_effective_from,
                         currency, unit_of_measure, period_start, period_end, cycle_code, meter_reading_id, meter_id,
                         meter_number, consumption, total_amount, amount_paid, status, created_at, status_changed_at,
                         actor_id)
                    SELECT {0}, 'BIL-999999', service_account_id, account_number, customer_id, customer_name,
                           service_location_id, rate_plan_id, rate_plan_code, rate_plan_name, rate_plan_effective_from,
                           currency, unit_of_measure, period_start, period_end, cycle_code, meter_reading_id, meter_id,
                           meter_number, consumption, total_amount, amount_paid, status, created_at, status_changed_at,
                           actor_id
                    FROM "billing"."bills" WHERE id = {1}
                    """,
                    Guid.CreateVersion7(),
                    duplicate.Id));

            // 23505 — unique_violation. The database, not the code, is what guarantees it.
            Assert.Equal("23505", exception.SqlState);
            Assert.Equal(account, first.ServiceAccountId);
        }
    }

    [Fact]
    public async Task Issuing_a_bill_writes_the_bill_its_audit_entry_and_its_outbox_row_in_one_transaction()
    {
        // Invariants 1 and 2 across two schemas: the status change is in billing, the audit entry
        // and the outbox row are in platform, and all three commit together or not at all.
        var (_, meter) = await AServedPremiseAsync(
            "Taimanao Residence",
            "44 Sinapalo Road",
            "SEN-2300103",
            installationReading: 500.000m);

        await ARecordedReadingAsync(meter, 1_100.000m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput(Cycle, Seed: 4471));
        }

        Guid billId;

        await using (var scope = fixture.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IBillService>().RunAsync(new RunBillingInput(Cycle));

            billId = Assert.Single(run.Bills).Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            var issued = await scope.ServiceProvider.GetRequiredService<IBillService>()
                .IssueAsync(billId, new IssueBillInput());

            Assert.Equal(BillStatus.Issued, issued.Status);
            Assert.NotNull(issued.DueDate);
        }

        await using (var scope = fixture.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var platform = scope.ServiceProvider.GetRequiredService<Platform.Data.PlatformDbContext>();

            Assert.Equal(BillStatus.Issued, (await billing.Bills.AsNoTracking().SingleAsync(bill => bill.Id == billId)).Status);

            Assert.True(await platform.AuditEntries.AnyAsync(audit =>
                audit.Action == Platform.Audit.AuditActions.BillIssued && audit.EntityId == billId.ToString()));
        }
    }

    [Fact]
    public async Task Adjusting_a_bill_stages_BillAdjusted_in_the_outbox_inside_the_same_transaction()
    {
        // WP-2.4's cross-module effect, which is what CONVENTIONS.md asks an integration test for.
        // Invariants 1, 2 and 5 across two schemas at once: the adjustment entry and the bill's
        // running total are in billing, the audit entry and the outbox row are in platform, and all
        // four commit together or not at all.
        //
        // Asserted on the tracked row inside the transaction rather than by counting committed rows
        // afterwards, because the delivery service is running: a row that has already been swept off
        // to the broker is a row a later count would miss, and the fact under test is that the
        // publish went into the database at all rather than onto a bus.
        var (_, meter) = await AServedPremiseAsync(
            "Ada Residence",
            "6 Tatachog Road",
            "SEN-2300105",
            installationReading: 2_000.000m);

        await ARecordedReadingAsync(meter, 2_600.000m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput(Cycle, Seed: 4471));
        }

        Guid billId;
        decimal printed;

        await using (var scope = fixture.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IBillService>().RunAsync(new RunBillingInput(Cycle));

            billId = Assert.Single(run.Bills).Id;
            printed = run.Bills[0].TotalAmount;
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBillService>().IssueAsync(billId, new IssueBillInput());
        }

        await using (var scope = fixture.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<Platform.Data.IUnitOfWork>();
            var platform = scope.ServiceProvider.GetRequiredService<Platform.Data.PlatformDbContext>();
            var bills = scope.ServiceProvider.GetRequiredService<IBillService>();

            // The register opens a unit of work of its own; nested, the outer transaction IS the
            // transaction, so what the service stages is visible here before anything commits.
            await unitOfWork.ExecuteAsync(async token =>
            {
                await bills.AdjustAsync(
                    billId,
                    new AdjustBillInput(BillAdjustmentKind.Credit, Credit, "Estimated read corrected after the customer disputed it."),
                    token);

                var staged = Assert.Single(platform.ChangeTracker.Entries<OutboxMessage>());

                Assert.Contains(nameof(BillAdjusted), staged.Entity.MessageType, StringComparison.Ordinal);

                // The audit entry is in this transaction too — invariant 1 for a sensitive action.
                Assert.Contains(
                    platform.ChangeTracker.Entries<Platform.Audit.AuditEntry>(),
                    audit => audit.Entity.Action == Platform.Audit.AuditActions.BillAdjusted);
            });
        }

        await using (var scope = fixture.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var platform = scope.ServiceProvider.GetRequiredService<Platform.Data.PlatformDbContext>();

            var stored = await billing.Bills
                .AsNoTracking()
                .Include(bill => bill.Adjustments)
                .SingleAsync(bill => bill.Id == billId);

            // numeric(18,2) round-trips the correction exactly, and the document still says what it
            // said: the printed total is untouched and what is owed has moved.
            Assert.Equal(printed, stored.TotalAmount);
            Assert.Equal(-Credit, stored.AdjustmentTotal);
            Assert.Equal(printed - Credit, stored.AmountDue);
            Assert.Equal(-Credit, Assert.Single(stored.Adjustments).Amount);

            Assert.True(await platform.AuditEntries.AnyAsync(audit =>
                audit.Action == Platform.Audit.AuditActions.BillAdjusted && audit.EntityId == billId.ToString()));
        }
    }

    [Fact]
    public async Task A_bill_cannot_carry_two_adjustments_in_one_position()
    {
        // ux_bill_adjustments_sequence, as a database fact. The order corrections were applied in is
        // what makes amount_due_after readable down the page; two rows claiming position 1 would
        // leave which figure came first decided by the query plan.
        var (_, meter) = await AServedPremiseAsync(
            "Aldan Residence",
            "21 Chalan Pale Arnold",
            "SEN-2300106",
            installationReading: 900.000m);

        await ARecordedReadingAsync(meter, 1_400.000m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput(Cycle, Seed: 4471));
        }

        Guid billId;

        await using (var scope = fixture.CreateScope())
        {
            var bills = scope.ServiceProvider.GetRequiredService<IBillService>();

            billId = Assert.Single((await bills.RunAsync(new RunBillingInput(Cycle))).Bills).Id;

            await bills.IssueAsync(billId, new IssueBillInput());
            await bills.AdjustAsync(billId, new AdjustBillInput(BillAdjustmentKind.Credit, Credit, "Estimated read corrected."));
        }

        await using (var scope = fixture.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

            // Inserted straight past the aggregate, exactly as a second concurrent correction would.
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                billing.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "billing"."bill_adjustments"
                        (id, bill_id, sequence, kind, amount, amount_due_after, reason, actor_id, recorded_at)
                    VALUES ({0}, {1}, 1, 'Charge', 10.00, 0.00, 'Raced the first one.', 'system', now())
                    """,
                    Guid.CreateVersion7(),
                    billId));

            // 23505 — unique_violation. The database, not the code, is what guarantees it.
            Assert.Equal("23505", exception.SqlState);
        }
    }

    [Fact]
    public async Task One_customers_bill_window_comes_back_with_its_corrections()
    {
        // WP-2.10's 360° page asks Billing for one customer's last few bills WITH the corrections on
        // them — a filtered, ordered, limited list with an Include of an ordered collection beside
        // it. The fast tier runs that shape against SQLite; only Npgsql can say the SQL it becomes
        // still returns one row per bill, with its entries, in the order the sequence puts them.
        var (_, meter) = await AServedPremiseAsync(
            "Manglona Residence",
            "9 Chalan Kanoa Lane",
            "SEN-2300107",
            installationReading: 1_100.000m);

        await ARecordedReadingAsync(meter, 1_650.000m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput(Cycle, Seed: 4471));
        }

        Guid customerId;

        await using (var scope = fixture.CreateScope())
        {
            var bills = scope.ServiceProvider.GetRequiredService<IBillService>();
            var raised = Assert.Single((await bills.RunAsync(new RunBillingInput(Cycle))).Bills);

            customerId = raised.CustomerId;

            await bills.IssueAsync(raised.Id, new IssueBillInput());
            await bills.AdjustAsync(
                raised.Id,
                new AdjustBillInput(BillAdjustmentKind.Credit, Credit, "Estimated read corrected."));
        }

        await using (var scope = fixture.CreateScope())
        {
            var bills = scope.ServiceProvider.GetRequiredService<IBillService>();

            var plain = await bills.ListAsync(new BillQuery(CustomerId: customerId));
            var withEntries = await bills.ListAsync(new BillQuery(CustomerId: customerId, IncludeAdjustments: true));

            // Off by default, so no other caller's page grew a second collection it will not render.
            Assert.Empty(Assert.Single(plain).Adjustments);
            Assert.Equal(-Credit, Assert.Single(plain).AdjustmentTotal);

            var listed = Assert.Single(withEntries);
            var entry = Assert.Single(listed.Adjustments);

            Assert.Equal(1, entry.Sequence);
            Assert.Equal(-Credit, entry.Amount);
            Assert.Equal("Estimated read corrected.", entry.Reason);

            // The lines stay off the row whichever way it was asked for — they are the collection
            // the objection was always about.
            Assert.Empty(listed.Lines);
        }
    }

    [Fact]
    public async Task A_bill_is_priced_on_the_tariff_version_in_force_for_its_own_period()
    {
        // Effective dating against the rows the migration actually seeded, rather than the ones the
        // fast tier builds in memory.
        await using var scope = fixture.CreateScope();

        var tariffs = scope.ServiceProvider.GetRequiredService<IRatePlanService>();

        var before = await tariffs.InForceAsync(
            DefaultRatePlans.ResidentialStandard,
            DefaultRatePlans.ResidentialRevisionFrom.AddDays(-1));

        var after = await tariffs.InForceAsync(
            DefaultRatePlans.ResidentialStandard,
            DefaultRatePlans.ResidentialRevisionFrom);

        Assert.NotEqual(before.Id, after.Id);
        Assert.Equal(12.50m, before.MonthlyServiceCharge);
        Assert.Equal(13.75m, after.MonthlyServiceCharge);
    }

    [Fact]
    public async Task A_second_tariff_assignment_for_one_account_is_refused_by_Postgres()
    {
        // ux_account_rate_plans_account. One tariff per account, as a database fact: two would be
        // two bills for one period, and which the customer owes decided by the query plan.
        var (account, _) = await AServedPremiseAsync(
            "Manglona Farm",
            "17 As Matuis Way",
            "SEN-2300104",
            installationReading: 0m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IRatePlanService>()
                .AssignAsync(account, DefaultRatePlans.CommercialStandard);
        }

        await using (var scope = fixture.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                billing.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "billing"."account_rate_plans"
                        (id, service_account_id, rate_plan_code, assigned_at, actor_id)
                    VALUES ({0}, {1}, 'RES-STD', now(), 'system')
                    """,
                    Guid.CreateVersion7(),
                    account));

            Assert.Equal("23505", exception.SqlState);
        }
    }

    [Fact]
    public async Task The_seeded_demo_world_bills_its_own_reading_cycles()
    {
        // The whole seeding chain against real Postgres — customers, accounts, assets, stock, meters,
        // readings, bills — proving the demo world a reviewer opens actually has money in it. Every
        // figure here came out of the real rate engine and the real aggregate, so a bill that could
        // not be explained would have failed the run rather than shipped.
        await new DemoSeedRunner(
                fixture.Application.Services.GetRequiredService<IServiceScopeFactory>(),
                fixture.Application.Services.GetRequiredService<IHostEnvironment>(),
                fixture.Application.Services.GetRequiredService<TimeProvider>(),
                fixture.Application.Services.GetRequiredService<ILogger<DemoSeedRunner>>())
            .StartAsync(CancellationToken.None);

        await using var scope = fixture.CreateScope();

        var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var bills = await billing.Bills.Include(bill => bill.Lines).ToListAsync();

        Assert.NotEmpty(bills);

        // Every one adds up to its own printed lines, after a round trip through numeric(18,2).
        Assert.All(bills, bill => Assert.Equal(bill.TotalAmount, Money.Total(bill.Lines.Select(line => line.Amount))));

        // Both sides of the tariff revision are represented, which is the point of publishing two
        // versions of the residential plan.
        var versions = bills.Select(bill => bill.RatePlanEffectiveFrom).Distinct().ToList();

        Assert.Contains(DefaultRatePlans.OriginalEffectiveFrom, versions);
        Assert.Contains(DefaultRatePlans.ResidentialRevisionFrom, versions);

        // And the demo opens with work to do rather than a finished month.
        Assert.Contains(BillStatus.Draft, bills.Select(bill => bill.Status));
    }

    [Fact]
    public async Task A_tariff_cannot_be_assigned_to_an_account_that_does_not_exist()
    {
        // Failure path across the boundary, against the real Customers registry rather than a fake:
        // the answer depends on another module's schema, which no validator at this edge can see.
        await using var scope = fixture.CreateScope();

        await Assert.ThrowsAsync<ServiceAccountNotFoundException>(() =>
            scope.ServiceProvider.GetRequiredService<IRatePlanService>()
                .AssignAsync(Guid.CreateVersion7(), DefaultRatePlans.CommercialStandard));
    }
}
