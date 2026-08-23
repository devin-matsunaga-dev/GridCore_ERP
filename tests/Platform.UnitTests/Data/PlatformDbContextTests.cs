using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.UnitTests.Data;

public class PlatformDbContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 30, 0, TimeSpan.Zero);

    private static AuditEntry AnEntry() =>
        AuditEntry.For(Now, "user-7", "billing", "bill.adjust", "billing.bill", "b-1", null, null, null);

    [Fact]
    public async Task The_audit_trail_owns_the_platform_schema()
    {
        using var database = new PlatformTestDatabase();

        Assert.Equal(PlatformDbContext.SchemaName, database.Context.Model.GetDefaultSchema());
        Assert.Equal("audit_entries", database.Context.Model.FindEntityType(typeof(AuditEntry))!.GetTableName());

        database.Context.AuditEntries.Add(AnEntry());

        Assert.Equal(1, await database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task An_audit_entry_cannot_be_rewritten()
    {
        using var database = new PlatformTestDatabase();
        database.Context.AuditEntries.Add(AnEntry());
        await database.Context.SaveChangesAsync();

        await using var editor = database.NewContext();
        var entry = await editor.AuditEntries.SingleAsync();
        editor.Entry(entry).Property(candidate => candidate.UserId).CurrentValue = "someone-else";

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => editor.SaveChangesAsync());

        Assert.Contains("append-only", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_audit_entry_cannot_be_deleted()
    {
        using var database = new PlatformTestDatabase();
        database.Context.AuditEntries.Add(AnEntry());
        await database.Context.SaveChangesAsync();

        await using var deleter = database.NewContext();
        deleter.AuditEntries.Remove(await deleter.AuditEntries.SingleAsync());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => deleter.SaveChangesAsync());

        Assert.Contains("append-only", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await database.NewContext().AuditEntries.CountAsync());
    }
}
