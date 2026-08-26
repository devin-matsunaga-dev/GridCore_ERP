using GridCore.Modules.Customers.Features.Customers;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Deposits;

/// <summary>Maps <see cref="DepositEntry"/> onto <c>customers.deposit_entries</c>.</summary>
public sealed class DepositEntryConfiguration : IEntityTypeConfiguration<DepositEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DepositEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("deposit_entries");

        builder.HasKey(entry => entry.Id).HasName("pk_deposit_entries");

        // Never store-generated: every id in GridCore is a Guid v7 minted in code from the clock.
        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(entry => entry.CustomerId).HasColumnName("customer_id");

        // A foreign key without a navigation, the shape ServiceAccount already uses: the database
        // guarantees an entry never points at a customer who is not there, while a navigation would
        // invite a query to walk into the customer and turn the ledger into a join it does not need.
        // Restrict, not cascade — a ledger that disappears with its subject is not a ledger.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(entry => entry.CustomerId)
            .HasConstraintName("fk_deposit_entries_customer")
            .OnDelete(DeleteBehavior.Restrict);

        // Stored by name: a movement read years from now must not depend on today's enum ordering.
        builder.Property(entry => entry.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(DepositEntry.KindNameLength)
            .IsRequired();

        builder.Property(entry => entry.Amount)
            .HasColumnName("amount")
            .HasPrecision(Money.Precision, Money.DecimalPlaces)
            .IsRequired();

        builder.Property(entry => entry.BalanceAfter)
            .HasColumnName("balance_after")
            .HasPrecision(Money.Precision, Money.DecimalPlaces)
            .IsRequired();

        builder.Property(entry => entry.Currency)
            .HasColumnName("currency")
            .HasMaxLength(DepositEntry.CurrencyLength)
            .IsRequired();

        builder.Property(entry => entry.IsInterestBearing).HasColumnName("is_interest_bearing");

        builder.Property(entry => entry.BillId).HasColumnName("bill_id");
        builder.Property(entry => entry.BillNumber).HasColumnName("bill_number").HasMaxLength(DepositEntry.BillNumberLength);

        // No foreign key on either: the bill lives in the billing schema and the service account is
        // named here only so a Finance posting can carry the subsidiary dimension. A constraint
        // across a module boundary is the coupling schema-per-module exists to prevent.
        builder.Property(entry => entry.ServiceAccountId).HasColumnName("service_account_id");

        builder.Property(entry => entry.Reason).HasColumnName("reason").HasMaxLength(DepositEntry.ReasonLength);
        builder.Property(entry => entry.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(entry => entry.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(entry => entry.RecordedAt).HasColumnName("recorded_at");

        // The ledger is always read one customer at a time — the 360's deposit tab, and the balance
        // check before money moves.
        builder.HasIndex(entry => entry.CustomerId).HasDatabaseName("ix_deposit_entries_customer_id");

        // "What settled this bill" is the other question asked of these rows, and it is the one a
        // billing dispute asks. Filtered to the applications, which are the only entries that
        // name a bill.
        builder.HasIndex(entry => entry.BillId)
            .HasDatabaseName("ix_deposit_entries_bill_id")
            .HasFilter("\"bill_id\" IS NOT NULL");
    }
}
