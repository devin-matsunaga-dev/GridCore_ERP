using System.Text.Json;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using GridCore.Platform.UnitTests.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.UnitTests.Audit;

public class AuditLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 30, 0, TimeSpan.Zero);

    private sealed record Bill(string Number, decimal Amount);

    [Fact]
    public async Task Records_who_did_what_with_the_before_and_after_snapshots()
    {
        using var database = new PlatformTestDatabase();
        var log = new AuditLog(database.Context, new FakeCurrentUser("user-7", "billing"), new FakeClock(Now));

        await log.RecordAsync(
            "bill.adjust",
            "billing.bill",
            "b-1001",
            before: new Bill("B-1001", 120.50m),
            after: new Bill("B-1001", 95.00m));

        await using var reader = database.NewContext();
        var entry = await reader.AuditEntries.SingleAsync();

        Assert.Equal("user-7", entry.UserId);
        Assert.Equal("billing", entry.UserName);
        Assert.Equal("bill.adjust", entry.Action);
        Assert.Equal("billing.bill", entry.EntityType);
        Assert.Equal("b-1001", entry.EntityId);
        Assert.Equal(Now, entry.OccurredAt);
        Assert.Equal(120.50m, JsonSerializer.Deserialize<Bill>(entry.BeforeJson!, AuditJson.Options)!.Amount);
        Assert.Equal(95.00m, JsonSerializer.Deserialize<Bill>(entry.AfterJson!, AuditJson.Options)!.Amount);
    }

    [Fact]
    public async Task A_creation_has_no_before_snapshot_and_a_deletion_has_no_after()
    {
        using var database = new PlatformTestDatabase();
        var log = new AuditLog(database.Context, new FakeCurrentUser("user-7"), new FakeClock(Now));

        await log.RecordAsync("bill.create", "billing.bill", "b-1", after: new Bill("B-1", 10m));
        await log.RecordAsync("bill.void", "billing.bill", "b-1", before: new Bill("B-1", 10m));

        await using var reader = database.NewContext();
        var entries = await reader.AuditEntries.OrderBy(entry => entry.Action).ToListAsync();

        Assert.Null(entries[0].BeforeJson);
        Assert.NotNull(entries[0].AfterJson);
        Assert.NotNull(entries[1].BeforeJson);
        Assert.Null(entries[1].AfterJson);
    }

    [Fact]
    public async Task Background_work_is_attributed_to_the_system_rather_than_to_nobody()
    {
        using var database = new PlatformTestDatabase();
        var log = new AuditLog(database.Context, SystemUser.Instance, new FakeClock(Now));

        await log.RecordAsync("billing.cycle-run", "billing.cycle", "2026-08");

        await using var reader = database.NewContext();

        Assert.Equal(SystemUser.SystemUserId, (await reader.AuditEntries.SingleAsync()).UserId);
    }

    [Fact]
    public void Record_defers_the_write_so_it_commits_with_the_change_it_describes()
    {
        using var database = new PlatformTestDatabase();
        var log = new AuditLog(database.Context, new FakeCurrentUser("user-7"), new FakeClock(Now));

        log.Record("bill.adjust", "billing.bill", "b-1");

        using var reader = database.NewContext();

        Assert.Empty(reader.AuditEntries);
    }

    [Fact]
    public async Task An_action_with_no_entity_is_refused_rather_than_written_unattributable()
    {
        using var database = new PlatformTestDatabase();
        var log = new AuditLog(database.Context, new FakeCurrentUser("user-7"), new FakeClock(Now));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => log.RecordAsync("bill.adjust", "billing.bill", entityId: "   "));
    }

    [Fact]
    public void Ids_are_time_ordered_so_the_trail_reads_chronologically_by_key()
    {
        var clock = new FakeClock(Now);
        var first = AuditEntry.For(clock.GetUtcNow(), "u", null, "a", "t", "1", null, null, null);

        clock.Advance(TimeSpan.FromSeconds(30));

        var second = AuditEntry.For(clock.GetUtcNow(), "u", null, "a", "t", "2", null, null, null);

        Assert.True(first.Id.CompareTo(second.Id) < 0);
    }
}
