using GridCore.Contracts.Events;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests.Messaging;

/// <summary>
/// Invariant 2 against real infrastructure: a published event is staged in the outbox inside the
/// caller's transaction and reaches the consumer only after that transaction commits. The unit
/// tier proves the dedupe and the accounting; only this needs Postgres and a broker.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboxDeliveryTests(GateFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_event_is_staged_in_the_outbox_and_delivered_after_the_commit()
    {
        var issued = NewBillIssued(184.55m);

        // Taken before the publish: a delivery that beats the assertion is still observed.
        var delivered = fixture.Postings.NextAsync();

        await using (var scope = fixture.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            await unitOfWork.ExecuteAsync(async token =>
            {
                await publisher.PublishAsync(issued, token);

                // The publish went into the platform context, not onto the broker: it is a pending
                // database row inside this very transaction. That is the whole point of an outbox,
                // and it is why a rollback can still take the event back.
                Assert.Single(platform.ChangeTracker.Entries<OutboxMessage>());
            });
        }

        var posting = await delivered.WaitAsync(DeliveryTimeout);

        Assert.Equal(issued.EventId, posting.EventId);
        Assert.Equal("B-000123", posting.Reference);
        Assert.Equal(184.55m, posting.TotalDebits);
        Assert.Equal(posting.TotalDebits, posting.TotalCredits);
    }

    [Fact]
    public async Task Nothing_is_published_for_work_that_rolled_back()
    {
        var abandoned = NewBillIssued(99m);

        await using var scope = fixture.CreateScope();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        // Failure path: the publish happened, the transaction did not.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteAsync(async token =>
            {
                await publisher.PublishAsync(abandoned, token);

                throw new InvalidOperationException("the rate plan is missing");
            }));

        // Give the delivery service several sweeps to prove it has nothing to deliver.
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Equal(0, await fixture.CountOutboxMessagesForAsync(abandoned.EventId));
        Assert.DoesNotContain(fixture.Postings.Postings, posting => posting.EventId == abandoned.EventId);
    }

    private static BillIssued NewBillIssued(decimal amount) => BillIssued.For(
        DateTimeOffset.UtcNow,
        billId: Guid.CreateVersion7(),
        billNumber: "B-000123",
        serviceAccountId: Guid.CreateVersion7(),
        customerId: Guid.CreateVersion7(),
        periodStart: new DateOnly(2026, 7, 1),
        periodEnd: new DateOnly(2026, 7, 31),
        dueDate: new DateOnly(2026, 8, 20),
        amount: amount,
        currency: "USD");
}
