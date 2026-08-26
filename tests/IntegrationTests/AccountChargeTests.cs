using GridCore.IntegrationTests.Infrastructure;
using GridCore.Contracts.Services;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Platform.Monetary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// A fee raised at the counter, billed on a charge bill, and posted to the ledger as fee revenue —
/// WP-2.16's cross-module effect, against real Postgres and the real broker.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves the schedule's effective dating, the charge's state machine, the shape of a
/// fee line and the permission gate — nearly every case in the package lives there. What only
/// containers can show is the rest of the sentence: that the schedule's rows really do ship with the
/// migration, that a charge bill survives a table whose meter and tariff columns are now nullable,
/// and that <c>BillIssued</c> carries its fee split across the bus into an entry that credits
/// <see cref="FinanceAccounts.ServiceFeeRevenue"/> rather than utility revenue.
/// </para>
/// <para>
/// <b>There is no meter and no reading cycle here</b>, which is the point of a charge bill: the
/// walk is customer → premise → account → fee → bill → journal entry, and it never touches
/// Metering. That also keeps it clear of the simulator's deliberate missing-read chance.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AccountChargeTests(GateFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// How long a test will wait for the broker to carry the bill to Finance. A ceiling, never a
    /// pause: the wait returns the moment the entry lands.
    /// </summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A customer with a premise and an energised account — everything a fee needs and nothing more.
    /// </summary>
    private async Task<Guid> AnAccountAsync(string customerName, string line1)
    {
        Guid customer;
        Guid premise;

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

            var account = await accounts.OpenAsync(new OpenServiceAccountInput(customer, premise, ServiceType.Electricity, "Requested at the counter"));

            await accounts.StartServiceAsync(account.Id, "Connected.");

            return account.Id;
        }
    }

    [Fact]
    public async Task The_published_schedule_ships_with_the_migration()
    {
        // Invariant 8: a migrated database can price a fee in every environment, with no seeder
        // involved. The fast tier reads the same rows out of the configuration's HasData; this is
        // the one place they are read back out of Postgres.
        await using var scope = fixture.CreateScope();

        var schedule = await scope.ServiceProvider.GetRequiredService<IFeeScheduleService>()
            .ListAsync(new DateOnly(2026, 8, 26));

        Assert.Equal(Enum.GetValues<FeeCode>().Length, schedule.Count);

        // numeric(18,2) round-trips the published figure exactly, which is the whole reason money is
        // decimal and never a float.
        Assert.Equal(60.00m, schedule.Single(fee => fee.Code == FeeCode.Reconnection).Amount);
    }

    [Fact]
    public async Task A_fee_billed_at_the_counter_becomes_a_journal_entry_that_credits_fee_revenue()
    {
        // THE CROSS-MODULE EFFECT THIS PACKAGE ADDS, end to end: Customers opens the account,
        // Billing prices the fee off its own published schedule and raises a bill of its own, and
        // Finance — which has never heard of a fee schedule — posts Dr AR / Cr fee revenue off the
        // event alone.
        var account = await AnAccountAsync("Sablan Family Residence", "128 As Nieves Road");

        AccountCharge charge;

        await using (var scope = fixture.CreateScope())
        {
            charge = await scope.ServiceProvider.GetRequiredService<IAccountChargeService>()
                .RaiseAsync(new RaiseChargeInput(
                    account,
                    FeeCode.Reconnection,
                    "Supply restored after the arrears were settled."));
        }

        Assert.Equal(AccountChargeStatus.Pending, charge.Status);

        CounterBillResult counter;

        await using (var scope = fixture.CreateScope())
        {
            counter = await scope.ServiceProvider.GetRequiredService<IAccountChargeService>()
                .BillNowAsync(charge.Id, new BillChargeInput("Paid at the counter."));
        }

        Assert.Equal(AccountChargeStatus.Billed, counter.Charge.Status);
        Assert.Equal(BillKind.Charge, counter.Bill.Kind);
        Assert.Equal(BillStatus.Issued, counter.Bill.Status);

        await using (var scope = fixture.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

            var stored = await billing.Bills
                .AsNoTracking()
                .Include(bill => bill.Lines)
                .SingleAsync(bill => bill.Id == counter.Bill.Id);

            // THE NULLABLE HALF OF THE TABLE, read back out of Postgres: a charge bill has no meter
            // and no tariff, and it still adds up to the sum of what is printed on it.
            Assert.Null(stored.MeterId);
            Assert.Null(stored.RatePlanId);
            Assert.Null(stored.UnitOfMeasure);
            Assert.Equal(stored.TotalAmount, Money.Total(stored.Lines.Select(line => line.Amount)));
            Assert.Equal(stored.TotalAmount, stored.FeeAmount);

            var storedCharge = await billing.AccountCharges.AsNoTracking().SingleAsync(row => row.Id == charge.Id);

            // The schedule row that priced it, stamped and round-tripped — how a figure stays
            // traceable after the catalogue has moved on.
            Assert.Equal(charge.FeeScheduleId, storedCharge.FeeScheduleId);
            Assert.Equal(counter.Bill.Id, storedCharge.BillId);
        }

        var entry = await AwaitPostingAsync(counter.Bill.BillNumber);

        Assert.Equal(FinancePostings.BillIssuedSource, entry.Source);
        Assert.Equal(counter.Bill.TotalAmount, entry.TotalDebits);
        Assert.Equal(entry.TotalDebits, entry.TotalCredits);
        Assert.Equal(account, entry.ServiceAccountId);

        Assert.Equal(
            counter.Bill.TotalAmount,
            entry.Lines.Single(line => line.Account.Code == FinanceAccounts.AccountsReceivable).Debit);

        // Fee revenue, not utility revenue: the chart has carried 4100 since WP-0.8 waiting for this.
        Assert.Equal(
            counter.Bill.TotalAmount,
            entry.Lines.Single(line => line.Account.Code == FinanceAccounts.ServiceFeeRevenue).Credit);

        Assert.DoesNotContain(entry.Lines, line => line.Account.Code == FinanceAccounts.Revenue);
    }

    /// <summary>Waits for the journal entry raised for <paramref name="billNumber"/> to appear.</summary>
    /// <remarks>
    /// The table is polled rather than a recorder awaited, for the reason <c>GeneralLedgerTests</c>
    /// documents: the recorder's signal fires inside the consumer's transaction, so the committed
    /// row lands a moment after it. This returns the instant the commit lands and never sleeps a
    /// fixed span (CONVENTIONS.md rule G).
    /// </remarks>
    private async Task<JournalEntry> AwaitPostingAsync(string billNumber)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using (var scope = fixture.CreateScope())
            {
                var finance = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();

                var entry = await finance.JournalEntries
                    .AsNoTracking()
                    .Include(candidate => candidate.Lines)
                    .ThenInclude(line => line.Account)
                    .FirstOrDefaultAsync(candidate =>
                        candidate.Source == FinancePostings.BillIssuedSource && candidate.Reference == billNumber);

                if (entry is not null)
                {
                    return entry;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"Finance did not post bill {billNumber} within {DeliveryTimeout}.");

        throw new InvalidOperationException("Unreachable.");
    }
}
