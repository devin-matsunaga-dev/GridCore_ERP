using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Finance.Features.Journal;

/// <summary>Maps <see cref="JournalLine"/> onto <c>finance.journal_lines</c>.</summary>
public sealed class JournalLineConfiguration : IEntityTypeConfiguration<JournalLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<JournalLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("journal_lines");

        builder.HasKey(line => line.Id).HasName("pk_journal_lines");

        // Never store-generated — the same trap BillLineConfiguration documents: a line appended to
        // a tracked entry's collection is decided to be an insert or an update by whether its key is
        // set on a store-generated column.
        builder.Property(line => line.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(line => line.JournalEntryId).HasColumnName("journal_entry_id");
        builder.Property(line => line.Sequence).HasColumnName("sequence");
        builder.Property(line => line.AccountId).HasColumnName("account_id");

        builder.Property(line => line.Debit)
            .HasColumnName("debit")
            .HasPrecision(JournalEntry.MoneyPrecision, JournalEntry.MoneyScale);

        builder.Property(line => line.Credit)
            .HasColumnName("credit")
            .HasPrecision(JournalEntry.MoneyPrecision, JournalEntry.MoneyScale);

        // Derived from what is stored, so neither is a column that could disagree with the amounts
        // it describes.
        builder.Ignore(line => line.Amount);
        builder.Ignore(line => line.IsDebit);

        // A REAL foreign key, unlike every cross-module id in this codebase — the chart lives in
        // this schema. It is what makes "you cannot post to an account that does not exist" a fact
        // the database enforces rather than a check the aggregate happens to run. Restrict, not
        // cascade: deleting an account out from under a posted ledger line is not a thing that
        // should be made easy, and accounts are only ever added by migration anyway.
        builder.HasOne(line => line.Account)
            .WithMany()
            .HasForeignKey(line => line.AccountId)
            .HasConstraintName("fk_journal_lines_account")
            .OnDelete(DeleteBehavior.Restrict);

        // An entry cannot have two line 2s — the order the lines are posted in is the entry.
        builder.HasIndex(line => new { line.JournalEntryId, line.Sequence })
            .HasDatabaseName("ux_journal_lines_sequence")
            .IsUnique();

        // The trial balance and the AR view both read the ledger by account.
        builder.HasIndex(line => line.AccountId).HasDatabaseName("ix_journal_lines_account");
    }
}
