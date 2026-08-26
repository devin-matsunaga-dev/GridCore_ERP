using GridCore.Modules.Customers.Features.Contacts;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Notes;
using GridCore.Modules.Customers.Features.Profile;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Transitions;
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

    /// <summary>
    /// The people a customer's account may be discussed with — WP-2.11. Separate from the customer's
    /// own contact details, which stay on the customer row.
    /// </summary>
    public DbSet<CustomerContact> CustomerContacts => Set<CustomerContact>();

    /// <summary>
    /// Where post goes and how a customer wants to be written to — WP-2.11. A customer with no row
    /// here is a customer on the defaults, which is why this is a table rather than ten columns on
    /// <see cref="Customers"/>.
    /// </summary>
    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();

    /// <summary>
    /// Every movement of every customer's security deposit — WP-2.12. Append-only, and the thing
    /// <see cref="Customer.DepositHeld"/> is the projection of: the balance is these rows added up,
    /// never a figure a form set.
    /// </summary>
    public DbSet<DepositEntry> DepositEntries => Set<DepositEntry>();

    /// <summary>
    /// Every note and logged interaction on every customer — WP-2.13. Append-only: a correction is a
    /// new row pointing at the one it supersedes, and the only column that ever moves is the pin.
    /// </summary>
    public DbSet<CustomerNote> CustomerNotes => Set<CustomerNote>();

    /// <summary>
    /// Every class change, status change, move-in, move-out and transfer — WP-2.15. Append-only: a
    /// transition that turned out to be wrong is a new transition back, never an edited row.
    /// </summary>
    public DbSet<AccountTransition> AccountTransitions => Set<AccountTransition>();

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
