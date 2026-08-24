using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Finance.Features.ChartOfAccounts;

/// <summary>Maps <see cref="Account"/> onto <c>finance.accounts</c> and seeds the chart.</summary>
public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("accounts");

        builder.HasKey(account => account.Id).HasName("pk_accounts");

        builder.Property(account => account.Id).HasColumnName("id");
        builder.Property(account => account.Code).HasColumnName("code").HasMaxLength(Account.CodeLength).IsRequired();
        builder.Property(account => account.Name).HasColumnName("name").HasMaxLength(Account.NameLength).IsRequired();

        // Stored by name, like the audit trail's enums: a chart read years from now must not depend
        // on today's enum numbering.
        builder.Property(account => account.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(Account.NameLength)
            .IsRequired();

        // Derived from the type, so there is nothing to store and nothing to disagree with.
        builder.Ignore(account => account.NormalBalance);

        // The code is what a person quotes and what a posting names, so it is unique in its own
        // right — the surrogate key exists for foreign keys, not to permit two 1100s.
        builder.HasIndex(account => account.Code).HasDatabaseName("ux_accounts_code").IsUnique();

        // Reference data ships with the schema: a database that has been migrated has a chart of
        // accounts, in every environment, with no seeder involved.
        builder.HasData(ChartOfAccounts.All);
    }
}
