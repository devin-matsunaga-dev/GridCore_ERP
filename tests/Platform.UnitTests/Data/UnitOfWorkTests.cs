using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.UnitTests.Data;

/// <summary>
/// Cross-context atomicity: the thing invariants 1 and 2 rest on. A module's write, the audit entry
/// describing it and the outbox row announcing it live in different contexts and must still be one
/// transaction.
/// </summary>
public sealed class UnitOfWorkTests : IDisposable
{
    private readonly PlatformTestHost _host = new();

    [Fact]
    public async Task Commits_writes_from_every_context_together()
    {
        var rowId = Guid.CreateVersion7();

        await _host.InScopeAsync(async services =>
        {
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            var platform = services.GetRequiredService<PlatformDbContext>();
            var module = services.GetRequiredService<ModuleTestDbContext>();

            await unitOfWork.ExecuteAsync(_ =>
            {
                module.Rows.Add(new ModuleRow { Id = rowId, Name = "meter-swap" });
                platform.AuditEntries.Add(NewAuditEntry(rowId));

                return Task.CompletedTask;
            });

            return true;
        });

        using var moduleReader = _host.NewModuleContext();
        using var platformReader = _host.NewPlatformContext();

        Assert.Single(await moduleReader.Rows.Where(row => row.Id == rowId).ToListAsync());
        Assert.Single(await platformReader.AuditEntries.Where(entry => entry.EntityId == rowId.ToString()).ToListAsync());
    }

    [Fact]
    public async Task Rolls_back_every_context_when_the_work_throws()
    {
        var rowId = Guid.CreateVersion7();

        var thrown = await _host.InScopeAsync(async services =>
        {
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            var platform = services.GetRequiredService<PlatformDbContext>();
            var module = services.GetRequiredService<ModuleTestDbContext>();

            return await Assert.ThrowsAsync<InvalidOperationException>(() =>
                unitOfWork.ExecuteAsync(_ =>
                {
                    module.Rows.Add(new ModuleRow { Id = rowId, Name = "half-done" });
                    platform.AuditEntries.Add(NewAuditEntry(rowId));

                    throw new InvalidOperationException("the rate plan is missing");
                }));
        });

        Assert.Equal("the rate plan is missing", thrown.Message);

        using var moduleReader = _host.NewModuleContext();
        using var platformReader = _host.NewPlatformContext();

        // Neither half survived: an audit entry for a write that did not happen would be a lie, and
        // an outbox row for one would announce work nobody did.
        Assert.Empty(await moduleReader.Rows.Where(row => row.Id == rowId).ToListAsync());
        Assert.Empty(await platformReader.AuditEntries.Where(entry => entry.EntityId == rowId.ToString()).ToListAsync());
    }

    [Fact]
    public async Task Nested_execute_joins_the_outer_transaction_instead_of_committing_early()
    {
        var rowId = Guid.CreateVersion7();

        await _host.InScopeAsync(async services =>
        {
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            var module = services.GetRequiredService<ModuleTestDbContext>();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                unitOfWork.ExecuteAsync(async _ =>
                {
                    // A service calling another service that also wraps its work.
                    await unitOfWork.ExecuteAsync(__ =>
                    {
                        module.Rows.Add(new ModuleRow { Id = rowId, Name = "inner" });

                        return Task.CompletedTask;
                    });

                    throw new InvalidOperationException("the outer step failed");
                }));

            return true;
        });

        using var moduleReader = _host.NewModuleContext();

        // Had the inner call committed, the outer failure could not undo it.
        Assert.Empty(await moduleReader.Rows.Where(row => row.Id == rowId).ToListAsync());
    }

    [Fact]
    public async Task Reports_whether_a_transaction_is_open()
    {
        await _host.InScopeAsync(async services =>
        {
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();

            Assert.False(unitOfWork.IsActive);

            await unitOfWork.ExecuteAsync(_ =>
            {
                Assert.True(unitOfWork.IsActive);

                return Task.CompletedTask;
            });

            Assert.False(unitOfWork.IsActive);

            return true;
        });
    }

    [Fact]
    public async Task Returns_the_result_of_the_work()
    {
        var total = await _host.InScopeAsync(services =>
            services.GetRequiredService<IUnitOfWork>().ExecuteAsync(_ => Task.FromResult(42m)));

        Assert.Equal(42m, total);
    }

    public void Dispose() => _host.Dispose();

    private static AuditEntry NewAuditEntry(Guid rowId) => AuditEntry.For(
        DateTimeOffset.UtcNow,
        "system",
        "System",
        "module.write",
        "module.row",
        rowId.ToString(),
        before: null,
        after: new { Name = "meter-swap" },
        correlationId: null);
}
