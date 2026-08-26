using GridCore.Contracts.Services;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Delinquency;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Delinquency;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// WP-2.19's cross-module effect against real Postgres and a real broker: evaluating an account for
/// disconnection sets the held deposit against qualifying past-due amounts, and that movement
/// crosses the bus and becomes a balanced journal entry in Finance.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves everything that does not need infrastructure — the ageing, the four tests,
/// the offset arithmetic, the permission gate, the audit trail and the idempotency of the
/// late-charge run, all in milliseconds. What only containers can show is the claim the work package
/// actually makes: that a deposit applied <b>because the law required it</b> reaches the general
/// ledger, through the outbox, across three schemas whose migrations have been applied.
/// </para>
/// <para>
/// This is the WP's named gate-tier case: "the offset event → journal entry".
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DelinquencyTests(GateFixture fixture) : IAsyncLifetime
{
    /// <summary>How long a test will wait for the broker to carry a fact to Finance. A ceiling, never a pause.</summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_statutory_offset_crosses_the_bus_and_relieves_the_receivable()
    {
        // THE WORK PACKAGE'S GATE-TIER CASE. Nothing in this test touches Finance: Customers decides
        // the law obliges it to spend the deposit, and the ledger entry appears because Finance is
        // downstream of everyone and consumed the fact.
        var bill = await APastDueBillAsync(daysPastDue: 90);

        // Enough to leave arrears behind, so the account is still eligible afterwards and the offset
        // is the whole of what was held rather than the whole of what was owed.
        var held = decimal.Round(bill.Balance / 2, 2);

        await CollectAsync(bill.CustomerId, held);
        await ServeAsync(bill.ServiceAccountId, DunningNoticeType.Disconnection, daysAgo: 20);

        var evaluation = await EvaluateAsync(bill.ServiceAccountId);

        Assert.Equal(held, evaluation.OffsetAmount);
        Assert.True(evaluation.Eligibility.IsOffsetApplied);

        var eventId = await AwaitPostedEventIdAsync(FinancePostings.DepositAppliedSource, bill.BillNumber);
        var entry = await EntryForAsync(eventId);

        Assert.NotNull(entry);
        Assert.Equal(held, entry.Lines.Single(line => line.Account.Code == FinanceAccounts.CustomerDeposits).Debit);
        Assert.Equal(held, entry.Lines.Single(line => line.Account.Code == FinanceAccounts.AccountsReceivable).Credit);

        // Balanced, which invariant 3 demands of every posting and this one gets for free by being an
        // ordinary deposit application.
        Assert.Equal(entry.TotalDebits, entry.TotalCredits);

        // No cash line: the money entered the utility when the deposit was taken, and this is a
        // transfer between two things the utility already held.
        Assert.DoesNotContain(entry.Lines, line => line.Account.Code == FinanceAccounts.Cash);

        await using var scope = fixture.CreateScope();

        // And the ledger row says why it happened, which is the point of naming the statute in the
        // reason rather than in somebody's memory.
        var moved = await scope.ServiceProvider.GetRequiredService<CustomersDbContext>()
            .DepositEntries.AsNoTracking()
            .SingleAsync(row => row.CustomerId == bill.CustomerId && row.Kind == DepositEntryKind.Applied);

        Assert.Contains(StatutoryBasis.PublicLaw1617, moved.Reason!, StringComparison.Ordinal);
        Assert.Equal(bill.Id, moved.BillId);
    }

    [Fact]
    public async Task A_deposit_that_clears_the_arrears_leaves_the_account_ineligible()
    {
        // The case the statute exists for, end to end across two modules: the customer keeps their
        // supply, and the reason is their own money.
        var bill = await APastDueBillAsync(daysPastDue: 90);

        await CollectAsync(bill.CustomerId, bill.Balance);
        await ServeAsync(bill.ServiceAccountId, DunningNoticeType.Disconnection, daysAgo: 20);

        var evaluation = await EvaluateAsync(bill.ServiceAccountId);

        Assert.Equal(bill.Balance, evaluation.OffsetAmount);
        Assert.Equal(0m, evaluation.Eligibility.ArrearsAfterOffset);
        Assert.True(evaluation.Eligibility.DepositClearsArrears);
        Assert.False(evaluation.Eligibility.IsEligible);

        // Billing's own consumer reduces the bill, under a different consumer name from Finance's —
        // which is exactly what the two distinct names exist for.
        await AwaitBillSettlementAsync(bill.Id, bill.Balance);
    }

    [Fact]
    public async Task The_late_charge_run_charges_a_past_due_bill_once_and_the_fee_reaches_the_register()
    {
        // The other half of the package against real Postgres: the idempotency is a unique index, and
        // an index that only exists in the fast tier's SQLite is an index nobody has proved.
        var bill = await APastDueBillAsync(daysPastDue: 40);

        var first = await RunLateChargesAsync();
        var second = await RunLateChargesAsync();

        Assert.Contains(first.Assessed, assessment => assessment.BillId == bill.Id);
        Assert.DoesNotContain(second.Assessed, assessment => assessment.BillId == bill.Id);

        await using var scope = fixture.CreateScope();

        var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var assessment = await billing.LateChargeAssessments.AsNoTracking().SingleAsync(row => row.BillId == bill.Id);
        var charge = await billing.AccountCharges.AsNoTracking().SingleAsync(row => row.Id == assessment.AccountChargeId);

        Assert.Equal(FeeCode.LateCharge, charge.Code);
        Assert.Equal(FeeBasis.Rate, charge.Basis);
        Assert.Equal(FeeSchedules.LateChargeMonthlyRate, charge.Rate);

        // The 1% is on the past-due BALANCE, and `numeric(18,4)` round-trips the rate exactly — a
        // float column would hand back 0.009999999 and every late charge would be a cent out.
        Assert.Equal(bill.Balance, charge.BasisAmount);
        Assert.Equal(decimal.Round(bill.Balance * FeeSchedules.LateChargeMonthlyRate, 2, MidpointRounding.AwayFromZero), charge.Amount);
    }

    /// <summary>
    /// An issued bill on an energised account, due <paramref name="daysPastDue"/> days ago — the
    /// state a delinquency picture is read from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Assembled through four modules' own services exactly as the application would, the shape
    /// <see cref="DepositLedgerTests"/> and <see cref="PaymentRegistryTests"/> already use. The one
    /// difference is the issue: the due date is stated rather than defaulted, because a bill on the
    /// ordinary twenty-one-day term is not late and nothing in this file has anything to say about it.
    /// </para>
    /// <para>
    /// <b>It is then corrected upwards, and that is not cosmetic.</b> The reading cycle is a
    /// simulator, so what the rate engine prints is whatever it generated — a handful of dollars,
    /// which is below the published disconnection threshold and would make every test here fail for a
    /// reason that has nothing to do with delinquency. A WP-2.4 charge adjustment is the register's
    /// own way of saying a bill is owed more, and it exercises the balance-versus-total distinction
    /// this package turns on into the bargain.
    /// </para>
    /// </remarks>
    /// <param name="daysPastDue">How long ago the bill fell due.</param>
    /// <param name="atLeast">The balance to bring it up to, so it clears the published threshold.</param>
    private async Task<Bill> APastDueBillAsync(int daysPastDue, decimal atLeast = 200.00m)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var cycle = $"DLQ-{tag}";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Guid premise;
        Guid customer;
        Bill issued;

        await using (var scope = fixture.CreateScope())
        {
            customer = (await scope.ServiceProvider.GetRequiredService<ICustomerService>()
                .RegisterAsync(new RegisterCustomerInput($"Delinquent customer {tag}", CustomerClass.Residential, "Ana Reyes")))
                .Id;

            premise = (await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
                .RegisterAsync(new ServiceLocationInput(
                    Address.Create($"{tag} As Nieves Road", "Songsong", "Rota", "MP", postalCode: "96951"),
                    "Meter on the north wall",
                    IsActive: true,
                    null)))
                .Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
            var account = await accounts.OpenAsync(new OpenServiceAccountInput(customer, premise, ServiceType.Electricity, "Requested at the counter"));

            await accounts.StartServiceAsync(account.Id, "Connected.");
        }

        await using (var scope = fixture.CreateScope())
        {
            var meters = scope.ServiceProvider.GetRequiredService<IMeterService>();
            var meter = await meters.RegisterAsync(new RegisterMeterInput($"SN-{tag}", MeterType.SinglePhase, Manufacturer: "Sensus"));

            await meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise, 1_000.000m));

            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RecordAsync(meter.Meter.Id, new RecordReadingInput(1_600.000m, Note: "Read off the card"));
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput(cycle, Seed: 4471));
        }

        await using (var scope = fixture.CreateScope())
        {
            var bills = scope.ServiceProvider.GetRequiredService<IBillService>();
            var draft = Assert.Single((await bills.RunAsync(new RunBillingInput(cycle))).Bills);

            issued = await bills.IssueAsync(
                draft.Id,
                new IssueBillInput(today.AddDays(-daysPastDue - 21), today.AddDays(-daysPastDue)));
        }

        if (issued.Balance >= atLeast)
        {
            return issued;
        }

        await using (var scope = fixture.CreateScope())
        {
            return await scope.ServiceProvider.GetRequiredService<IBillService>().AdjustAsync(
                issued.Id,
                new AdjustBillInput(
                    BillAdjustmentKind.Charge,
                    atLeast - issued.Balance,
                    "Corrected upwards: the read the cycle billed was too low."));
        }
    }

    private async Task CollectAsync(Guid customerId, decimal amount)
    {
        await using var scope = fixture.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ICustomerDepositService>()
            .CollectAsync(customerId, new CollectDepositInput(amount, Reason: "Taken at the counter."));
    }

    private async Task ServeAsync(Guid serviceAccountId, DunningNoticeType noticeType, int daysAgo)
    {
        await using var scope = fixture.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IDelinquencyService>()
            .ServeAsync(
                serviceAccountId,
                new ServeNoticeInput(noticeType, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-daysAgo)));
    }

    private async Task<DisconnectionEvaluation> EvaluateAsync(Guid serviceAccountId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IDelinquencyService>()
            .EvaluateAsync(serviceAccountId, new EvaluateDisconnectionInput());
    }

    private async Task<LateChargeRunResult> RunLateChargesAsync()
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ILateChargeService>()
            .RunAsync(new LateChargeRunInput());
    }

    /// <summary>Waits for Billing's own consumer to reduce what the bill is owed.</summary>
    private async Task AwaitBillSettlementAsync(Guid billId, decimal expected)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = fixture.CreateScope();

            var bill = await scope.ServiceProvider.GetRequiredService<BillingDbContext>()
                .Bills.AsNoTracking().FirstOrDefaultAsync(row => row.Id == billId);

            if (bill is not null && bill.AmountPaid == expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"Billing did not apply the deposit to bill {billId} within {DeliveryTimeout}.");
    }

    private async Task<JournalEntry?> EntryForAsync(Guid eventId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<FinanceDbContext>()
            .JournalEntries
            .AsNoTracking()
            .Include(entry => entry.Lines)
            .ThenInclude(line => line.Account)
            .FirstOrDefaultAsync(entry => entry.EventId == eventId);
    }

    /// <summary>
    /// Waits for Finance's seam to fire for <paramref name="source"/> with <paramref name="reference"/>,
    /// and hands back the posting's event id. The shape <see cref="DepositLedgerTests"/> documents.
    /// </summary>
    private async Task<Guid> AwaitPostedEventIdAsync(string source, string reference)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var posting = fixture.Postings.Postings
                .FirstOrDefault(candidate => candidate.Source == source && candidate.Reference == reference);

            if (posting is not null)
            {
                var deadlineForEntry = DateTimeOffset.UtcNow + DeliveryTimeout;

                while (DateTimeOffset.UtcNow < deadlineForEntry)
                {
                    if (await EntryForAsync(posting.EventId) is not null)
                    {
                        return posting.EventId;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }

                Assert.Fail($"Finance did not post event {posting.EventId} within {DeliveryTimeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"Finance did not post a {source} entry for {reference} within {DeliveryTimeout}.");

        throw new InvalidOperationException("Unreachable.");
    }
}
