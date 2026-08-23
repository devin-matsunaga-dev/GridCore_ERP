using GridCore.Contracts.Events;
using GridCore.Platform.Messaging;

namespace GridCore.Platform.UnitTests.Messaging;

/// <summary>The one publish path there is, and the one thing it refuses to publish.</summary>
public sealed class OutboxEventPublisherTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Publishes_the_event_as_its_own_type()
    {
        var endpoint = new RecordingPublishEndpoint();
        var publisher = new OutboxEventPublisher(endpoint);

        var issued = BillIssued.For(
            Now,
            billId: Guid.CreateVersion7(Now),
            billNumber: "B-000123",
            serviceAccountId: Guid.CreateVersion7(Now),
            customerId: Guid.CreateVersion7(Now),
            periodStart: new DateOnly(2026, 7, 1),
            periodEnd: new DateOnly(2026, 7, 31),
            dueDate: new DateOnly(2026, 8, 20),
            amount: 184.55m,
            currency: "USD");

        await publisher.PublishAsync(issued);

        // Published as BillIssued, not as object: the message type is what routes it to Finance.
        Assert.Equal(issued, Assert.IsType<BillIssued>(Assert.Single(endpoint.Published)));
    }

    [Fact]
    public async Task Refuses_an_event_with_no_identity()
    {
        var endpoint = new RecordingPublishEndpoint();
        var publisher = new OutboxEventPublisher(endpoint);

        // Failure path: without an EventId no consumer could deduplicate the redelivery, so this
        // is caught at the publisher rather than becoming a double posting downstream.
        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync(
            new BillIssued(
                Guid.Empty,
                Now,
                Guid.CreateVersion7(Now),
                "B-000124",
                Guid.CreateVersion7(Now),
                Guid.CreateVersion7(Now),
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 31),
                new DateOnly(2026, 8, 20),
                10m,
                "USD")));

        Assert.Equal("event", thrown.ParamName);
        Assert.Empty(endpoint.Published);
    }

    [Fact]
    public async Task Refuses_a_null_event() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new OutboxEventPublisher(new RecordingPublishEndpoint()).PublishAsync<BillIssued>(null!));
}
