using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GridCore.Modules.Assets.Data;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without booting the host — this is a class
/// library, so there is no host to boot. The connection string is never used: migrations are
/// generated from the model, not from a live database.
/// </summary>
public sealed class AssetsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AssetsDbContext>
{
    /// <inheritdoc />
    public AssetsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AssetsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=gridcore;Username=design-time;Password=design-time",
                GridCoreDbContexts.InSchema(AssetsDbContext.SchemaName))
            .Options;

        return new AssetsDbContext(options);
    }
}
