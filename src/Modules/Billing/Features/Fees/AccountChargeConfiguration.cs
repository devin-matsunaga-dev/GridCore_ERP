using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Billing.Features.Fees;

/// <summary>Maps <see cref="AccountCharge"/> onto <c>billing.account_charges</c>.</summary>
public sealed class AccountChargeConfiguration : IEntityTypeConfiguration<AccountCharge>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AccountCharge> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("account_charges");

        builder.HasKey(charge => charge.Id).HasName("pk_account_charges");

        // Never store-generated — WP-1.2's lesson, and the same reason the bill's key is not.
        builder.Property(charge => charge.Id).HasColumnName("id").ValueGeneratedNever();

        // No foreign keys on either of these: Customers is another module over another schema. The
        // ids come from IServiceAccountDirectory, which has already proved they exist.
        builder.Property(charge => charge.ServiceAccountId).HasColumnName("service_account_id");
        builder.Property(charge => charge.CustomerId).HasColumnName("customer_id");

        // Stamped so a charge reads on its own, without cross-module lookups that would answer
        // differently after a customer is renamed — the call the bill already makes.
        builder.Property(charge => charge.AccountNumber)
            .HasColumnName("account_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(charge => charge.CustomerName)
            .HasColumnName("customer_name")
            .HasMaxLength(Bills.Bill.NameLength)
            .IsRequired();

        // Stored by name: a charge read back years from now must not depend on today's enum ordering.
        builder.Property(charge => charge.Code)
            .HasColumnName("fee_code")
            .HasConversion<string>()
            .HasMaxLength(FeeScheduleEntry.CodeNameLength)
            .IsRequired();

        // The row that priced it. No foreign key to fee_schedule — see the property's remarks.
        builder.Property(charge => charge.FeeScheduleId).HasColumnName("fee_schedule_id");
        builder.Property(charge => charge.ScheduleEffectiveFrom).HasColumnName("schedule_effective_from");

        builder.Property(charge => charge.Description)
            .HasColumnName("description")
            .HasMaxLength(FeeScheduleEntry.NameLength)
            .IsRequired();

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(charge => charge.Amount)
            .HasColumnName("amount")
            .HasPrecision(Bills.Bill.MoneyPrecision, Bills.Bill.MoneyScale);

        builder.Property(charge => charge.Currency)
            .HasColumnName("currency")
            .HasMaxLength(FeeScheduleEntry.CurrencyLength)
            .IsRequired();

        builder.Property(charge => charge.RaisedOn).HasColumnName("raised_on");

        builder.Property(charge => charge.Reason)
            .HasColumnName("reason")
            .HasMaxLength(AccountCharge.ReasonLength)
            .IsRequired();

        builder.Property(charge => charge.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(AccountCharge.EnumNameLength)
            .IsRequired();

        builder.Property(charge => charge.BillId).HasColumnName("bill_id");

        builder.Property(charge => charge.BillNumber)
            .HasColumnName("bill_number")
            .HasMaxLength(RegistryNumbers.MaxLength);

        builder.Property(charge => charge.RaisedAt).HasColumnName("raised_at");
        builder.Property(charge => charge.StatusChangedAt).HasColumnName("status_changed_at");

        builder.Property(charge => charge.StatusReason)
            .HasColumnName("status_reason")
            .HasMaxLength(AccountCharge.ReasonLength);

        builder.Property(charge => charge.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(RegistryActor.MaxLength)
            .IsRequired();

        builder.Property(charge => charge.ActorName)
            .HasColumnName("actor_name")
            .HasMaxLength(RegistryActor.MaxLength);

        // Derived from what is stored. Mapped, EF would want backing fields it cannot find and the
        // model would fail to build at startup rather than in a test.
        builder.Ignore(charge => charge.IsPending);
        builder.Ignore(charge => charge.AllowedTransitions);

        // The two questions asked of this table. "What is waiting to be billed for this account" is
        // what every cycle run asks, once per run, over the accounts it is about; "what did this bill
        // carry" is what a reprint of a counter bill asks.
        builder.HasIndex(charge => new { charge.ServiceAccountId, charge.Status })
            .HasDatabaseName("ix_account_charges_account_status");

        builder.HasIndex(charge => charge.BillId).HasDatabaseName("ix_account_charges_bill_id");
    }
}
