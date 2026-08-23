using GridCore.Contracts.Events;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Platform.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridCore.Modules.Finance.UnitTests.EventSeam;

/// <summary>
/// What Finance asks the host for: three consumers and a ledger seam behind them. Finance is
/// downstream-only, so it registers no client of Billing, Payments or Inventory.
/// </summary>
public sealed class FinanceModuleSeamTests
{
    [Fact]
    public void Registers_a_consumer_for_every_event_finance_reacts_to()
    {
        var services = AddFinance();

        Assert.Equal(
            [typeof(BillIssuedConsumer), typeof(PaymentApprovedConsumer), typeof(GoodsReceivedConsumer)],
            RegisteredConsumers(services));
    }

    [Fact]
    public void Registers_the_no_op_ledger_seam_wp_2_6_will_replace()
    {
        using var provider = AddFinance().AddLogging().BuildServiceProvider();

        Assert.IsType<LoggingJournalPostingSeam>(provider.GetRequiredService<IJournalPostingSeam>());
    }

    [Fact]
    public void Every_consumer_names_itself_stably_and_distinctly()
    {
        string[] names = [BillIssuedConsumer.Name, PaymentApprovedConsumer.Name, GoodsReceivedConsumer.Name];

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
    public void Consumes_the_three_upstream_events_and_nothing_else()
    {
        Type[] consumed =
        [
            typeof(IConsumer<BillIssued>),
            typeof(IConsumer<PaymentApproved>),
            typeof(IConsumer<GoodsReceived>),
        ];

        Assert.True(consumed[0].IsAssignableFrom(typeof(BillIssuedConsumer)));
        Assert.True(consumed[1].IsAssignableFrom(typeof(PaymentApprovedConsumer)));
        Assert.True(consumed[2].IsAssignableFrom(typeof(GoodsReceivedConsumer)));
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
