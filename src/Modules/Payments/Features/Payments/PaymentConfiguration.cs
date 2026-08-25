using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Payments.Features.Payments;

/// <summary>Maps <see cref="Payment"/> onto <c>payments.payments</c>.</summary>
public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("payments");

        builder.HasKey(payment => payment.Id).HasName("pk_payments");

        // Never store-generated — WP-1.2's lesson: a Guid v7 minted by the aggregate is what tells
        // EF this is an insert, and it is also the idempotency key sent to the provider.
        builder.Property(payment => payment.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(payment => payment.PaymentNumber)
            .HasColumnName("payment_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(payment => payment.ServiceAccountId).HasColumnName("service_account_id");

        builder.Property(payment => payment.AccountNumber)
            .HasColumnName("account_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(payment => payment.CustomerId).HasColumnName("customer_id");

        builder.Property(payment => payment.CustomerName)
            .HasColumnName("customer_name")
            .HasMaxLength(Payment.NameLength)
            .IsRequired();

        builder.Property(payment => payment.BillId).HasColumnName("bill_id");

        builder.Property(payment => payment.BillNumber)
            .HasColumnName("bill_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(payment => payment.Amount)
            .HasColumnName("amount")
            .HasPrecision(Payment.MoneyPrecision, Payment.MoneyScale);

        builder.Property(payment => payment.BalanceBefore)
            .HasColumnName("balance_before")
            .HasPrecision(Payment.MoneyPrecision, Payment.MoneyScale);

        builder.Property(payment => payment.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(payment => payment.Method)
            .HasColumnName("method")
            .HasMaxLength(Payment.MethodLength)
            .IsRequired();

        builder.Property(payment => payment.Instrument)
            .HasColumnName("instrument")
            .HasMaxLength(Payment.InstrumentLength);

        // Both stored by name: a receipt read back years from now must not depend on today's enum
        // ordering, which is what decides whether money moved.
        builder.Property(payment => payment.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(Payment.EnumNameLength)
            .IsRequired();

        builder.Property(payment => payment.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(Payment.EnumNameLength);

        builder.Property(payment => payment.ProviderName)
            .HasColumnName("provider_name")
            .HasMaxLength(Payment.NameLength);

        builder.Property(payment => payment.ProviderReference)
            .HasColumnName("provider_reference")
            .HasMaxLength(Payment.ProviderReferenceLength);

        builder.Property(payment => payment.ProviderMessage)
            .HasColumnName("provider_message")
            .HasMaxLength(Payment.ReasonLength);

        builder.Property(payment => payment.RequestedAt).HasColumnName("requested_at");
        builder.Property(payment => payment.SettledAt).HasColumnName("settled_at");
        builder.Property(payment => payment.StatusChangedAt).HasColumnName("status_changed_at");

        builder.Property(payment => payment.StatusReason)
            .HasColumnName("status_reason")
            .HasMaxLength(Payment.ReasonLength);

        builder.Property(payment => payment.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(RegistryActor.MaxLength)
            .IsRequired();

        builder.Property(payment => payment.ActorName)
            .HasColumnName("actor_name")
            .HasMaxLength(RegistryActor.MaxLength);

        // The receipt number is unique, and it is what makes the number series safe without a lock
        // — see RegistryNumberSeries.
        builder.HasIndex(payment => payment.PaymentNumber)
            .HasDatabaseName("ux_payments_number")
            .IsUnique();

        // A bill's payment history, which is what a customer 360 page and a dispute both ask for.
        builder.HasIndex(payment => payment.BillId).HasDatabaseName("ix_payments_bill");

        // The account's, for the same reason one level up.
        builder.HasIndex(payment => payment.ServiceAccountId).HasDatabaseName("ix_payments_service_account");

        // Deliberately NOT unique on the provider's reference. A sandbox is free to repeat one, a
        // real gateway reuses references across environments, and a unique index over somebody
        // else's identifier is an outage waiting for the day they change their scheme.
        builder.HasIndex(payment => payment.ProviderReference).HasDatabaseName("ix_payments_provider_reference");
    }
}
