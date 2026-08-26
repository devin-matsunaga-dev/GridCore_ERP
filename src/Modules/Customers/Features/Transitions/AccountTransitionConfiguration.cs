using GridCore.Modules.Customers.Features.Customers;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Transitions;

/// <summary>Maps <see cref="AccountTransition"/> onto <c>customers.account_transitions</c>.</summary>
public sealed class AccountTransitionConfiguration : IEntityTypeConfiguration<AccountTransition>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AccountTransition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("account_transitions");

        builder.HasKey(transition => transition.Id).HasName("pk_account_transitions");

        // Never store-generated: every id in GridCore is a Guid v7 minted in code from the clock.
        builder.Property(transition => transition.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(transition => transition.CustomerId).HasColumnName("customer_id");

        // A foreign key without a navigation, the shape DepositEntry and CustomerNote already use:
        // the database guarantees a transition never points at a customer who is not there, while a
        // navigation would invite a query to walk into the customer. Restrict, not cascade — a
        // service history that disappears with its subject is not a history.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(transition => transition.CustomerId)
            .HasConstraintName("fk_account_transitions_customer")
            .OnDelete(DeleteBehavior.Restrict);

        // Stored by name: a transition read years from now must not depend on today's enum ordering.
        builder.Property(transition => transition.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(AccountTransition.EnumNameLength)
            .IsRequired();

        builder.Property(transition => transition.ReasonCode)
            .HasColumnName("reason_code")
            .HasConversion<string>()
            .HasMaxLength(AccountTransition.EnumNameLength)
            .IsRequired();

        builder.Property(transition => transition.Notes).HasColumnName("notes").HasMaxLength(AccountTransition.NotesLength);

        builder.Property(transition => transition.EffectiveOn).HasColumnName("effective_on");

        builder.Property(transition => transition.FromValue).HasColumnName("from_value").HasMaxLength(AccountTransition.ValueLength);
        builder.Property(transition => transition.ToValue).HasColumnName("to_value").HasMaxLength(AccountTransition.ValueLength);

        // No foreign key on either account column, deliberately, though both live in this same
        // schema: the columns are nullable and the service checks the account belongs to the
        // customer, which is a rule a foreign key cannot express. The same call CustomerNote makes
        // about its own service_account_id.
        builder.Property(transition => transition.FromServiceAccountId).HasColumnName("from_service_account_id");
        builder.Property(transition => transition.ToServiceAccountId).HasColumnName("to_service_account_id");

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(transition => transition.DepositCarried)
            .HasColumnName("deposit_carried")
            .HasPrecision(Money.Precision, Money.DecimalPlaces)
            .IsRequired();

        builder.Property(transition => transition.Currency).HasColumnName("currency").HasMaxLength(AccountTransition.CurrencyLength);

        // No foreign key to the ledger entry either. It is nullable — a transfer of a customer
        // holding nothing writes no entry — and the pairing is made in one transaction, so a
        // constraint would police a state the code cannot reach.
        builder.Property(transition => transition.DepositEntryId).HasColumnName("deposit_entry_id");

        builder.Property(transition => transition.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(transition => transition.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(transition => transition.RecordedAt).HasColumnName("recorded_at");

        // The register is always read one customer at a time — the 360's transitions tab, and the
        // effective-date guard that asks what has already been recorded.
        builder.HasIndex(transition => transition.CustomerId).HasDatabaseName("ix_account_transitions_customer_id");

        // "What happened to this account" is the other question asked of these rows, and it is asked
        // of BOTH columns: an account appears as the one released on a move-out or a transfer, and as
        // the one opened on a move-in or the other half of that same transfer. Two filtered indexes
        // rather than one, because no single index can answer a predicate over two columns OR'd
        // together. Declared with the NAMED overload: EF keys an index by its property list, so a
        // second plain HasIndex over the same column would rename the first rather than add a second.
        builder.HasIndex(transition => transition.FromServiceAccountId)
            .HasDatabaseName("ix_account_transitions_from_account")
            .HasFilter("\"from_service_account_id\" IS NOT NULL");

        builder.HasIndex(transition => transition.ToServiceAccountId)
            .HasDatabaseName("ix_account_transitions_to_account")
            .HasFilter("\"to_service_account_id\" IS NOT NULL");
    }
}
