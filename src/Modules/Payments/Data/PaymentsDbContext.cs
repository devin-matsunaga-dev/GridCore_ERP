using GridCore.Modules.Payments.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Payments.Data;

/// <summary>
/// The Payments module's schema: every attempt to take money from a customer, and what the payment
/// provider answered.
/// </summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "payments";

    /// <summary>
    /// Every payment taken, refusals included. Append-only in spirit: a payment is answered once and
    /// a retry is a new row, never this one revived — the same habit the reading register keeps.
    /// </summary>
    public DbSet<Payment> Payments => Set<Payment>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);
    }
}
