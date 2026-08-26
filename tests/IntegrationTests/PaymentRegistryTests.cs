using GridCore.Contracts.Events;
using GridCore.Contracts.Providers;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Payments.Data;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Modules.Payments.Features.Shared;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// The revenue cycle's second half against real Postgres and a real broker: a bill is paid, the
/// approval crosses the bus, and the balance moves in another module's schema.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves every outcome, every guard and the idempotency of the consumer with no
/// infrastructure at all — that is where nearly all of WP-2.5's cases live. What only containers
/// can show is what the <b>seams</b> do: that Payments really can check a balance it reaches
/// through a <c>Contracts</c> interface implemented by another module over another schema, that
/// <c>PaymentApproved</c> goes into the outbox in the same transaction as the payment row, and that
/// the broker then carries it to two independent consumers — Billing's, which reduces the balance,
/// and Finance's, which posts the cash receipt.
/// </para>
/// <para>
/// This is the cross-module effect WP-2.5 adds, so CONVENTIONS.md's "new cross-module effect → one
/// integration test" is what this file is.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PaymentRegistryTests(GateFixture fixture) : IAsyncLifetime
{
    private const string Cycle = "2026-08";

    /// <summary>
    /// How long a test will wait for the broker to carry an approval to its consumers. Generous
    /// because a container's first delivery pays for the queue being declared. A ceiling, never a
    /// pause: every wait here returns the moment the thing it is watching for happens, so a fast
    /// delivery costs nothing (CONVENTIONS.md rule G).
    /// </summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// An issued bill on an energised account, assembled through four modules' own services exactly
    /// as the application would — the state a payment arrives at.
    /// </summary>
    private async Task<Bill> AnIssuedBillAsync(string customerName, string line1, string serialNumber)
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

        await using (var scope = fixture.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
            var account = await accounts.OpenAsync(new OpenServiceAccountInput(customer, premise, "Requested at the counter"));

            await accounts.StartServiceAsync(account.Id, "Connected.");
        }

        await using (var scope = fixture.CreateScope())
        {
            var meters = scope.ServiceProvider.GetRequiredService<IMeterService>();
            var meter = await meters.RegisterAsync(new RegisterMeterInput(serialNumber, MeterType.SinglePhase, Manufacturer: "Sensus"));

            await meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise, 1_000.000m));

            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RecordAsync(meter.Meter.Id, new RecordReadingInput(1_600.000m, Note: "Read off the card"));
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput(Cycle, Seed: 4471));
        }

        await using (var scope = fixture.CreateScope())
        {
            var bills = scope.ServiceProvider.GetRequiredService<IBillService>();
            var draft = Assert.Single((await bills.RunAsync(new RunBillingInput(Cycle))).Bills);

            return await bills.IssueAsync(draft.Id, new IssueBillInput());
        }
    }

    private Task<PaymentResult> TakeAsync(Guid billId, decimal amount, string method = PaymentMethods.Card, string? instrument = "•••• 4242")
    {
        return Take();

        async Task<PaymentResult> Take()
        {
            await using var scope = fixture.CreateScope();

            return await scope.ServiceProvider.GetRequiredService<IPaymentService>()
                .TakeAsync(new TakePaymentInput(billId, amount, method, instrument));
        }
    }

    private async Task<Bill> BillAsync(Guid billId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<BillingDbContext>()
            .Bills
            .AsNoTracking()
            .Include(bill => bill.Adjustments)
            .SingleAsync(bill => bill.Id == billId);
    }

    /// <summary>
    /// Waits for a row to reach <paramref name="condition"/>, re-reading until it does or the
    /// deadline passes.
    /// </summary>
    /// <remarks>
    /// Polling a table, not sleeping a fixed span: the assertion is on the real change and a fast
    /// delivery returns immediately. Billing's consumer writes to the database and offers no
    /// in-process signal to await, unlike Finance's seam, which the recorder exposes one for.
    /// </remarks>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;
        var latest = await read();

        while (!condition(latest) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            latest = await read();
        }

        return latest;
    }

    [Fact]
    public async Task An_approved_payment_crosses_the_bus_and_settles_the_bill_in_another_schema()
    {
        // The headline of the work package, end to end across five schemas and a broker. Cash, so
        // the sandbox cannot decline it and the test is about delivery rather than about luck.
        var bill = await AnIssuedBillAsync("Sablan Family Residence", "128 As Nieves Road", "SEN-2500101");

        var result = await TakeAsync(bill.Id, bill.Balance, PaymentMethods.Cash, instrument: null);

        Assert.Equal(PaymentStatus.Approved, result.Payment.Status);
        Assert.Equal(PaymentOutcome.Approved, result.Payment.Outcome);

        // The payment row survived numeric(18,2) exactly.
        await using (var scope = fixture.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>()
                .Payments
                .AsNoTracking()
                .SingleAsync(payment => payment.Id == result.Payment.Id);

            Assert.Equal(bill.Balance, stored.Amount);
            Assert.Equal(bill.Balance, stored.BalanceBefore);
            Assert.Equal(bill.Currency, stored.Currency);
        }

        // And the broker carried the approval to Billing's consumer, which reduced the balance in a
        // schema the Payments module has never heard of.
        var settled = await EventuallyAsync(() => BillAsync(bill.Id), candidate => candidate.Status is BillStatus.Paid);

        Assert.Equal(BillStatus.Paid, settled.Status);
        Assert.Equal(bill.Balance, settled.AmountPaid);
        Assert.Equal(0m, settled.Balance);

        // The printed total never moved. It is what the customer holds a copy of.
        Assert.Equal(bill.TotalAmount, settled.TotalAmount);

        // Finance's consumer took the same event independently and posted the cash receipt. Two
        // modules, one fact, neither aware of the other — and the posting balances, which is
        // invariant 3 arriving at the far end of this work package.
        //
        // Watched for by SOURCE rather than by taking the recorder's "next": issuing the bill above
        // publishes BillIssued, and its posting is still in flight when the payment is taken. The
        // next posting to arrive is not necessarily this one.
        var receipt = await EventuallyAsync(
            () => Task.FromResult(fixture.Postings.Postings
                .FirstOrDefault(posting => posting.Source == FinancePostings.PaymentApprovedSource)),
            posting => posting is not null);

        Assert.NotNull(receipt);
        Assert.Equal(bill.Balance, receipt.TotalDebits);
        Assert.Equal(receipt.TotalDebits, receipt.TotalCredits);
    }

    [Fact]
    public async Task Taking_a_payment_stages_PaymentApproved_in_the_outbox_inside_the_same_transaction()
    {
        // Invariants 1 and 2 across two schemas: the payment row is in payments, the audit entry and
        // the outbox row are in platform, and all three commit together or not at all.
        //
        // Asserted on the tracked row inside the transaction rather than by counting committed rows
        // afterwards, because the delivery service is running: a row already swept off to the broker
        // is a row a later count would miss, and the fact under test is that the publish went into
        // the database at all rather than onto a bus. WP-2.4's shape.
        var bill = await AnIssuedBillAsync("Camacho Store", "9 Chalan Kanoa Street", "SEN-2500102");

        await using var scope = fixture.CreateScope();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<Platform.Data.IUnitOfWork>();
        var platform = scope.ServiceProvider.GetRequiredService<Platform.Data.PlatformDbContext>();
        var payments = scope.ServiceProvider.GetRequiredService<IPaymentService>();

        // The register opens a unit of work of its own; nested, the outer transaction IS the
        // transaction, so what the service stages is visible here before anything commits.
        await unitOfWork.ExecuteAsync(async token =>
        {
            await payments.TakeAsync(
                new TakePaymentInput(bill.Id, 10.00m, PaymentMethods.Cash, null),
                token);

            var staged = Assert.Single(platform.ChangeTracker.Entries<OutboxMessage>());

            Assert.Contains(nameof(PaymentApproved), staged.Entity.MessageType, StringComparison.Ordinal);

            Assert.Contains(
                platform.ChangeTracker.Entries<Platform.Audit.AuditEntry>(),
                audit => audit.Entity.Action == Platform.Audit.AuditActions.PaymentTaken);
        });
    }

    [Fact]
    public async Task A_refused_payment_is_recorded_and_leaves_the_bill_alone()
    {
        // The failure path across the seam. A pinned instrument makes the decline deliberate rather
        // than a matter of which payment number came up.
        var bill = await AnIssuedBillAsync("Taimanao Residence", "44 Sinapalo Road", "SEN-2500103");

        var result = await TakeAsync(
            bill.Id,
            10.00m,
            instrument: $"•••• {Modules.Payments.Simulation.SimulatedPaymentProvider.DeclinedInstrumentSuffix}");

        Assert.Equal(PaymentStatus.Declined, result.Payment.Status);
        Assert.Equal(PaymentOutcome.Declined, result.Payment.Outcome);

        // Recorded — a refusal is an answer, and the register has to be able to show it.
        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>()
                .Payments
                .AsNoTracking()
                .AnyAsync(payment => payment.Id == result.Payment.Id && payment.Status == PaymentStatus.Declined));

            // And audited, refusal or not (invariant 1).
            Assert.True(await scope.ServiceProvider.GetRequiredService<Platform.Data.PlatformDbContext>()
                .AuditEntries
                .AnyAsync(audit =>
                    audit.Action == Platform.Audit.AuditActions.PaymentTaken
                    && audit.EntityId == result.Payment.Id.ToString()));
        }

        // Nothing was published, so nothing can have moved the bill.
        Assert.Equal(0, await fixture.CountOutboxMessagesForAsync(result.Payment.Id));

        var untouched = await BillAsync(bill.Id);

        Assert.Equal(BillStatus.Issued, untouched.Status);
        Assert.Equal(0m, untouched.AmountPaid);
    }

    [Fact]
    public async Task A_payment_is_checked_against_the_balance_a_credit_left_behind()
    {
        // WP-2.4 reaching WP-2.5 across the boundary. The bill is credited, so what is owed and what
        // was printed differ — and Payments, which reads the figure through IBillDirectory, must be
        // refusing against the balance rather than the printed total.
        var bill = await AnIssuedBillAsync("Ada Residence", "6 Tatachog Road", "SEN-2500104");
        var credit = decimal.Round(bill.TotalAmount / 2, 2);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBillService>().AdjustAsync(
                bill.Id,
                new AdjustBillInput(BillAdjustmentKind.Credit, credit, "Estimated read corrected after the customer disputed it."));
        }

        // Paying what the DOCUMENT says is now more than is owed, and is refused before anybody is
        // charged.
        var thrown = await Assert.ThrowsAsync<PaymentWorkflowException>(() =>
            TakeAsync(bill.Id, bill.TotalAmount, PaymentMethods.Cash, instrument: null));

        Assert.Contains("more than is owed", thrown.Message, StringComparison.Ordinal);

        // Nothing was recorded: the guard runs before the provider is asked.
        await using (var scope = fixture.CreateScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>()
                .Payments
                .AsNoTracking()
                .AnyAsync(payment => payment.BillId == bill.Id));
        }

        // Paying what IS owed goes through and settles it.
        var result = await TakeAsync(bill.Id, bill.TotalAmount - credit, PaymentMethods.Cash, instrument: null);

        Assert.Equal(PaymentStatus.Approved, result.Payment.Status);

        var settled = await EventuallyAsync(() => BillAsync(bill.Id), candidate => candidate.Status is BillStatus.Paid);

        Assert.Equal(BillStatus.Paid, settled.Status);
        Assert.Equal(bill.TotalAmount, settled.TotalAmount);
        Assert.Equal(bill.TotalAmount - credit, settled.AmountPaid);
    }

    [Fact]
    public async Task A_part_payment_leaves_the_bill_owed_and_a_second_one_settles_it()
    {
        // Two instalments are two facts and both reduce the balance — the flip side of the
        // consumer's idempotency, proved across a real broker rather than a fake one.
        var bill = await AnIssuedBillAsync("Aldan Residence", "21 Chalan Pale Arnold", "SEN-2500105");
        var half = decimal.Round(bill.Balance / 2, 2);

        await TakeAsync(bill.Id, half, PaymentMethods.Cash, instrument: null);

        var part = await EventuallyAsync(() => BillAsync(bill.Id), candidate => candidate.AmountPaid == half);

        Assert.Equal(BillStatus.PartiallyPaid, part.Status);
        Assert.Equal(bill.Balance - half, part.Balance);

        await TakeAsync(bill.Id, part.Balance, PaymentMethods.Cash, instrument: null);

        var settled = await EventuallyAsync(() => BillAsync(bill.Id), candidate => candidate.Status is BillStatus.Paid);

        Assert.Equal(BillStatus.Paid, settled.Status);
        Assert.Equal(bill.Balance, settled.AmountPaid);
    }

    [Fact]
    public async Task A_payment_number_cannot_be_issued_twice()
    {
        // ux_payments_number, as a database fact. The unique index is what makes the number series
        // safe without a lock — see RegistryNumberSeries.
        var bill = await AnIssuedBillAsync("Manglona Residence", "3 As Matmos Road", "SEN-2500106");

        var first = await TakeAsync(bill.Id, 10.00m, PaymentMethods.Cash, instrument: null);

        await using var scope = fixture.CreateScope();

        var payments = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        // Inserted straight past the service, exactly as a second concurrent payment would.
        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            payments.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "payments"."payments"
                    (id, payment_number, service_account_id, account_number, customer_id, customer_name,
                     bill_id, bill_number, amount, currency, method, balance_before, status,
                     requested_at, status_changed_at, actor_id)
                SELECT {0}, payment_number, service_account_id, account_number, customer_id, customer_name,
                       bill_id, bill_number, amount, currency, method, balance_before, status,
                       requested_at, status_changed_at, actor_id
                FROM "payments"."payments" WHERE id = {1}
                """,
                Guid.CreateVersion7(),
                first.Payment.Id));

        // 23505 — unique_violation. The database, not the code, is what guarantees it.
        Assert.Equal("23505", exception.SqlState);
    }
}
