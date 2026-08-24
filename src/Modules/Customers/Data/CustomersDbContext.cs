using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceLocations;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Data;

/// <summary>
/// The Customers module's schema: the customer registry and the premises they are served at.
/// WP-1.2 adds the service accounts that connect the two.
/// </summary>
public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "customers";

    /// <summary>The people and organisations the utility serves.</summary>
    public DbSet<Customer> Customers => Set<Customer>();

    /// <summary>The premises service is delivered to. Independent of who is served there.</summary>
    public DbSet<ServiceLocation> ServiceLocations => Set<ServiceLocation>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CustomersDbContext).Assembly);
    }
}
