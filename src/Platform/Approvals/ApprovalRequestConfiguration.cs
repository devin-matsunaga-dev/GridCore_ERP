using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Platform.Approvals;

/// <summary>Maps <see cref="ApprovalRequest"/> onto <c>platform.approval_requests</c>.</summary>
public sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("approval_requests");

        builder.HasKey(request => request.Id).HasName("pk_approval_requests");

        builder.Property(request => request.Id).HasColumnName("id");
        builder.Property(request => request.RequestType).HasColumnName("request_type").HasMaxLength(ApprovalRequest.NameLength).IsRequired();
        builder.Property(request => request.SubjectType).HasColumnName("subject_type").HasMaxLength(ApprovalRequest.NameLength).IsRequired();
        builder.Property(request => request.SubjectId).HasColumnName("subject_id").HasMaxLength(ApprovalRequest.NameLength).IsRequired();
        builder.Property(request => request.RequiredPermission).HasColumnName("required_permission").HasMaxLength(ApprovalRequest.NameLength).IsRequired();
        builder.Property(request => request.PayloadJson).HasColumnName("payload_json").HasColumnType(PlatformDbContext.JsonColumnType);
        builder.Property(request => request.Reason).HasColumnName("reason").HasMaxLength(ApprovalRequest.NoteLength);
        builder.Property(request => request.RequestedByUserId).HasColumnName("requested_by_user_id").HasMaxLength(ApprovalRequest.NameLength).IsRequired();
        builder.Property(request => request.RequestedByUserName).HasColumnName("requested_by_user_name").HasMaxLength(ApprovalRequest.NameLength);
        builder.Property(request => request.RequestedAt).HasColumnName("requested_at").IsRequired();
        builder.Property(request => request.DecidedByUserId).HasColumnName("decided_by_user_id").HasMaxLength(ApprovalRequest.NameLength);
        builder.Property(request => request.DecidedByUserName).HasColumnName("decided_by_user_name").HasMaxLength(ApprovalRequest.NameLength);
        builder.Property(request => request.DecidedAt).HasColumnName("decided_at");
        builder.Property(request => request.DecisionNote).HasColumnName("decision_note").HasMaxLength(ApprovalRequest.NoteLength);

        // Stored by name: the queue is read in SQL during support work, and a number there would
        // mean nothing. Renumbering the enum then stays a non-event.
        builder.Property(request => request.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();

        builder.Ignore(request => request.AllowedTransitions);

        // The pending queue is the hot read; the subject index backs "is there an open request for this?".
        builder.HasIndex(request => request.Status).HasDatabaseName("ix_approval_requests_status");
        builder.HasIndex(request => new { request.SubjectType, request.SubjectId }).HasDatabaseName("ix_approval_requests_subject");
        builder.HasIndex(request => request.RequestedByUserId).HasDatabaseName("ix_approval_requests_requested_by");
    }
}
