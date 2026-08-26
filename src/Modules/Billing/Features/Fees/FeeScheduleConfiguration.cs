using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Billing.Features.Fees;

/// <summary>Maps <see cref="FeeScheduleEntry"/> onto <c>billing.fee_schedule</c> and seeds it.</summary>
public sealed class FeeScheduleConfiguration : IEntityTypeConfiguration<FeeScheduleEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<FeeScheduleEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("fee_schedule");

        builder.HasKey(entry => entry.Id).HasName("pk_fee_schedule");

        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();

        // Stored by name, like every other enum in this schema: a schedule read years from now must
        // not depend on today's enum numbering.
        builder.Property(entry => entry.Code)
            .HasColumnName("code")
            .HasConversion<string>()
            .HasMaxLength(FeeScheduleEntry.CodeNameLength)
            .IsRequired();

        builder.Property(entry => entry.Name)
            .HasColumnName("name")
            .HasMaxLength(FeeScheduleEntry.NameLength)
            .IsRequired();

        builder.Property(entry => entry.Description)
            .HasColumnName("description")
            .HasMaxLength(FeeScheduleEntry.DescriptionLength)
            .IsRequired();

        builder.Property(entry => entry.ServiceType)
            .HasColumnName("service_type")
            .HasConversion<string>()
            .HasMaxLength(FeeScheduleEntry.ServiceTypeNameLength)
            .IsRequired();

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(entry => entry.Amount)
            .HasColumnName("amount")
            .HasPrecision(Bills.Bill.MoneyPrecision, Bills.Bill.MoneyScale)
            .IsRequired();

        builder.Property(entry => entry.Currency)
            .HasColumnName("currency")
            .HasMaxLength(FeeScheduleEntry.CurrencyLength)
            .IsRequired();

        builder.Property(entry => entry.EffectiveFrom).HasColumnName("effective_from");

        builder.Ignore(entry => entry.VersionKey);

        // A fee has ONE figure on any given day. The version — code and effective date together —
        // is the row's natural key, exactly as ux_rate_plans_code_effective makes a tariff's.
        builder.HasIndex(entry => new { entry.Code, entry.EffectiveFrom })
            .HasDatabaseName("ux_fee_schedule_code_effective")
            .IsUnique();

        // Reference data ships with the schema: a migrated database can price a fee, in every
        // environment, with no seeder involved (ARCHITECTURE.md invariant 8).
        //
        // The completeness check runs HERE, where the model is built, so a declared code with no row
        // fails at startup rather than at the counter — WORK_PACKAGES.md asks this package for
        // exactly that, and this is the one line that delivers it.
        FeeSchedules.RequireComplete(FeeSchedules.All);

        builder.HasData(FeeSchedules.All);
    }
}
