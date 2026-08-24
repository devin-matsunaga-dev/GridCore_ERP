using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.ServiceAccounts;

/// <summary>Maps <see cref="ServiceAccount"/> onto <c>customers.service_accounts</c>.</summary>
public sealed class ServiceAccountConfiguration : IEntityTypeConfiguration<ServiceAccount>
{
    /// <summary>
    /// The filtered unique index that stops a premise being double-booked. Written against the
    /// stored column and the stored enum <i>name</i>, in SQL both Postgres and the fast tier's
    /// SQLite parse identically — the same reason the number generator uses ORDER BY rather than a
    /// provider-specific MAX.
    /// </summary>
    public const string OnePremiseFilter = "\"status\" <> 'Closed'";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ServiceAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("service_accounts");

        builder.HasKey(account => account.Id).HasName("pk_service_accounts");

        // Never store-generated: every id in GridCore is a Guid v7 minted in code from the clock,
        // and saying so is load-bearing here rather than cosmetic. EF decides whether an untracked
        // child of a tracked parent is an insert or an update by asking whether its key is set on a
        // store-generated column — leave the default and a freshly appended history line is tracked
        // as Modified, and the save fails having updated nothing.
        builder.Property(account => account.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(account => account.AccountNumber)
            .HasColumnName("account_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(account => account.CustomerId).HasColumnName("customer_id");
        builder.Property(account => account.ServiceLocationId).HasColumnName("service_location_id");

        // Foreign keys without navigations. Both registries are this module's own, so the database
        // can guarantee an account never points at a customer or a premise that is not there — but
        // a navigation property would invite a query to walk into the customer and turn every
        // account list into a join it does not need. Restrict, not cascade: nothing deletes from
        // this registry (WP-1.1), and if something ever tried, taking the accounts with it is the
        // last thing anyone would want.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(account => account.CustomerId)
            .HasConstraintName("fk_service_accounts_customer")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceLocation>()
            .WithMany()
            .HasForeignKey(account => account.ServiceLocationId)
            .HasConstraintName("fk_service_accounts_location")
            .OnDelete(DeleteBehavior.Restrict);

        // Stored by name: a record read years from now must not depend on today's enum ordering.
        builder.Property(account => account.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(ServiceAccount.EnumNameLength)
            .IsRequired();

        builder.Property(account => account.OpenedAt).HasColumnName("opened_at");
        builder.Property(account => account.ServiceStartedAt).HasColumnName("service_started_at");
        builder.Property(account => account.ServiceEndedAt).HasColumnName("service_ended_at");
        builder.Property(account => account.StatusChangedAt).HasColumnName("status_changed_at");
        builder.Property(account => account.StatusReason).HasColumnName("status_reason").HasMaxLength(ServiceAccount.ReasonLength);

        // Append-only, owned by the account and loaded through it. Backing field rather than the
        // property: History is IReadOnlyList so nothing outside the aggregate can add a line.
        builder.HasMany(account => account.History)
            .WithOne()
            .HasForeignKey(entry => entry.ServiceAccountId)
            .HasConstraintName("fk_service_account_history_account")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(ServiceAccount.History))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(account => account.AccountNumber).HasDatabaseName("ux_service_accounts_account_number").IsUnique();

        // The customer 360 page (WP-1.5) reads every account a customer holds; the premise index
        // answers "who is served here" and backs the uniqueness check below.
        builder.HasIndex(account => account.CustomerId).HasDatabaseName("ix_service_accounts_customer_id");
        builder.HasIndex(account => account.Status).HasDatabaseName("ix_service_accounts_status");

        // One open account per premise, as a database fact. The service checks first so the loser
        // of a race gets a 409 it can act on, but this index is what makes two accounts billing the
        // same meter impossible rather than merely unlikely. Closed accounts are excluded, which is
        // what lets the next occupant be given one.
        builder.HasIndex(account => account.ServiceLocationId)
            .HasDatabaseName("ux_service_accounts_open_location")
            .IsUnique()
            .HasFilter(OnePremiseFilter);
    }
}

/// <summary>Maps <see cref="ServiceAccountHistoryEntry"/> onto <c>customers.service_account_history</c>.</summary>
public sealed class ServiceAccountHistoryEntryConfiguration : IEntityTypeConfiguration<ServiceAccountHistoryEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ServiceAccountHistoryEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("service_account_history");

        builder.HasKey(entry => entry.Id).HasName("pk_service_account_history");

        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entry => entry.ServiceAccountId).HasColumnName("service_account_id");

        builder.Property(entry => entry.FromStatus)
            .HasColumnName("from_status")
            .HasConversion<string>()
            .HasMaxLength(ServiceAccount.EnumNameLength);

        builder.Property(entry => entry.ToStatus)
            .HasColumnName("to_status")
            .HasConversion<string>()
            .HasMaxLength(ServiceAccount.EnumNameLength)
            .IsRequired();

        builder.Property(entry => entry.Reason).HasColumnName("reason").HasMaxLength(ServiceAccountHistoryEntry.ReasonLength);
        builder.Property(entry => entry.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(entry => entry.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(entry => entry.RecordedAt).HasColumnName("recorded_at");

        builder.HasIndex(entry => entry.ServiceAccountId).HasDatabaseName("ix_service_account_history_account_id");
    }
}
