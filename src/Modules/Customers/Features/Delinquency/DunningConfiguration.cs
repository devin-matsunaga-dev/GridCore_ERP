using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Delinquency;

/// <summary>Maps <see cref="DunningStep"/> onto <c>customers.dunning_steps</c> and seeds it.</summary>
public sealed class DunningStepConfiguration : IEntityTypeConfiguration<DunningStep>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DunningStep> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("dunning_steps");

        builder.HasKey(step => step.Id).HasName("pk_dunning_steps");

        builder.Property(step => step.Id).HasColumnName("id").ValueGeneratedNever();

        // Stored by name, like every other enum in this schema: a notice served years from now must
        // not depend on today's numbering.
        builder.Property(step => step.NoticeType)
            .HasColumnName("notice_type")
            .HasConversion<string>()
            .HasMaxLength(DunningStep.TypeNameLength)
            .IsRequired();

        builder.Property(step => step.Sequence).HasColumnName("sequence");
        builder.Property(step => step.DaysPastDue).HasColumnName("days_past_due");
        builder.Property(step => step.WaitingPeriodDays).HasColumnName("waiting_period_days");

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(step => step.MinimumArrears)
            .HasColumnName("minimum_arrears")
            .HasPrecision(Money.Precision, Money.DecimalPlaces);

        builder.Property(step => step.Currency)
            .HasColumnName("currency")
            .HasMaxLength(DunningStep.CurrencyLength)
            .IsRequired();

        builder.Property(step => step.Name)
            .HasColumnName("name")
            .HasMaxLength(DunningStep.NameLength)
            .IsRequired();

        builder.Property(step => step.Message)
            .HasColumnName("message")
            .HasMaxLength(DunningStep.MessageLength)
            .IsRequired();

        builder.Ignore(step => step.HasWaitingPeriod);

        // One step per notice, and one notice per position in the sequence. Two rows claiming either
        // is a sequence with no answer to "what comes next".
        builder.HasIndex(step => step.NoticeType).HasDatabaseName("ux_dunning_steps_notice_type").IsUnique();
        builder.HasIndex(step => step.Sequence).HasDatabaseName("ux_dunning_steps_sequence").IsUnique();

        // Reference data ships with the schema: a migrated database can work a delinquency queue in
        // every environment, with no seeder involved (ARCHITECTURE.md invariant 8).
        //
        // The completeness check runs HERE, where the model is built, so a declared notice with no
        // step fails at startup rather than at the desk.
        DunningSequence.RequireComplete(DunningSequence.All);

        builder.HasData(DunningSequence.All);
    }
}

/// <summary>Maps <see cref="DunningNotice"/> onto <c>customers.dunning_notices</c>.</summary>
public sealed class DunningNoticeConfiguration : IEntityTypeConfiguration<DunningNotice>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DunningNotice> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("dunning_notices");

        builder.HasKey(notice => notice.Id).HasName("pk_dunning_notices");

        builder.Property(notice => notice.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(notice => notice.ServiceAccountId).HasColumnName("service_account_id");

        builder.Property(notice => notice.AccountNumber)
            .HasColumnName("account_number")
            .HasMaxLength(DunningNotice.NumberLength)
            .IsRequired();

        builder.Property(notice => notice.CustomerId).HasColumnName("customer_id");

        builder.Property(notice => notice.CustomerName)
            .HasColumnName("customer_name")
            .HasMaxLength(DunningNotice.NameLength)
            .IsRequired();

        builder.Property(notice => notice.NoticeType)
            .HasColumnName("notice_type")
            .HasConversion<string>()
            .HasMaxLength(DunningNotice.TypeNameLength)
            .IsRequired();

        builder.Property(notice => notice.ServedOn).HasColumnName("served_on");

        builder.Property(notice => notice.ArrearsAmount)
            .HasColumnName("arrears_amount")
            .HasPrecision(Money.Precision, Money.DecimalPlaces);

        builder.Property(notice => notice.Currency)
            .HasColumnName("currency")
            .HasMaxLength(DunningNotice.CurrencyLength)
            .IsRequired();

        builder.Property(notice => notice.DaysPastDue).HasColumnName("days_past_due");
        builder.Property(notice => notice.DunningStepId).HasColumnName("dunning_step_id");
        builder.Property(notice => notice.WaitingPeriodDays).HasColumnName("waiting_period_days");

        builder.Property(notice => notice.Notes)
            .HasColumnName("notes")
            .HasMaxLength(DunningNotice.NotesLength);

        builder.Property(notice => notice.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(RegistryActor.MaxLength)
            .IsRequired();

        builder.Property(notice => notice.ActorName)
            .HasColumnName("actor_name")
            .HasMaxLength(RegistryActor.MaxLength);

        builder.Property(notice => notice.RecordedAt).HasColumnName("recorded_at");

        // Derived from ServedOn and WaitingPeriodDays, both stored. A column would be a third fact
        // that could disagree with the two it is computed from.
        builder.Ignore(notice => notice.EffectiveFrom);

        // The register read: what has been served over this account, newest first — and the query
        // the eligibility test runs to find the most recent disconnection notice.
        builder.HasIndex(notice => new { notice.ServiceAccountId, notice.NoticeType, notice.ServedOn })
            .HasDatabaseName("ix_dunning_notices_account_type_served");

        // No foreign key to dunning_steps, the call every stamped reference id in GridCore makes: the
        // notice has to keep saying what it said if the published sequence is ever replaced.
    }
}
