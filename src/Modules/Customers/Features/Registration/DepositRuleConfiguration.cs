using GridCore.Platform.Monetary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Registration;

/// <summary>Maps <see cref="DepositRule"/> onto <c>customers.deposit_rules</c> and seeds the schedule.</summary>
public sealed class DepositRuleConfiguration : IEntityTypeConfiguration<DepositRule>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DepositRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("deposit_rules");

        builder.HasKey(rule => rule.Id).HasName("pk_deposit_rules");

        builder.Property(rule => rule.Id).HasColumnName("id");

        // Stored by name, like every other enum in this schema: a schedule read years from now must
        // not depend on today's enum numbering.
        builder.Property(rule => rule.CustomerClass)
            .HasColumnName("customer_class")
            .HasConversion<string>()
            .HasMaxLength(DepositRule.ClassNameLength)
            .IsRequired();

        builder.Property(rule => rule.ServiceType)
            .HasColumnName("service_type")
            .HasConversion<string>()
            .HasMaxLength(DepositRule.ServiceTypeNameLength)
            .IsRequired();

        builder.Property(rule => rule.MinimumAmount)
            .HasColumnName("minimum_amount")
            .HasPrecision(Money.Precision, Money.DecimalPlaces)
            .IsRequired();

        // Both nullable, and null together: a flat deposit has neither. The pair is checked in
        // DepositRule.Reference rather than by a check constraint, because the message a reader
        // needs is "a usage basis is both or neither" and a constraint violation says none of that.
        builder.Property(rule => rule.UsageMonths).HasColumnName("usage_months");

        builder.Property(rule => rule.UsageRate)
            .HasColumnName("usage_rate")
            .HasPrecision(DepositRule.RatePrecision, DepositRule.RateDecimalPlaces);

        builder.Property(rule => rule.Currency)
            .HasColumnName("currency")
            .HasMaxLength(DepositRule.CurrencyLength)
            .IsRequired();

        builder.Property(rule => rule.Description)
            .HasColumnName("description")
            .HasMaxLength(DepositRule.DescriptionLength)
            .IsRequired();

        // The class AND THE SERVICE are the rule's identity (WP-2.17), so the pair is unique in its
        // own right — the surrogate key exists to be a key, not to permit two residential electric
        // schedules that disagree. Unique on the class alone, which is what WP-2.8 shipped when a
        // deposit was one figure, would now refuse the second service a class ever takes.
        builder.HasIndex(rule => new { rule.CustomerClass, rule.ServiceType })
            .HasDatabaseName("ux_deposit_rules_class_service")
            .IsUnique();

        // Reference data ships with the schema: a migrated database can assess a deposit, in every
        // environment, with no seeder involved (ARCHITECTURE.md invariant 8).
        DepositRules.RequireComplete(DepositRules.All);

        builder.HasData(DepositRules.All);
    }
}
