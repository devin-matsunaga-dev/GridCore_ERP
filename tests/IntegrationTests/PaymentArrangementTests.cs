using GridCore.Contracts.Services;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Arrangements;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Delinquency;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Payments.Features.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// WP-2.20's cross-module effect against real Postgres and a real broker: a payment taken through
/// Payments crosses the bus and settles an arrangement's instalment in Customers.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves everything that does not need infrastructure — the schedule arithmetic, the
/// state machine, the allocation, the two ceilings, the approval gate, the permission gate and the
/// audit trail, all in milliseconds. What only containers can show is the claim the work package
/// actually makes: that an instalment is settled by a <b>real payment through WP-2.5</b> rather than
/// by a figure a rep types into an arrangements screen — which means the fact has to leave Payments,
/// go through the outbox, and be claimed by a consumer this module registered.
/// </para>
/// <para>
/// It also pins the half of the design that has no fast-tier equivalent: <b>Billing and Customers
/// claim the same event under different consumer names</b>, so the bill's balance falls AND the
/// instalment settles. A shared name would have whichever handled it first silently suppress the
/// other, and only a real broker can show that they do not.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PaymentArrangementTests(GateFixture fixture) : IAsyncLifetime
{
    /// <summary>How long a test will wait for the broker to carry a fact to Customers. A ceiling, never a pause.</summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_real_payment_crosses_the_bus_and_settles_the_earliest_instalment()
    {
        // THE WORK PACKAGE'S GATE-TIER CASE. Nothing in this test touches an arrangement after it is
        // brought into force: the instalment settles because Payments stated that money arrived and
        // Customers consumed the fact.
        var bill = await APastDueBillAsync(daysPastDue: 40);

        var arrangement = await ArrangeAsync(bill.ServiceAccountId, bill.Balance, instalmentCount: 3);

        Assert.Equal(PaymentArrangementStatus.Active, arrangement.Status);
        Assert.Equal(bill.Balance, arrangement.ScheduledAmount);

        var first = arrangement.Instalments.OrderBy(instalment => instalment.Sequence).First();

        var payment = await TakeAsync(bill.Id, first.Amount);

        Assert.Equal(PaymentStatus.Approved, payment.Payment.Status);

        var settled = await AwaitInstalmentSettlementAsync(arrangement.Id, first.Sequence);

        // The earliest unpaid instalment, and only that one: a payment for one month's figure does
        // not quietly credit the next.
        Assert.Equal(first.Amount, settled.PaidAmount);
        Assert.Equal(bill.Balance - first.Amount, settled.Arrangement.OutstandingAmount);
        Assert.Equal(PaymentArrangementStatus.Active, settled.Arrangement.Status);

        // AND THE BILL FELL TOO, under Billing's own consumer name. Two modules claim one event and
        // neither suppresses the other — the arrangement records how the debt will be paid, and the
        // bill records that some of it has been.
        await AwaitBillPaymentAsync(bill.Id, first.Amount);
    }

    [Fact]
    public async Task The_payment_that_finishes_the_schedule_records_the_arrangement_as_kept()
    {
        var bill = await APastDueBillAsync(daysPastDue: 40);

        var arrangement = await ArrangeAsync(bill.ServiceAccountId, bill.Balance, instalmentCount: 2);

        await TakeAsync(bill.Id, bill.Balance);

        var settled = await AwaitInstalmentSettlementAsync(arrangement.Id, sequence: 2);

        // A payment larger than one instalment cascades down the schedule: a customer who pays two
        // months at once has paid two months.
        Assert.Equal(0m, settled.Arrangement.OutstandingAmount);
        Assert.Equal(PaymentArrangementStatus.Kept, settled.Arrangement.Status);
        Assert.NotNull(settled.Arrangement.ClosedOn);
    }

    [Fact]
    public async Task An_arrangement_in_force_stops_the_account_being_eligible_for_disconnection()
    {
        // THE SEAM WP-2.19 LEFT, END TO END. That package wrote the fourth disconnection test around
        // a stub answering "there are none"; this is the first time it is answered by a real register
        // over real Postgres, and the account it protects is one that passes all three other tests.
        var bill = await APastDueBillAsync(daysPastDue: 90);

        await ServeAsync(bill.ServiceAccountId, DunningNoticeType.Disconnection, daysAgo: 20);

        var before = await EvaluateAsync(bill.ServiceAccountId);

        Assert.True(before.Eligibility.IsEligible);

        await ArrangeAsync(bill.ServiceAccountId, bill.Balance, instalmentCount: 3);

        var after = await EvaluateAsync(bill.ServiceAccountId);

        Assert.False(after.Eligibility.IsEligible);
        Assert.Contains(DisconnectionRules.ArrangementTest, after.Eligibility.Blockers);
        Assert.Equal(nameof(PaymentArrangementStatus.Active), after.Eligibility.Arrangement!.Status);
    }

    /// <summary>Makes an arrangement over the account's arrears and brings it into force.</summary>
    private async Task<PaymentArrangement> ArrangeAsync(Guid serviceAccountId, decimal balance, int instalmentCount)
    {
        Guid id;

        await using (var scope = fixture.CreateScope())
        {
            id = (await scope.ServiceProvider.GetRequiredService<IPaymentArrangementService>()
                .ProposeAsync(
                    serviceAccountId,
                    new ProposeArrangementInput(balance, InstalmentCount: instalmentCount)))
                .Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            return await scope.ServiceProvider.GetRequiredService<IPaymentArrangementService>().ActivateAsync(id);
        }
    }

    private async Task<PaymentResult> TakeAsync(Guid billId, decimal amount)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPaymentService>()
            .TakeAsync(new TakePaymentInput(billId, amount, PaymentMethods.Cash, null));
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

    /// <summary>Waits for Customers' consumer to settle the instalment at <paramref name="sequence"/>.</summary>
    private async Task<(PaymentArrangement Arrangement, decimal PaidAmount)> AwaitInstalmentSettlementAsync(
        Guid arrangementId,
        int sequence)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = fixture.CreateScope();

            var arrangement = await scope.ServiceProvider.GetRequiredService<CustomersDbContext>()
                .PaymentArrangements
                .AsNoTracking()
                .Include(row => row.Instalments)
                .FirstOrDefaultAsync(row => row.Id == arrangementId);

            var instalment = arrangement?.Instalments.FirstOrDefault(row => row.Sequence == sequence);

            if (instalment is { IsSettled: true })
            {
                return (arrangement!, instalment.PaidAmount);
            }

            // No Thread.Sleep and no fixed pause: a real signal, polled to a ceiling.
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"Customers did not settle instalment {sequence} of arrangement {arrangementId} within {DeliveryTimeout}.");

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Waits for Billing's own consumer to reduce what the bill is owed.</summary>
    private async Task AwaitBillPaymentAsync(Guid billId, decimal expected)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = fixture.CreateScope();

            var bill = await scope.ServiceProvider.GetRequiredService<BillingDbContext>()
                .Bills.AsNoTracking().FirstOrDefaultAsync(row => row.Id == billId);

            if (bill is not null && bill.AmountPaid >= expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"Billing did not record the payment against bill {billId} within {DeliveryTimeout}.");
    }

    /// <summary>
    /// An issued bill on an energised account, due <paramref name="daysPastDue"/> days ago — the
    /// arrears an arrangement is made against.
    /// </summary>
    /// <remarks>
    /// The shape <see cref="DelinquencyTests.APastDueBillAsync"/> documents, and corrected upwards
    /// for the same reason: the reading cycle is a simulator, so what the rate engine prints is a
    /// handful of dollars, and an arrangement over $4.17 in three instalments would fail on the
    /// schedule's own "worth collecting" rule rather than on anything this file is about.
    /// </remarks>
    private async Task<Bill> APastDueBillAsync(int daysPastDue, decimal atLeast = 300.00m)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var cycle = $"ARR-{tag}";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Guid premise;
        Guid customer;
        Bill issued;

        await using (var scope = fixture.CreateScope())
        {
            customer = (await scope.ServiceProvider.GetRequiredService<ICustomerService>()
                .RegisterAsync(new RegisterCustomerInput($"Arranging customer {tag}", CustomerClass.Residential, "Ana Reyes")))
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
}
