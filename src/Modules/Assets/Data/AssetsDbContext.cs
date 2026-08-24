using GridCore.Modules.Assets.Features.Assets;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Assets.Data;

/// <summary>
/// The Assets module's schema: the utility's plant register and everything that has happened to
/// each piece of it.
/// </summary>
public sealed class AssetsDbContext(DbContextOptions<AssetsDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "assets";

    /// <summary>The utility's plant.</summary>
    public DbSet<Asset> Assets => Set<Asset>();

    /// <summary>
    /// Every line of every asset's history. Exposed as a set of its own so one asset's history can
    /// be read without loading the asset, which is what the history endpoint does — and what WP-3.4
    /// writes its maintenance lines through.
    /// </summary>
    public DbSet<AssetHistoryEntry> AssetHistory => Set<AssetHistoryEntry>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AssetsDbContext).Assembly);
    }
}
