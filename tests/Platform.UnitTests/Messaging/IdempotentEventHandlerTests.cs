using GridCore.Platform.Messaging;
using GridCore.Platform.UnitTests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.UnitTests.Messaging;

/// <summary>
/// The consume path end to end without a bus: claim, run, commit — or roll the whole thing back.
/// </summary>
public sealed class IdempotentEventHandlerTests : IDisposable
{
    private const string Consumer = "finance.bill-issued";

    private readonly PlatformTestHost _host = new();

    [Fact]
    public async Task Runs_the_handler_once_and_commits_its_work_with_the_claim()
    {
        var eventId = Guid.CreateVersion7();
        var rowId = Guid.CreateVersion7();

        var ran = await HandleAsync(eventId, module => module.Rows.Add(new ModuleRow { Id = rowId, Name = "posted" }));

        Assert.True(ran);

        using var moduleReader = _host.NewModuleContext();
        using var platformReader = _host.NewPlatformContext();

        Assert.Single(await moduleReader.Rows.Where(row => row.Id == rowId).ToListAsync());
        Assert.Single(await platformReader.ProcessedMessages.Where(message => message.MessageId == eventId).ToListAsync());
    }

    [Fact]
    public async Task Skips_the_handler_on_redelivery()
    {
        var eventId = Guid.CreateVersion7();
        var runs = 0;

        Assert.True(await HandleAsync(eventId, _ => runs++));
        Assert.False(await HandleAsync(eventId, _ => runs++));

        // At-least-once delivery, exactly-once effect.
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Leaves_no_claim_behind_when_the_handler_throws()
    {
        var eventId = Guid.CreateVersion7();
        var rowId = Guid.CreateVersion7();

        // Failure path: a consumer that fails half way must not look handled, or the redelivery
        // the broker is about to make would be skipped and the work lost for good.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HandleAsync(eventId, module =>
            {
                module.Rows.Add(new ModuleRow { Id = rowId, Name = "half-posted" });

                throw new InvalidOperationException("the account is closed");
            }));

        using var platformReader = _host.NewPlatformContext();
        using var moduleReader = _host.NewModuleContext();

        Assert.Empty(await platformReader.ProcessedMessages.Where(message => message.MessageId == eventId).ToListAsync());
        Assert.Empty(await moduleReader.Rows.Where(row => row.Id == rowId).ToListAsync());

        // And the retry really does get to run.
        Assert.True(await HandleAsync(eventId, _ => { }));
    }

    [Fact]
    public async Task Refuses_an_event_with_no_identity() =>
        await Assert.ThrowsAsync<ArgumentException>(() => HandleAsync(Guid.Empty, _ => { }));

    public void Dispose() => _host.Dispose();

    private Task<bool> HandleAsync(Guid eventId, Action<ModuleTestDbContext> work) =>
        _host.InScopeAsync(services =>
        {
            var module = services.GetRequiredService<ModuleTestDbContext>();

            return services.GetRequiredService<IdempotentEventHandler>().HandleAsync(
                eventId,
                Consumer,
                _ =>
                {
                    work(module);

                    return Task.CompletedTask;
                });
        });
}
