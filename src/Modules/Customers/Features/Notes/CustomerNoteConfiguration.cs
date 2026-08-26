using GridCore.Modules.Customers.Features.Customers;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Notes;

/// <summary>Maps <see cref="CustomerNote"/> onto <c>customers.customer_notes</c>.</summary>
public sealed class CustomerNoteConfiguration : IEntityTypeConfiguration<CustomerNote>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CustomerNote> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customer_notes");

        builder.HasKey(note => note.Id).HasName("pk_customer_notes");

        // Never store-generated: every id in GridCore is a Guid v7 minted in code from the clock.
        builder.Property(note => note.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(note => note.CustomerId).HasColumnName("customer_id");

        // A foreign key without a navigation, the shape ServiceAccount and DepositEntry already use:
        // the database guarantees a note never points at a customer who is not there, while a
        // navigation would invite a query to walk into the customer and turn the log into a join it
        // does not need. Restrict, not cascade — a service record that disappears with its subject
        // is not a service record.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(note => note.CustomerId)
            .HasConstraintName("fk_customer_notes_customer")
            .OnDelete(DeleteBehavior.Restrict);

        // No foreign key to the service account, deliberately, though it is in this same schema: the
        // column is nullable and the service checks the account belongs to the customer, which is a
        // rule a foreign key cannot express. A constraint here would catch only the half already
        // caught, and would make a note undeletable-with its account for no gain.
        builder.Property(note => note.ServiceAccountId).HasColumnName("service_account_id");

        // Stored by name: a note read years from now must not depend on today's enum ordering.
        builder.Property(note => note.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(CustomerNote.KindNameLength)
            .IsRequired();

        builder.Property(note => note.Body)
            .HasColumnName("body")
            .HasMaxLength(CustomerNote.BodyLength)
            .IsRequired();

        builder.Property(note => note.FollowUpOn).HasColumnName("follow_up_on");

        builder.Property(note => note.LinkKind)
            .HasColumnName("link_kind")
            .HasConversion<string>()
            .HasMaxLength(CustomerNote.KindNameLength);

        // No foreign key on either: a bill lives in the billing schema, a payment in the payments
        // schema and a work order in a schema WP-3.1 has yet to build. A constraint across a module
        // boundary is the coupling schema-per-module exists to prevent — the same call
        // DepositEntry's bill columns make.
        builder.Property(note => note.LinkedEntityId).HasColumnName("linked_entity_id");
        builder.Property(note => note.LinkedReference).HasColumnName("linked_reference").HasMaxLength(CustomerNote.ReferenceLength);

        // A self-reference, and Restrict rather than Cascade: deleting the corrected note would take
        // the correction with it, which is precisely the history the register exists to keep. Nothing
        // deletes a note today; the constraint is what makes that stay true by accident as well as
        // on purpose.
        builder.HasOne<CustomerNote>()
            .WithMany()
            .HasForeignKey(note => note.CorrectsNoteId)
            .HasConstraintName("fk_customer_notes_corrects")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(note => note.CorrectsNoteId).HasColumnName("corrects_note_id");
        builder.Property(note => note.IsPinned).HasColumnName("is_pinned");
        builder.Property(note => note.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(note => note.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(note => note.RecordedAt).HasColumnName("recorded_at");

        // Named explicitly rather than left to the self foreign key's automatic index, which would
        // ship as `IX_customer_notes_corrects_note_id` in a schema where every other index is
        // lower_snake_case. It earns its place besides: "which note supersedes this one" is the
        // question the browser answers by grouping corrections against their originals.
        builder.HasIndex(note => note.CorrectsNoteId)
            .HasDatabaseName("ix_customer_notes_corrects_note_id")
            .HasFilter("\"corrects_note_id\" IS NOT NULL");

        // The log is always read one customer at a time — the 360's notes tab, the pinned strip on
        // its summary, and the timeline's fifth source.
        builder.HasIndex(note => note.CustomerId).HasDatabaseName("ix_customer_notes_customer_id");

        // "What has been logged against this bill / payment / work order" is the other question asked
        // of these rows, and it is the one a billing dispute asks. Filtered to the notes that name
        // something, which are the only ones that could answer it.
        builder.HasIndex(note => new { note.LinkKind, note.LinkedEntityId })
            .HasDatabaseName("ix_customer_notes_link")
            .HasFilter("\"linked_entity_id\" IS NOT NULL");

        // Pinned notes are pulled out on their own for the 360's summary, and there are a handful of
        // them against a log that grows for years — which is exactly the shape a partial index is
        // for. Declared with the NAMED overload: EF keys an index by its property list, so a second
        // plain HasIndex(note => note.CustomerId) would rename the one above rather than add this
        // one, and the filtered index would silently never exist.
        builder.HasIndex([nameof(CustomerNote.CustomerId)], "ix_customer_notes_pinned")
            .HasDatabaseName("ix_customer_notes_pinned")
            .HasFilter("\"is_pinned\"");
    }
}
