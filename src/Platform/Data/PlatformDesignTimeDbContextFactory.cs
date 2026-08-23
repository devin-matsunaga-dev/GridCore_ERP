using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GridCore.Platform.Data;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without booting the host — this is a class
/// library, so there is no host to boot. The connection string is never used: migrations are
/// generated from the model, not from a live database.
/// </summary>
public sealed class PlatformDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    /// <inheritdoc />
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=gridcore;Username=design-time;Password=design-time",
                npgsql => npgsql.MigrationsHistoryTable(
                    PlatformDbContext.MigrationsHistoryTable,
                    PlatformDbContext.SchemaName))
            .Options;

        return new PlatformDbContext(options);
    }
}
