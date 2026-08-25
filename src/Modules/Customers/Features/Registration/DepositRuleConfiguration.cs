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

        builder.Property(rule => rule.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(rule => rule.Description)
            .HasColumnName("description")
            .HasMaxLength(DepositRule.DescriptionLength)
            .IsRequired();

        // The class is the rule's identity, so it is unique in its own right — the surrogate key
        // exists to be a key, not to permit two residential schedules that disagree.
        builder.HasIndex(rule => rule.CustomerClass).HasDatabaseName("ux_deposit_rules_class").IsUnique();

        // Reference data ships with the schema: a migrated database can assess a deposit, in every
        // environment, with no seeder involved (ARCHITECTURE.md invariant 8).
        DepositRules.RequireComplete(DepositRules.All);

        builder.HasData(DepositRules.All);
    }
}
