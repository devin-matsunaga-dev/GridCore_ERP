using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.UnitTests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.UnitTests.Messaging;

/// <summary>The dedupe helper invariant 2 names: every consumer is idempotent.</summary>
public sealed class MessageDeduplicatorTests : IDisposable
{
    private const string Consumer = "finance.bill-issued";

    private readonly PlatformTestHost _host = new(new FakeClock(new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task Claims_an_event_the_first_time()
    {
        var eventId = Guid.CreateVersion7();

        var claimed = await ClaimAndSaveAsync(eventId, Consumer);

        Assert.True(claimed);

        using var reader = _host.NewPlatformContext();

        Assert.Single(await reader.ProcessedMessages.Where(message => message.MessageId == eventId).ToListAsync());
    }

    [Fact]
    public async Task Refuses_the_same_event_on_redelivery()
    {
        var eventId = Guid.CreateVersion7();

        Assert.True(await ClaimAndSaveAsync(eventId, Consumer));
        Assert.False(await ClaimAndSaveAsync(eventId, Consumer));

        using var reader = _host.NewPlatformContext();

        Assert.Single(await reader.ProcessedMessages.Where(message => message.MessageId == eventId).ToListAsync());
    }

    [Fact]
    public async Task Refuses_a_repeat_claim_inside_the_same_unit_of_work()
    {
        var eventId = Guid.CreateVersion7();

        var (first, second) = await _host.InScopeAsync(async services =>
        {
            var deduplicator = services.GetRequiredService<IMessageDeduplicator>();

            // The first claim is not saved yet, so a database read alone would not see it.
            return (await deduplicator.TryBeginAsync(eventId, Consumer),
                await deduplicator.TryBeginAsync(eventId, Consumer));
        });

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task Gives_every_consumer_its_own_turn_at_the_same_event()
    {
        var eventId = Guid.CreateVersion7();

        Assert.True(await ClaimAndSaveAsync(eventId, "finance.bill-issued"));
        Assert.True(await ClaimAndSaveAsync(eventId, "notifications.bill-issued"));

        using var reader = _host.NewPlatformContext();

        Assert.Equal(2, await reader.ProcessedMessages.CountAsync(message => message.MessageId == eventId));
    }

    [Fact]
    public async Task Refuses_an_event_with_no_identity()
    {
        // Failure path: an event built by hand instead of by its For(...) factory cannot be
        // deduplicated at all, so it is rejected loudly rather than processed twice quietly.
        var thrown = await _host.InScopeAsync(services =>
            Assert.ThrowsAsync<ArgumentException>(() =>
                services.GetRequiredService<IMessageDeduplicator>().TryBeginAsync(Guid.Empty, Consumer)));

        Assert.Equal("messageId", thrown.ParamName);
    }

    [Fact]
    public async Task Refuses_a_blank_consumer_name() =>
        await _host.InScopeAsync(services =>
            Assert.ThrowsAnyAsync<ArgumentException>(() =>
                services.GetRequiredService<IMessageDeduplicator>().TryBeginAsync(Guid.CreateVersion7(), "  ")));

    public void Dispose() => _host.Dispose();

    private Task<bool> ClaimAndSaveAsync(Guid eventId, string consumer) =>
        _host.InScopeAsync(services =>
            services.GetRequiredService<IUnitOfWork>().ExecuteAsync(token =>
                services.GetRequiredService<IMessageDeduplicator>().TryBeginAsync(eventId, consumer, token)));
}
