using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Data;

/// <summary>
/// The Customers module's schema: the customer registry, the premises they are served at, and the
/// service accounts that join the two.
/// </summary>
public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "customers";

    /// <summary>The people and organisations the utility serves.</summary>
    public DbSet<Customer> Customers => Set<Customer>();

    /// <summary>The premises service is delivered to. Independent of who is served there.</summary>
    public DbSet<ServiceLocation> ServiceLocations => Set<ServiceLocation>();

    /// <summary>A customer taking service at a premise — the join, with its own lifecycle.</summary>
    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();

    /// <summary>
    /// What a customer of each class is asked for as a security deposit. Reference data shipped by
    /// migration (invariant 8), not a seeder and not a constant in the domain — WP-2.8.
    /// </summary>
    public DbSet<DepositRule> DepositRules => Set<DepositRule>();

    /// <summary>
    /// Every transition an account has been through. Exposed as a set of its own so the history of
    /// one account can be read without loading the account, which is what the history endpoint does.
    /// </summary>
    public DbSet<ServiceAccountHistoryEntry> ServiceAccountHistory => Set<ServiceAccountHistoryEntry>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CustomersDbContext).Assembly);
    }
}
