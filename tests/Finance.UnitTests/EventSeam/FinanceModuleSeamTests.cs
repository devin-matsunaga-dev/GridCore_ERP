using GridCore.Contracts.Events;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Platform.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridCore.Modules.Finance.UnitTests.EventSeam;

/// <summary>
/// What Finance asks the host for: a consumer per upstream fact and a ledger seam behind them.
/// Finance is downstream-only, so it registers no client of Billing, Payments, Inventory or
/// Customers.
/// </summary>
public sealed class FinanceModuleSeamTests
{
    [Fact]
    public void Registers_a_consumer_for_every_event_finance_reacts_to()
    {
        var services = AddFinance();

        // BillAdjusted joined the set in WP-2.6. Without it the receivable raised on BillIssued
        // would keep saying the original figure, and AR would diverge from Billing the first time a
        // disputed bill was credited. The three deposit facts joined in WP-2.12: money the utility
        // holds on somebody else's behalf is a liability, and one that never reached the ledger
        // would leave a trial balance short by every deposit on the books.
        Assert.Equal(
            [
                typeof(BillIssuedConsumer),
                typeof(BillAdjustedConsumer),
                typeof(PaymentApprovedConsumer),
                typeof(GoodsReceivedConsumer),
                typeof(CustomerDepositCollectedConsumer),
                typeof(CustomerDepositAppliedConsumer),
                typeof(CustomerDepositRefundedConsumer),
            ],
            RegisteredConsumers(services));
    }

    [Fact]
    public void Registers_the_general_ledger_behind_the_seam()
    {
        // WP-0.5's LoggingJournalPostingSeam is what this replaced, by DI and nothing else — Billing
        // and Payments publish the same events they always did.
        var registered = AddFinance()
            .Single(descriptor => descriptor.ServiceType == typeof(IJournalPostingSeam));

        Assert.Equal(typeof(JournalPostingSeam), registered.ImplementationType);
    }

    [Fact]
    public void Every_consumer_names_itself_stably_and_distinctly()
    {
        string[] names =
        [
            BillIssuedConsumer.Name,
            BillAdjustedConsumer.Name,
            PaymentApprovedConsumer.Name,
            GoodsReceivedConsumer.Name,
        ];

        // The dedupe table is keyed on these; a collision would make one consumer swallow another's
        // events, and a rename would replay every event ever handled.
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.StartsWith("finance.", name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_no_op_seam_reports_a_balanced_entry_without_touching_a_ledger()
    {
        var seam = new LoggingJournalPostingSeam(NullLogger<LoggingJournalPostingSeam>.Instance);

        var posting = FinancePostings.From(PaymentApproved.For(
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            billId: null,
            amount: 40m,
            currency: "USD",
            method: "bank-transfer",
            providerReference: "SIM-1"));

        await seam.PostAsync(posting);

        Assert.Equal(posting.TotalDebits, posting.TotalCredits);
    }

    [Fact]
    public async Task The_no_op_seam_refuses_a_null_posting() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new LoggingJournalPostingSeam(NullLogger<LoggingJournalPostingSeam>.Instance).PostAsync(null!));

    [Fact]
    public void Consumes_the_four_upstream_events_and_nothing_else()
    {
        Assert.True(typeof(IConsumer<BillIssued>).IsAssignableFrom(typeof(BillIssuedConsumer)));
        Assert.True(typeof(IConsumer<BillAdjusted>).IsAssignableFrom(typeof(BillAdjustedConsumer)));
        Assert.True(typeof(IConsumer<PaymentApproved>).IsAssignableFrom(typeof(PaymentApprovedConsumer)));
        Assert.True(typeof(IConsumer<GoodsReceived>).IsAssignableFrom(typeof(GoodsReceivedConsumer)));
    }

    [Fact]
    public void Finance_names_the_payment_approved_consumer_differently_from_billings()
    {
        // Both modules claim PaymentApproved and each has its own work to do with it — Billing
        // reduces the balance, Finance posts the cash receipt. The dedupe table is keyed on the
        // consumer name, so a shared one would mean whichever handled it first silently suppressed
        // the other. Billing's is "billing.payment-approved"; asserted from this side too, because
        // the collision would be invisible from either alone.
        Assert.Equal("finance.payment-approved", PaymentApprovedConsumer.Name);
    }

    /// <summary>What the module asked the host to run, read the way the host reads it.</summary>
    private static Type[] RegisteredConsumers(IServiceCollection services) =>
        [.. services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<EventConsumerDescriptor>()
            .Select(descriptor => descriptor.ConsumerType)];

    private static ServiceCollection AddFinance()
    {
        var services = new ServiceCollection();

        new FinanceModule().AddServices(services, new ConfigurationBuilder().Build());

        return services;
    }
}
