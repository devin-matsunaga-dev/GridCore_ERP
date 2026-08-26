using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>Maps <see cref="ServiceApplication"/> onto <c>customers.service_applications</c>.</summary>
public sealed class ServiceApplicationConfiguration : IEntityTypeConfiguration<ServiceApplication>
{
    /// <summary>
    /// The filter that limits the one-open-application-per-supply index to applications still on
    /// the desk. Written against the stored enum <i>names</i>, in SQL both Postgres and the fast
    /// tier's SQLite parse identically — the call <c>ServiceAccountConfiguration</c> already made.
    /// </summary>
    public const string OpenApplicationFilter = "\"status\" IN ('Submitted', 'UnderReview')";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ServiceApplication> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("service_applications");

        builder.HasKey(application => application.Id).HasName("pk_service_applications");

        // Never store-generated: every id in GridCore is a Guid v7 minted in code from the clock,
        // and saying so is load-bearing for the same reason it is on ServiceAccount — EF decides
        // whether an untracked child of a tracked parent is an insert or an update by asking whether
        // its key is store-generated, and a freshly attached document would otherwise be tracked as
        // Modified and update nothing.
        builder.Property(application => application.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(application => application.ApplicationNumber)
            .HasColumnName("application_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(application => application.CustomerId).HasColumnName("customer_id");
        builder.Property(application => application.ServiceLocationId).HasColumnName("service_location_id");

        // Foreign keys without navigations, the shape every other table in this schema uses: the
        // database guarantees an application never points at a customer or a premise that is not
        // there, while a navigation would invite a queue query to walk into the customer. Restrict,
        // not cascade — an application register that disappears with its subject is not a register.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(application => application.CustomerId)
            .HasConstraintName("fk_service_applications_customer")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceLocation>()
            .WithMany()
            .HasForeignKey(application => application.ServiceLocationId)
            .HasConstraintName("fk_service_applications_location")
            .OnDelete(DeleteBehavior.Restrict);

        // Stored by name: a record read years from now must not depend on today's enum ordering.
        builder.Property(application => application.ServiceType)
            .HasColumnName("service_type")
            .HasConversion<string>()
            .HasMaxLength(ServiceApplication.EnumNameLength)
            .IsRequired();

        builder.Property(application => application.Type)
            .HasColumnName("application_type")
            .HasConversion<string>()
            .HasMaxLength(ServiceApplication.EnumNameLength)
            .IsRequired();

        builder.Property(application => application.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(ServiceApplication.EnumNameLength)
            .IsRequired();

        builder.Property(application => application.DecisionReasonCode)
            .HasColumnName("decision_reason_code")
            .HasConversion<string>()
            .HasMaxLength(ServiceApplication.EnumNameLength);

        builder.Property(application => application.RequestedOn).HasColumnName("requested_on");
        builder.Property(application => application.Notes).HasColumnName("notes").HasMaxLength(ServiceApplication.NotesLength);
        builder.Property(application => application.DecisionNotes).HasColumnName("decision_notes").HasMaxLength(ServiceApplication.NotesLength);

        builder.Property(application => application.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(application => application.SubmittedById).HasColumnName("submitted_by_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(application => application.SubmittedByName).HasColumnName("submitted_by_name").HasMaxLength(RegistryActor.MaxLength);

        builder.Property(application => application.ReviewStartedAt).HasColumnName("review_started_at");
        builder.Property(application => application.ReviewerId).HasColumnName("reviewer_id").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(application => application.ReviewerName).HasColumnName("reviewer_name").HasMaxLength(RegistryActor.MaxLength);

        builder.Property(application => application.DecidedAt).HasColumnName("decided_at");
        builder.Property(application => application.DecidedById).HasColumnName("decided_by_id").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(application => application.DecidedByName).HasColumnName("decided_by_name").HasMaxLength(RegistryActor.MaxLength);

        // No foreign key on either id, deliberately, though both point into this same schema. The
        // account column is nullable and written by the same transaction that inserts the account,
        // so a constraint would police a state the code cannot reach; the replaced application is a
        // provenance link the service already checks belongs to the same customer, which is a rule a
        // foreign key cannot express. The call AccountTransition makes about its own two columns.
        builder.Property(application => application.ServiceAccountId).HasColumnName("service_account_id");
        builder.Property(application => application.ReplacesApplicationId).HasColumnName("replaces_application_id");

        // Computed from Documents, not stored. EF discovers a collection of a non-primitive type as
        // a navigation whether or not it has a setter, so the checklist would otherwise become a
        // table of its own — a second, persisted copy of an answer derived from rows that are
        // already there, and therefore a second place for it to be wrong. The whole point of
        // ServiceApplication.Checklist is that it cannot fall out of step.
        builder.Ignore(application => application.Checklist);

        // Append-only, owned by the application and loaded through it. Backing field rather than the
        // property: Documents is IReadOnlyList so nothing outside the aggregate can attach one.
        builder.HasMany(application => application.Documents)
            .WithOne()
            .HasForeignKey(document => document.ServiceApplicationId)
            .HasConstraintName("fk_application_documents_application")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(ServiceApplication.Documents))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(application => application.ApplicationNumber)
            .HasDatabaseName("ux_service_applications_number")
            .IsUnique();

        // The 360's applications tab, and the guard that refuses a second application for a supply
        // somebody is already waiting on.
        builder.HasIndex(application => application.CustomerId).HasDatabaseName("ix_service_applications_customer_id");

        // The review queue is filtered by status and nothing else, which is the one screen this
        // table exists for.
        builder.HasIndex(application => application.Status).HasDatabaseName("ix_service_applications_status");

        // One OPEN application per premise per supply, as a database fact. The service checks first
        // so the loser of a race gets a 409 naming the collision, but this index is what makes two
        // reps taking the same telephone call impossible rather than merely unlikely. A decided
        // application is excluded, which is what lets a rejected one be replaced by a fresh
        // submission — the whole point of ReplacesApplicationId.
        builder.HasIndex(application => new { application.ServiceLocationId, application.ServiceType })
            .HasDatabaseName("ux_service_applications_open_premise")
            .IsUnique()
            .HasFilter(OpenApplicationFilter);
    }
}

/// <summary>Maps <see cref="ApplicationDocument"/> onto <c>customers.application_documents</c>.</summary>
public sealed class ApplicationDocumentConfiguration : IEntityTypeConfiguration<ApplicationDocument>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApplicationDocument> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("application_documents");

        builder.HasKey(document => document.Id).HasName("pk_application_documents");

        builder.Property(document => document.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(document => document.ServiceApplicationId).HasColumnName("service_application_id");

        builder.Property(document => document.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(ApplicationDocument.EnumNameLength)
            .IsRequired();

        builder.Property(document => document.FileName).HasColumnName("file_name").HasMaxLength(ApplicationDocument.FileNameLength).IsRequired();
        builder.Property(document => document.ContentType).HasColumnName("content_type").HasMaxLength(ApplicationDocument.ContentTypeLength).IsRequired();
        builder.Property(document => document.SizeInBytes).HasColumnName("size_in_bytes");
        builder.Property(document => document.Checksum).HasColumnName("checksum").HasMaxLength(ApplicationDocument.ChecksumLength).IsRequired();

        // Unique: two rows filed under one key would be two rows describing one object, and the
        // second upload would have silently overwritten the first one's bytes while leaving a record
        // claiming otherwise. The key carries the document's own id, so this can only fire on a bug.
        builder.Property(document => document.StorageKey).HasColumnName("storage_key").HasMaxLength(ApplicationDocument.StorageKeyLength).IsRequired();
        builder.HasIndex(document => document.StorageKey).HasDatabaseName("ux_application_documents_storage_key").IsUnique();

        builder.Property(document => document.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(document => document.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(document => document.UploadedAt).HasColumnName("uploaded_at");

        builder.HasIndex(document => document.ServiceApplicationId).HasDatabaseName("ix_application_documents_application_id");
    }
}
