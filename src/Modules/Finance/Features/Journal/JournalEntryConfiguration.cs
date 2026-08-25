using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Finance.Features.Journal;

/// <summary>Maps <see cref="JournalEntry"/> onto <c>finance.journal_entries</c>.</summary>
public sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("journal_entries");

        builder.HasKey(entry => entry.Id).HasName("pk_journal_entries");

        // Never store-generated — WP-1.2's lesson: with the Guid default, a freshly appended line in
        // the Lines collection is tracked as Modified and the save throws having updated nothing.
        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(entry => entry.EntryNumber)
            .HasColumnName("entry_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(entry => entry.EventId).HasColumnName("event_id");

        builder.Property(entry => entry.Source)
            .HasColumnName("source")
            .HasMaxLength(JournalEntry.SourceLength)
            .IsRequired();

        builder.Property(entry => entry.Reference)
            .HasColumnName("reference")
            .HasMaxLength(JournalEntry.ReferenceLength)
            .IsRequired();

        builder.Property(entry => entry.Description)
            .HasColumnName("description")
            .HasMaxLength(JournalEntry.DescriptionLength)
            .IsRequired();

        builder.Property(entry => entry.Currency)
            .HasColumnName("currency")
            .HasMaxLength(JournalEntry.CurrencyLength)
            .IsRequired();

        builder.Property(entry => entry.PostedOn).HasColumnName("posted_on");
        builder.Property(entry => entry.OccurredAt).HasColumnName("occurred_at");
        builder.Property(entry => entry.PostedAt).HasColumnName("posted_at");

        // No foreign keys: the service account and the customer live in the Customers schema, which
        // this module has never heard of. The ids arrive on the event, from the module that owns
        // them, and are here so an AR view can say who owes the money.
        builder.Property(entry => entry.ServiceAccountId).HasColumnName("service_account_id");
        builder.Property(entry => entry.CustomerId).HasColumnName("customer_id");

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(entry => entry.TotalDebits)
            .HasColumnName("total_debits")
            .HasPrecision(JournalEntry.MoneyPrecision, JournalEntry.MoneyScale);

        builder.Property(entry => entry.TotalCredits)
            .HasColumnName("total_credits")
            .HasPrecision(JournalEntry.MoneyPrecision, JournalEntry.MoneyScale);

        builder.Property(entry => entry.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(RegistryActor.MaxLength)
            .IsRequired();

        builder.Property(entry => entry.ActorName)
            .HasColumnName("actor_name")
            .HasMaxLength(RegistryActor.MaxLength);

        // Derived from what is stored. Mapped, EF would want a backing field it cannot find and the
        // model would fail to build at startup rather than in a test.
        builder.Ignore(entry => entry.IsBalanced);

        // Lines ARE a navigation. An entry has two or three of them, no path loads an entry without
        // them, and an entry without its lines is not an entry — the same call BillConfiguration
        // made about bill lines.
        builder.HasMany(entry => entry.Lines)
            .WithOne()
            .HasForeignKey(line => line.JournalEntryId)
            .HasConstraintName("fk_journal_lines_entry")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(JournalEntry.Lines))!.SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(entry => entry.EntryNumber).HasDatabaseName("ux_journal_entries_number").IsUnique();

        // ONE ENTRY PER EVENT, as a database fact.
        //
        // The dedupe claim in platform.processed_messages already stops a redelivery reaching the
        // ledger, and it commits in the same transaction as the entry — so this index should never
        // fire. It is here because "should never" is doing a lot of work in a sentence about money:
        // a consumer renamed, a claim table restored from an older backup, a second consumer added
        // for the same event, and the ledger would double every posting silently.
        //
        // Unfiltered, and for the reason ux_bills_account_cycle is (WP-2.3): an entry raised by hand
        // holds NULL in event_id, and NULLs in a unique index are distinct on both Postgres and the
        // fast tier's SQLite. So manual entries stay possible without a predicate naming a column
        // for a later rename to desynchronise.
        builder.HasIndex(entry => entry.EventId).HasDatabaseName("ux_journal_entries_event").IsUnique();

        builder.HasIndex(entry => entry.PostedOn).HasDatabaseName("ix_journal_entries_posted_on");
        builder.HasIndex(entry => entry.Source).HasDatabaseName("ix_journal_entries_source");

        // The subsidiary ledger's index: "what does this account owe" reads AR lines through their
        // entry, and without this it reads the whole ledger to find them.
        builder.HasIndex(entry => entry.ServiceAccountId).HasDatabaseName("ix_journal_entries_service_account");
        builder.HasIndex(entry => entry.CustomerId).HasDatabaseName("ix_journal_entries_customer");
    }
}
