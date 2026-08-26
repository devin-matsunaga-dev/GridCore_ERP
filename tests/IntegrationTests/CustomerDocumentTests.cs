using System.Text.Json;
using GridCore.Contracts.Directories;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Documents;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Documents;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// WP-2.14's documents against real Postgres: an account statement composed across three registers,
/// and a bill reprinted from a schema whose migration has been applied.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves everything that does not need infrastructure — the statement's arithmetic,
/// the reprint's guards, the CSV escaping, the permission gates and both audit entries, all in
/// milliseconds against doubles. What only containers can show is what those doubles were standing
/// in for: that the <i>real</i> <see cref="IBillDirectory.ActivityForCustomerAsync"/> and
/// <see cref="IPaymentDirectory.ForCustomerAsync"/> — the two seams this package widened — answer
/// the way the doubles claimed, so a statement built on them proves out against schemas rather than
/// against dictionaries.
/// </para>
/// <para>
/// <b>Both new queries are translations, not obligations.</b> The billing history filters on a
/// nullable <c>IssuedOn</c>, includes a child collection and orders by key; the payment history
/// orders the entity and projects in the database. Either can compile and throw at run time against
/// Npgsql — WP-2.9's lesson, and the reason these two are here at all.
/// </para>
/// <para>
/// <b>There is no event to wait for.</b> Producing a document publishes nothing — a read that
/// changes no state has nothing for another module to act on — so, like
/// <see cref="CustomerNoteLogTests"/>, this file never touches the broker.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CustomerDocumentTests(GateFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// How many reading cycles a helper will run before it accepts that the meter is unreadable —
    /// <see cref="AnIssuedBillAsync"/>. The simulator misses one read in twenty-five on purpose.
    /// </summary>
    private const int ReadAttempts = 5;

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_statement_composed_over_the_REAL_seams_proves_out()
    {
        // THE PACKAGE'S CLAIM, end to end and through nobody's tables. Billing raised and issued the
        // bill, Payments took the money, Customers holds the deposit — and the statement adds the
        // three of them up without any module reading another's schema.
        var bill = await AnIssuedBillAsync();

        await TakeAsync(bill.Id, 10.00m);
        await CollectAsync(bill.CustomerId, 75.00m);

        var statement = await StatementAsync(bill.CustomerId, WholeOfTime);

        Assert.Equal(
            statement.OpeningBalance + Money.Total(statement.Entries.Select(entry => entry.Amount)),
            statement.ClosingBalance);

        // Opened at nothing — this customer was registered inside the test — billed, then paid.
        Assert.Equal(Money.Zero, statement.OpeningBalance);
        Assert.Equal(bill.TotalAmount, statement.Billed);
        Assert.Equal(10.00m, statement.Paid);
        Assert.Equal(bill.TotalAmount - 10.00m, statement.ClosingBalance);

        // And the deposit is in its own column, having moved nothing that is owed.
        Assert.Equal(75.00m, statement.ClosingDepositHeld);
        Assert.False(statement.IsTruncated);
    }

    [Fact]
    public async Task A_CORRECTION_reaches_the_statement_through_the_billing_seam()
    {
        // The half of `ActivityForCustomerAsync` a dictionary cannot prove: the Include of a child
        // collection, materialised and projected out of Postgres.
        var bill = await AnIssuedBillAsync();

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBillService>()
                .AdjustAsync(bill.Id, new AdjustBillInput(BillAdjustmentKind.Credit, 5.00m, "Meter misread"));
        }

        var statement = await StatementAsync(bill.CustomerId, WholeOfTime);

        var correction = Assert.Single(statement.Entries, entry => entry.Kind is StatementEntryKind.BillCorrected);

        Assert.Equal(-5.00m, correction.Amount);
        Assert.Equal("Credit on bill " + bill.BillNumber + ": Meter misread", correction.Description);
        Assert.Equal(bill.TotalAmount - 5.00m, statement.ClosingBalance);
    }

    [Fact]
    public async Task A_range_with_no_activity_answers_a_statement_rather_than_an_error()
    {
        var bill = await AnIssuedBillAsync();

        // A window before this customer existed at all.
        var statement = await StatementAsync(
            bill.CustomerId,
            new StatementRange(new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 31)));

        Assert.Empty(statement.Entries);
        Assert.Equal(Money.Zero, statement.OpeningBalance);
        Assert.Equal(Money.Zero, statement.ClosingBalance);
    }

    [Fact]
    public async Task Producing_a_statement_writes_its_audit_entry_and_no_customer_row()
    {
        var bill = await AnIssuedBillAsync();

        await StatementAsync(bill.CustomerId, WholeOfTime);

        await using var scope = fixture.CreateScope();

        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entry = await platform.AuditEntries
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Action == AuditActions.CustomerStatementProduced
                && candidate.EntityId == bill.CustomerId.ToString());

        Assert.Equal(AuditEntityTypes.CustomerDocument, entry.EntityType);

        // No before: nothing changed, which is the whole point of a read that is audited.
        Assert.Null(entry.BeforeJson);

        var snapshot = JsonSerializer.Deserialize<StatementSnapshot>(entry.AfterJson!, AuditJson.Options);

        Assert.NotNull(snapshot);

        // The figures are IN the entry, so it answers on its own years later: following the customer
        // id to a balance that has moved a hundred times since does not say what the customer was
        // told. The number here is the CUSTOMER's account number, not the service account the bill
        // was raised against.
        Assert.NotEmpty(snapshot.AccountNumber);
        Assert.Equal(bill.TotalAmount, snapshot.ClosingBalance);
        Assert.False(snapshot.IsTruncated);
    }

    [Fact]
    public async Task A_reprint_reproduces_the_bill_as_issued_and_lists_its_corrections_separately()
    {
        var bill = await AnIssuedBillAsync();

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBillService>()
                .AdjustAsync(bill.Id, new AdjustBillInput(BillAdjustmentKind.Credit, 5.00m, "Meter misread"));
        }

        await using var scope2 = fixture.CreateScope();

        var document = await scope2.ServiceProvider.GetRequiredService<IBillDocumentService>().ReprintAsync(bill.Id);

        // The printed total still says what the customer holds a copy of, and its own lines add up
        // to it — the guard BillDocument.Of refuses to print without, here against stored rows
        // rather than an in-memory aggregate.
        Assert.Equal(bill.TotalAmount, document.PrintedTotal);
        Assert.Equal(bill.TotalAmount, Money.Total(document.Lines.Select(line => line.Amount)));
        Assert.NotEmpty(document.Lines);

        Assert.Equal(-5.00m, Assert.Single(document.Corrections).Amount);
        Assert.Equal(bill.TotalAmount - 5.00m, document.AmountDue);

        var platform = scope2.ServiceProvider.GetRequiredService<PlatformDbContext>();

        Assert.True(await platform.AuditEntries.AnyAsync(entry =>
            entry.Action == AuditActions.BillReprinted && entry.EntityId == bill.Id.ToString()));
    }

    [Fact]
    public async Task A_payment_history_export_carries_every_attempt_with_its_bill_and_its_premise()
    {
        // The other widened seam, and the one place an address crosses from the premise registry
        // into a document: a customer with three connections has to be able to tell which one a
        // payment was for.
        var bill = await AnIssuedBillAsync();

        await TakeAsync(bill.Id, 10.00m);

        await using var scope = fixture.CreateScope();

        var export = await scope.ServiceProvider.GetRequiredService<ICustomerDocumentService>()
            .ExportPaymentHistoryAsync(bill.CustomerId);

        Assert.Equal(1, export.Rows);
        Assert.Contains(bill.BillNumber, export.Csv, StringComparison.Ordinal);
        Assert.Contains(bill.AccountNumber, export.Csv, StringComparison.Ordinal);
        Assert.Contains("Songsong", export.Csv, StringComparison.Ordinal);
        Assert.EndsWith(".csv", export.FileName, StringComparison.Ordinal);
    }

    /// <summary>Every day a test in this file could care about.</summary>
    private static StatementRange WholeOfTime =>
        new(new DateOnly(2020, 1, 1), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1));

    private async Task<AccountStatement> StatementAsync(Guid customerId, StatementRange range)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerDocumentService>()
            .StatementAsync(customerId, range);
    }

    private async Task<DepositEntry> CollectAsync(Guid customerId, decimal amount)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerDepositService>()
            .CollectAsync(customerId, new CollectDepositInput(amount, IsInterestBearing: false, "Taken at the counter."));
    }

    private async Task<PaymentResult> TakeAsync(Guid billId, decimal amount)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPaymentService>()
            .TakeAsync(new TakePaymentInput(billId, amount, PaymentMethods.Cash, null));
    }

    /// <summary>
    /// An issued bill on an energised account, assembled through four modules' own services exactly
    /// as the application would.
    /// </summary>
    /// <remarks>
    /// The shape <see cref="CustomerNoteLogTests"/> uses, <b>including its retry</b>: the simulator
    /// misses one read in twenty-five by design, so a helper that runs one cycle and asserts a bill
    /// came out of it fails about four times in a hundred for a reason with nothing to do with what
    /// is under test. Each attempt draws a fresh cycle code, so five of them miss with probability
    /// about one in ten million.
    /// </remarks>
    private async Task<Bill> AnIssuedBillAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];

        Guid premise;
        Guid customer;

        await using (var scope = fixture.CreateScope())
        {
            customer = (await scope.ServiceProvider.GetRequiredService<ICustomerService>()
                .RegisterAsync(new RegisterCustomerInput($"Document customer {tag}", CustomerClass.Residential, "Ana Reyes")))
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
            var account = await accounts.OpenAsync(new OpenServiceAccountInput(customer, premise, "Requested at the counter"));

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

        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            var cycle = $"DOC-{tag}-{attempt}";

            await using (var scope = fixture.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                    .RunCycleAsync(new RunReadingCycleInput(cycle, Seed: 5171 + attempt));
            }

            await using (var scope = fixture.CreateScope())
            {
                var bills = scope.ServiceProvider.GetRequiredService<IBillService>();
                var run = await bills.RunAsync(new RunBillingInput(cycle));

                // Empty means the simulator refused this meter on this cycle — the modelled missed
                // read. Draw again rather than fail a test about documents over it.
                if (run.Bills.Count is 0)
                {
                    continue;
                }

                return await bills.IssueAsync(Assert.Single(run.Bills).Id, new IssueBillInput());
            }
        }

        Assert.Fail($"The simulator refused meter SN-{tag} on {ReadAttempts} consecutive cycles, which is not a thing that happens.");

        return null!;
    }
}
