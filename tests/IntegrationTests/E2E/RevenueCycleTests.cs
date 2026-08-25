using GridCore.Contracts.Events;
using System.Diagnostics;
using GridCore.Contracts.Providers;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Modules.Finance.Features.Reports;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Modules.Payments.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests.E2E;

/// <summary>
/// SPEC.md's <b>Revenue Cycle</b>, walked once, for real: Create Customer → Create Service Account →
/// Assign Meter → Generate Simulated Reading → Calculate Consumption → Generate Bill → Run Simulated
/// Payment → Update Balance → Generate Accounting Entries.
/// </summary>
/// <remarks>
/// <para>
/// This is the first of the two demonstration workflows and the MVP's acceptance target, so the
/// test's job is not to re-prove any single module — every one of them has its own fast tier and
/// its own gate-tier slice. Its job is to prove the <b>joins</b>: that nine steps performed in
/// order, through five schemas, one broker and two provider simulators, leave the utility's books
/// saying the same thing the customer's bill says. Each step asserts the downstream effect it is
/// supposed to cause, which is what WORK_PACKAGES.md asks WP-2.7 for, rather than running the whole
/// thing and looking only at the ledger at the end.
/// </para>
/// <para>
/// <b>Why this is not a separate xUnit collection.</b> The tier is E2E and the trait says so, but
/// the containers are the gate tier's: a second <c>ICollectionFixture&lt;GateFixture&gt;</c> would
/// start a second Postgres, a second RabbitMQ and a second Redis for the sake of a naming
/// distinction — the exact per-class-container mistake CONVENTIONS.md rule D exists to prevent. So
/// the class joins <see cref="GateCollection"/> like every other gate test and carries
/// <c>Tier=E2E</c> as a second trait, which is what <c>--filter "Tier=E2E"</c> selects on. The
/// <c>Category=Integration</c> trait is what keeps it out of the fast loop, and it is deliberately
/// the same value the rest of the gate tier uses: a second value on that key would have to be
/// excluded by name in every fast-loop command in the repository.
/// </para>
/// <para>
/// The walk drives each module's own service rather than its HTTP endpoint, the shape the rest of
/// the gate tier uses. The endpoints, their policies and their problem responses are asserted in
/// the fast tier where they cost milliseconds; what needs containers is the seams underneath, and
/// a service call reaches every one of them.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Tier", "E2E")]
public sealed class RevenueCycleTests(GateFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// How long a step will wait for the broker to carry a fact to a consumer in another module.
    /// A ceiling, never a pause: every wait returns the moment the thing it watches for happens
    /// (CONVENTIONS.md rule G).
    /// </summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The seed every walk here reads on. <see cref="MeterReadingsDemoSeeder"/>'s own, so the demo
    /// world and the test see the same simulator.
    /// </summary>
    private const int Seed = 4471;

    /// <summary>
    /// The meter every walk fits. Deterministic, and it has to be — see <see cref="CycleCodeFor"/>.
    /// </summary>
    private const string SerialNumber = "SEN-E2E-0001";

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_revenue_cycle_runs_end_to_end_and_the_books_reconcile()
    {
        var cycle = CycleCodeFor("revenue");

        // ── 1. Create Customer ───────────────────────────────────────────────────────────────────
        var customer = await RegisterCustomerAsync("Reyes Family Residence");

        Assert.Equal(CustomerStatus.Prospect, customer.Status);
        Assert.StartsWith(CustomerNumbers.CustomerPrefix, customer.AccountNumber, StringComparison.Ordinal);

        var premise = await RegisterPremiseAsync("77 As Nieves Road");

        // ── 2. Create Service Account ────────────────────────────────────────────────────────────
        var opened = await OpenAccountAsync(customer.Id, premise.Id);

        Assert.Equal(ServiceAccountStatus.Pending, opened.Status);

        // Energising is a separate act, and the billing run refuses an account that never was:
        // "Service account … has never been energised".
        var account = await StartServiceAsync(opened.Id);

        Assert.Equal(ServiceAccountStatus.Active, account.Status);
        Assert.NotNull(account.ServiceStartedAt);

        // ── 3. Assign Meter ──────────────────────────────────────────────────────────────────────
        const decimal installationReading = 4_200.000m;

        var registered = await RegisterMeterAsync(SerialNumber);

        Assert.Equal(MeterStatus.InStore, registered.Status);

        var meter = await FitMeterAsync(registered.Id, premise.Id, installationReading);

        // A meter is fitted to a PREMISE, never to an account (WP-2.1). The bill resolves the
        // account from the premise, which is the derivation this step exists to exercise.
        Assert.Equal(MeterStatus.Installed, meter.Status);
        Assert.Equal(premise.Id, meter.ServiceLocationId);
        Assert.Equal(installationReading, meter.InstallationReading);

        // ── 4. Generate Simulated Reading ────────────────────────────────────────────────────────
        // Through IMeterReadingProvider — the simulator is reached the way production would reach a
        // real AMI head end, and domain code never calls it directly (invariant 6).
        var run = await RunReadingCycleAsync(cycle);

        Assert.Equal(cycle, run.CycleCode);

        // The batch is stamped with the name of whatever is registered against the CONTRACTS
        // interface, compared here against the resolved provider rather than against a literal:
        // production swaps that registration for a real head end and this assertion still holds.
        Assert.Equal(await ReadingProviderNameAsync(), run.Provider);

        var reading = Assert.Single(run.Readings, candidate => candidate.MeterId == meter.Id);

        Assert.NotNull(reading.Reading);
        Assert.Equal(premise.Id, reading.ServiceLocationId);

        // ── 5. Calculate Consumption ─────────────────────────────────────────────────────────────
        // Computed inside Metering from what the provider returned — a provider reads meters and
        // never decides what a reading means.
        Assert.NotNull(reading.Consumption);
        Assert.Equal(installationReading, reading.PreviousReading);
        Assert.Equal(reading.Reading!.Value - installationReading, reading.Consumption!.Value);
        Assert.False(reading.RolledOver);

        // A newly metered premise has no usage profile to be judged against, so it is billable
        // rather than parked on the exception worklist.
        Assert.False(reading.IsException);
        Assert.Equal(ReadingExceptionCode.None, reading.ExceptionCode);

        // ── 6. Generate Bill ─────────────────────────────────────────────────────────────────────
        var billingRun = await RunBillingAsync(cycle);
        var draft = BillFor(billingRun, account);

        // A run produces DRAFTS and publishes nothing (WP-2.3), so nothing has reached Finance yet.
        Assert.Equal(BillStatus.Draft, draft.Status);
        Assert.Equal(reading.Consumption!.Value, draft.Consumption);
        Assert.Equal(reading.Id, draft.MeterReadingId);
        Assert.Equal(meter.MeterNumber, draft.MeterNumber);
        Assert.Equal(customer.Name, draft.CustomerName);
        Assert.Equal(account.AccountNumber, draft.AccountNumber);

        // The rate engine priced it, and the printed total is the sum of the printed lines — the
        // first thing a customer checks by hand.
        Assert.NotEmpty(draft.Lines);
        Assert.Equal(draft.TotalAmount, draft.Lines.Sum(line => line.Amount));
        Assert.True(draft.TotalAmount > 0m);

        Assert.Empty(await JournalEntriesForAsync(account.Id));

        // Issuing is the separate act that makes the bill money the utility is owed, and it is what
        // publishes BillIssued.
        var issued = await IssueBillAsync(draft.Id);

        Assert.Equal(BillStatus.Issued, issued.Status);
        Assert.Equal(issued.TotalAmount, issued.Balance);
        Assert.NotNull(issued.DueDate);

        // ── 9a. Generate Accounting Entries — the charge ─────────────────────────────────────────
        // Asserted here rather than at the end: the receivable is raised by issuing the bill, and a
        // test that only looked once, at the finish, could not tell which step had posted what.
        var charge = await AwaitEntryAsync(account.Id, FinancePostings.BillIssuedSource);

        Assert.Equal(issued.BillNumber, charge.Reference);
        Assert.Equal(customer.Id, charge.CustomerId);
        Assert.Equal(issued.TotalAmount, charge.TotalDebits);
        Assert.Equal(charge.TotalDebits, charge.TotalCredits);
        Assert.Equal(
            issued.TotalAmount,
            charge.Lines.Single(line => line.Account.Code == FinanceAccounts.AccountsReceivable).Debit);
        Assert.Equal(
            issued.TotalAmount,
            charge.Lines.Single(line => line.Account.Code == FinanceAccounts.Revenue).Credit);

        // The accounting date is the event's, never the clock's.
        Assert.Equal(DateOnly.FromDateTime(charge.OccurredAt.UtcDateTime), charge.PostedOn);

        // ── 7. Run Simulated Payment ─────────────────────────────────────────────────────────────
        // Through IPaymentProvider. Cash, so the sandbox cannot refuse it and this step is about the
        // seam rather than about which payment number came up.
        var payment = await TakePaymentAsync(issued.Id, issued.Balance, PaymentMethods.Cash, instrument: null);

        Assert.Equal(PaymentStatus.Approved, payment.Payment.Status);
        Assert.Equal(PaymentOutcome.Approved, payment.Payment.Outcome);
        Assert.Equal(issued.Balance, payment.Payment.BalanceBefore);
        Assert.Equal(issued.BillNumber, payment.Bill.BillNumber);

        // ── 8. Update Balance ────────────────────────────────────────────────────────────────────
        // In BILLING's schema, by Billing's own consumer, from an event Payments published — the
        // Payments module has never heard of a billing schema and there is no endpoint for this.
        var settled = await EventuallyAsync(
            () => BillAsync(issued.Id),
            candidate => candidate.Status is BillStatus.Paid);

        Assert.Equal(BillStatus.Paid, settled.Status);
        Assert.Equal(issued.TotalAmount, settled.AmountPaid);
        Assert.Equal(0m, settled.Balance);

        // What was printed never moved. It is what the customer holds a copy of.
        Assert.Equal(issued.TotalAmount, settled.TotalAmount);
        Assert.NotNull(settled.PaidAt);

        // ── 9b. Generate Accounting Entries — the receipt ────────────────────────────────────────
        var receipt = await AwaitEntryAsync(account.Id, FinancePostings.PaymentApprovedSource);

        Assert.Equal(issued.TotalAmount, receipt.TotalDebits);
        Assert.Equal(receipt.TotalDebits, receipt.TotalCredits);
        Assert.Equal(
            issued.TotalAmount,
            receipt.Lines.Single(line => line.Account.Code == FinanceAccounts.Cash).Debit);
        Assert.Equal(
            issued.TotalAmount,
            receipt.Lines.Single(line => line.Account.Code == FinanceAccounts.AccountsReceivable).Credit);

        // A consumer runs outside any request, so the posting is audited against `system` — the
        // clerk is named on the bill's and the payment's own entries.
        Assert.Equal("system", receipt.ActorId);

        // ── The numbers reconcile ────────────────────────────────────────────────────────────────
        await using var scope = fixture.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<IFinanceReportService>();

        // Invariant 3, over everything the walk posted.
        var trialBalance = await reports.TrialBalanceAsync();

        Assert.True(trialBalance.IsBalanced);
        Assert.Equal(0m, trialBalance.Difference);

        // Revenue was recognised once, for what the bill printed, and the cash account holds it.
        Assert.Equal(
            issued.TotalAmount,
            trialBalance.Rows.Single(row => row.AccountCode == FinanceAccounts.Revenue).Balance);
        Assert.Equal(
            issued.TotalAmount,
            trialBalance.Rows.Single(row => row.AccountCode == FinanceAccounts.Cash).Balance);

        // And the subsidiary ledger agrees with its control account: charged, settled, nothing left
        // owed. This is the assertion the whole walk is for — Billing says the bill is paid, and the
        // books, which were told by two independent events, say the customer owes nothing.
        var receivables = await reports.ReceivablesAsync(new ReceivablesQuery(ServiceAccountId: account.Id));
        var row = Assert.Single(receivables.Rows);

        Assert.Equal(issued.TotalAmount, row.Charged);
        Assert.Equal(issued.TotalAmount, row.Settled);
        Assert.Equal(0m, row.Outstanding);
        Assert.Equal(customer.Id, row.CustomerId);
        Assert.Equal(0m, receivables.TotalOutstanding);
        Assert.Equal(0m, receivables.Unallocated);
    }

    [Fact]
    public async Task A_refused_payment_leaves_the_customer_owing_what_the_books_say_they_owe()
    {
        // The failure path at the scale of the whole cycle. Everything up to the payment happens the
        // same way; the provider then refuses, and the point is that the refusal propagates NOWHERE.
        // The bill is still owed, the ledger still carries the receivable, and AR agrees with both —
        // a demonstration in which a declined card quietly settled a bill would be worse than one
        // that never took a payment at all.
        var cycle = CycleCodeFor("refusal");

        var customer = await RegisterCustomerAsync("Camacho Family Residence");
        var premise = await RegisterPremiseAsync("12 Sinapalo Road");
        var account = await StartServiceAsync((await OpenAccountAsync(customer.Id, premise.Id)).Id);

        var meter = await FitMeterAsync((await RegisterMeterAsync(SerialNumber)).Id, premise.Id, 8_100.000m);

        var run = await RunReadingCycleAsync(cycle);
        var reading = Assert.Single(run.Readings, candidate => candidate.MeterId == meter.Id);

        // Asserted rather than assumed, so a simulator change that made this premise's reading
        // unbillable reads as "the reading was flagged" and not as "the billing run raised nothing".
        Assert.False(reading.IsException, $"The simulated reading was flagged {reading.ExceptionCode}.");

        var billingRun = await RunBillingAsync(cycle);
        var issued = await IssueBillAsync(BillFor(billingRun, account).Id);

        // The charge reaches the ledger, so what follows is tested against a real receivable.
        await AwaitEntryAsync(account.Id, FinancePostings.BillIssuedSource);

        // A pinned instrument makes the refusal deliberate rather than a matter of which payment
        // number the series happened to hand out.
        var refused = await TakePaymentAsync(
            issued.Id,
            issued.Balance,
            PaymentMethods.Card,
            instrument: $"•••• {SimulatedPaymentProvider.DeclinedInstrumentSuffix}");

        Assert.Equal(PaymentStatus.Declined, refused.Payment.Status);
        Assert.Equal(PaymentOutcome.Declined, refused.Payment.Outcome);
        Assert.False(refused.Payment.IsSettled);

        // A refusal is an answer, not an error: the attempt is a row somebody can be shown when they
        // ask why the customer still owes money.
        Assert.NotEqual(Guid.Empty, refused.Payment.Id);

        // Nothing was published, so no consumer anywhere can have moved anything.
        Assert.Equal(0, await fixture.CountOutboxMessagesForAsync(refused.Payment.Id));

        var unpaid = await BillAsync(issued.Id);

        Assert.Equal(BillStatus.Issued, unpaid.Status);
        Assert.Equal(0m, unpaid.AmountPaid);
        Assert.Equal(issued.TotalAmount, unpaid.Balance);

        await using var scope = fixture.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<IFinanceReportService>();

        // Finance was told about the charge and never about a receipt, so the books say exactly what
        // the bill says: this money is still owed. The ledger balances all the same — a debt is as
        // balanced an entry as a settlement.
        var receivables = await reports.ReceivablesAsync(new ReceivablesQuery(ServiceAccountId: account.Id));

        Assert.Equal(issued.TotalAmount, receivables.TotalOutstanding);
        Assert.Equal(0m, Assert.Single(receivables.Rows).Settled);
        Assert.True((await reports.TrialBalanceAsync()).IsBalanced);

        // And no cash receipt was posted for this account, which is the entry a bug here would have
        // invented.
        Assert.DoesNotContain(
            await JournalEntriesForAsync(account.Id),
            entry => entry.Source == FinancePostings.PaymentApprovedSource);

        // The meter is beside the point of the failure and is asserted only so a break in the setup
        // cannot masquerade as the refusal working.
        Assert.Equal(MeterStatus.Installed, meter.Status);
    }

    /// <summary>
    /// The cycle code a walk reads under — fixed per walk, never a fresh Guid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Determinism, not tidiness.</b> The simulator draws each meter's stream from
    /// (seed, cycle code, meter <i>number</i>), and roughly one stream in ten answers with a missing
    /// read or unchanged dials — both of which the billing run correctly refuses to bill. A random
    /// cycle code therefore makes this walk fail about one run in ten, on a step that is working
    /// exactly as designed. Pinning the code pins the stream: the seed is fixed, the serial is
    /// fixed, and a Respawn reset leaves <c>metering.meters</c> empty so the number series hands out
    /// the same meter number every time.
    /// </para>
    /// <para>
    /// Reusing a code across resets is safe because the reset truncates the readings and the bills
    /// that the two unique indexes guard — which is the same reason the code can be fixed at all.
    /// </para>
    /// </remarks>
    private static string CycleCodeFor(string walk) => $"E2E-{walk}";

    /// <summary>
    /// The bill <paramref name="account"/> was raised in <paramref name="run"/>.
    /// </summary>
    /// <remarks>
    /// The run's own reasons are quoted when there is none. A billing run says in words why it
    /// passed a reading over, and an assertion that threw away that sentence in favour of "the
    /// collection was empty" would waste the most useful thing the run produced.
    /// </remarks>
    private static Bill BillFor(BillingRunResult run, ServiceAccount account)
    {
        if (run.Bills.FirstOrDefault(bill => bill.ServiceAccountId == account.Id) is { } raised)
        {
            return raised;
        }

        var reasons = run.Skipped.Count == 0
            ? "the run skipped nothing"
            : string.Join("; ", run.Skipped.Select(skipped => $"{skipped.MeterNumber}: {skipped.Reason}"));

        Assert.Fail($"The run raised no bill for account {account.AccountNumber} — {reasons}.");

        throw new UnreachableException();
    }

    // ── The nine steps, each through its own module's service ────────────────────────────────────

    private async Task<Customer> RegisterCustomerAsync(string name)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerService>()
            .RegisterAsync(new RegisterCustomerInput(name, CustomerClass.Residential, "Ana Reyes", null, null, 0m));
    }

    private async Task<ServiceLocation> RegisterPremiseAsync(string line1)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
            .RegisterAsync(new ServiceLocationInput(
                Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"),
                "Meter on the north wall",
                IsActive: true,
                null));
    }

    private async Task<ServiceAccount> OpenAccountAsync(Guid customerId, Guid premiseId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
            .OpenAsync(new OpenServiceAccountInput(customerId, premiseId, "Requested at the counter"));
    }

    private async Task<ServiceAccount> StartServiceAsync(Guid accountId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
            .StartServiceAsync(accountId, "Connected.");
    }

    private async Task<Meter> RegisterMeterAsync(string serialNumber)
    {
        await using var scope = fixture.CreateScope();

        return (await scope.ServiceProvider.GetRequiredService<IMeterService>()
            .RegisterAsync(new RegisterMeterInput(serialNumber, MeterType.SinglePhase, Manufacturer: "Sensus")))
            .Meter;
    }

    private async Task<Meter> FitMeterAsync(Guid meterId, Guid premiseId, decimal installationReading)
    {
        await using var scope = fixture.CreateScope();

        return (await scope.ServiceProvider.GetRequiredService<IMeterService>()
            .AssignAsync(meterId, new AssignMeterInput(premiseId, installationReading)))
            .Meter;
    }

    /// <summary>What the registered <see cref="IMeterReadingProvider"/> calls itself.</summary>
    private async Task<string> ReadingProviderNameAsync()
    {
        await using var scope = fixture.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IMeterReadingProvider>().Name;
    }

    private async Task<ReadingCycleResult> RunReadingCycleAsync(string cycleCode)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
            .RunCycleAsync(new RunReadingCycleInput(cycleCode, Seed: Seed));
    }

    private async Task<BillingRunResult> RunBillingAsync(string cycleCode)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IBillService>()
            .RunAsync(new RunBillingInput(cycleCode));
    }

    private async Task<Bill> IssueBillAsync(Guid billId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IBillService>()
            .IssueAsync(billId, new IssueBillInput());
    }

    private async Task<PaymentResult> TakePaymentAsync(Guid billId, decimal amount, string method, string? instrument)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPaymentService>()
            .TakeAsync(new TakePaymentInput(billId, amount, method, instrument));
    }

    // ── Reading the far end ──────────────────────────────────────────────────────────────────────

    private async Task<Bill> BillAsync(Guid billId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<BillingDbContext>()
            .Bills
            .AsNoTracking()
            .Include(bill => bill.Adjustments)
            .SingleAsync(bill => bill.Id == billId);
    }

    /// <summary>Every journal entry Finance has posted about <paramref name="serviceAccountId"/>.</summary>
    private async Task<IReadOnlyList<JournalEntry>> JournalEntriesForAsync(Guid serviceAccountId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<FinanceDbContext>()
            .JournalEntries
            .AsNoTracking()
            .Include(entry => entry.Lines)
            .ThenInclude(line => line.Account)
            .Where(entry => entry.ServiceAccountId == serviceAccountId)
            .ToListAsync();
    }

    /// <summary>
    /// Waits for the entry this account's <paramref name="source"/> fact should have produced.
    /// </summary>
    /// <remarks>
    /// The table is polled rather than <see cref="GateFixture.Postings"/> awaited, for the reason
    /// <c>GeneralLedgerTests</c> documents: the recorder decorates the ledger and signals inside the
    /// consumer's transaction, so the committed row lands a moment after the signal. Selecting by
    /// source matters as much here as it does in <c>PaymentRegistryTests</c> — a walk this long has
    /// more than one posting in flight, and "the next one to arrive" is not necessarily this one.
    /// </remarks>
    private async Task<JournalEntry> AwaitEntryAsync(Guid serviceAccountId, string source)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;

        while (true)
        {
            if ((await JournalEntriesForAsync(serviceAccountId)).FirstOrDefault(entry => entry.Source == source) is { } entry)
            {
                return entry;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Finance did not post a '{source}' entry for service account {serviceAccountId} within {DeliveryTimeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>Re-reads until <paramref name="condition"/> holds or the deadline passes.</summary>
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
}
