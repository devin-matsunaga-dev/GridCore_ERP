using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>Maps <see cref="ArrangementLimit"/> onto <c>customers.arrangement_limits</c> and seeds it.</summary>
public sealed class ArrangementLimitConfiguration : IEntityTypeConfiguration<ArrangementLimit>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ArrangementLimit> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("arrangement_limits");

        builder.HasKey(limit => limit.Id).HasName("pk_arrangement_limits");

        builder.Property(limit => limit.Id).HasColumnName("id").ValueGeneratedNever();

        // Stored by name, like every other enum in this schema: a limit read years from now must not
        // depend on today's numbering.
        builder.Property(limit => limit.CustomerClass)
            .HasColumnName("customer_class")
            .HasConversion<string>()
            .HasMaxLength(ArrangementLimit.ClassNameLength)
            .IsRequired();

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(limit => limit.MaximumBalance)
            .HasColumnName("maximum_balance")
            .HasPrecision(Money.Precision, Money.DecimalPlaces);

        builder.Property(limit => limit.Currency)
            .HasColumnName("currency")
            .HasMaxLength(ArrangementLimit.CurrencyLength)
            .IsRequired();

        builder.Property(limit => limit.MaximumInstalments).HasColumnName("maximum_instalments");

        builder.Property(limit => limit.Notes)
            .HasColumnName("notes")
            .HasMaxLength(ArrangementLimit.NotesLength)
            .IsRequired();

        // One ceiling per class. Two rows claiming one would make "may this rep sign it" depend on
        // which was read.
        builder.HasIndex(limit => limit.CustomerClass)
            .HasDatabaseName("ux_arrangement_limits_customer_class")
            .IsUnique();

        // Reference data ships with the schema, so a migrated database can work a counter in every
        // environment with no seeder involved (ARCHITECTURE.md invariant 8).
        //
        // The completeness check runs HERE, where the model is built, so a declared class with no
        // limit fails at startup rather than at the telephone.
        ArrangementLimits.RequireComplete(ArrangementLimits.All);

        builder.HasData(ArrangementLimits.All);
    }
}

/// <summary>Maps <see cref="PaymentArrangement"/> onto <c>customers.payment_arrangements</c>.</summary>
public sealed class PaymentArrangementConfiguration : IEntityTypeConfiguration<PaymentArrangement>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PaymentArrangement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("payment_arrangements");

        builder.HasKey(arrangement => arrangement.Id).HasName("pk_payment_arrangements");

        builder.Property(arrangement => arrangement.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(arrangement => arrangement.ArrangementNumber)
            .HasColumnName("arrangement_number")
            .HasMaxLength(PaymentArrangement.NumberLength)
            .IsRequired();

        builder.Property(arrangement => arrangement.ServiceAccountId).HasColumnName("service_account_id");

        builder.Property(arrangement => arrangement.AccountNumber)
            .HasColumnName("account_number")
            .HasMaxLength(PaymentArrangement.NumberLength)
            .IsRequired();

        builder.Property(arrangement => arrangement.CustomerId).HasColumnName("customer_id");

        builder.Property(arrangement => arrangement.CustomerName)
            .HasColumnName("customer_name")
            .HasMaxLength(PaymentArrangement.NameLength)
            .IsRequired();

        builder.Property(arrangement => arrangement.CustomerClass)
            .HasColumnName("customer_class")
            .HasConversion<string>()
            .HasMaxLength(PaymentArrangement.StatusNameLength)
            .IsRequired();

        builder.Property(arrangement => arrangement.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(PaymentArrangement.StatusNameLength)
            .IsRequired();

        builder.Property(arrangement => arrangement.ArrearsBalance)
            .HasColumnName("arrears_balance")
            .HasPrecision(Money.Precision, Money.DecimalPlaces);

        builder.Property(arrangement => arrangement.Currency)
            .HasColumnName("currency")
            .HasMaxLength(PaymentArrangement.CurrencyLength)
            .IsRequired();

        builder.Property(arrangement => arrangement.DownPayment)
            .HasColumnName("down_payment")
            .HasPrecision(Money.Precision, Money.DecimalPlaces);

        builder.Property(arrangement => arrangement.InstalmentCount).HasColumnName("instalment_count");
        builder.Property(arrangement => arrangement.IntervalDays).HasColumnName("interval_days");
        builder.Property(arrangement => arrangement.ArrangedOn).HasColumnName("arranged_on");

        builder.Property(arrangement => arrangement.LimitMaximumBalance)
            .HasColumnName("limit_maximum_balance")
            .HasPrecision(Money.Precision, Money.DecimalPlaces);

        builder.Property(arrangement => arrangement.LimitMaximumInstalments).HasColumnName("limit_maximum_instalments");
        builder.Property(arrangement => arrangement.RequiresApproval).HasColumnName("requires_approval");
        builder.Property(arrangement => arrangement.ApprovalRequestId).HasColumnName("approval_request_id");
        builder.Property(arrangement => arrangement.ActivatedOn).HasColumnName("activated_on");
        builder.Property(arrangement => arrangement.ClosedOn).HasColumnName("closed_on");

        builder.Property(arrangement => arrangement.Notes)
            .HasColumnName("notes")
            .HasMaxLength(PaymentArrangement.NotesLength);

        builder.Property(arrangement => arrangement.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(RegistryActor.MaxLength)
            .IsRequired();

        builder.Property(arrangement => arrangement.ActorName)
            .HasColumnName("actor_name")
            .HasMaxLength(RegistryActor.MaxLength);

        builder.Property(arrangement => arrangement.RecordedAt).HasColumnName("recorded_at");

        // All derived from the schedule and the status, every one of which is stored. Columns would
        // be further facts that could disagree with the rows they are computed from.
        builder.Ignore(arrangement => arrangement.ScheduledAmount);
        builder.Ignore(arrangement => arrangement.PaidAmount);
        builder.Ignore(arrangement => arrangement.OutstandingAmount);
        builder.Ignore(arrangement => arrangement.NextInstalment);

        builder.HasMany(arrangement => arrangement.Instalments)
            .WithOne()
            .HasForeignKey(instalment => instalment.PaymentArrangementId)
            .HasConstraintName("fk_arrangement_instalments_arrangement")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(arrangement => arrangement.Instalments)
            .HasField("_instalments")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // One arrangement per number, the rule every registry number in this schema follows.
        builder.HasIndex(arrangement => arrangement.ArrangementNumber)
            .HasDatabaseName("ux_payment_arrangements_number")
            .IsUnique();

        // The register read: what has been arranged on this account, newest first — and the query
        // the disconnection seam runs to find the one standing against it.
        builder.HasIndex(arrangement => new { arrangement.ServiceAccountId, arrangement.Status })
            .HasDatabaseName("ix_payment_arrangements_account_status");

        // No foreign key to arrangement_limits, the call every stamped reference id in this schema
        // makes: the arrangement has to keep saying what governed it if the published ceilings are
        // ever replaced. No foreign key to platform.approval_requests either — that is a different
        // context on a different schema, and the id is a pointer rather than a relation.
    }
}

/// <summary>Maps <see cref="ArrangementInstalment"/> onto <c>customers.arrangement_instalments</c>.</summary>
public sealed class ArrangementInstalmentConfiguration : IEntityTypeConfiguration<ArrangementInstalment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ArrangementInstalment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("arrangement_instalments");

        builder.HasKey(instalment => instalment.Id).HasName("pk_arrangement_instalments");

        builder.Property(instalment => instalment.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(instalment => instalment.PaymentArrangementId).HasColumnName("payment_arrangement_id");
        builder.Property(instalment => instalment.Sequence).HasColumnName("sequence");
        builder.Property(instalment => instalment.DueDate).HasColumnName("due_date");

        builder.Property(instalment => instalment.Amount)
            .HasColumnName("amount")
            .HasPrecision(Money.Precision, Money.DecimalPlaces);

        builder.Property(instalment => instalment.PaidAmount)
            .HasColumnName("paid_amount")
            .HasPrecision(Money.Precision, Money.DecimalPlaces);

        builder.Property(instalment => instalment.IsDownPayment).HasColumnName("is_down_payment");
        builder.Property(instalment => instalment.SettledAt).HasColumnName("settled_at");

        // Both derived from Amount and PaidAmount, both stored.
        builder.Ignore(instalment => instalment.Outstanding);
        builder.Ignore(instalment => instalment.IsSettled);

        // One line per position in one schedule. Two instalments at sequence 2 would make "the
        // earliest unpaid instalment" depend on the order the rows came back in.
        builder.HasIndex(instalment => new { instalment.PaymentArrangementId, instalment.Sequence })
            .HasDatabaseName("ux_arrangement_instalments_arrangement_sequence")
            .IsUnique();

        // The review run's read: every instalment falling due on or before a day, across the
        // register.
        builder.HasIndex(instalment => instalment.DueDate)
            .HasDatabaseName("ix_arrangement_instalments_due_date");
    }
}
