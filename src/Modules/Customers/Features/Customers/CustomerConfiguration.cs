using GridCore.Modules.Customers.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Customers;

/// <summary>Maps <see cref="Customer"/> onto <c>customers.customers</c>.</summary>
public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customers");

        builder.HasKey(customer => customer.Id).HasName("pk_customers");

        builder.Property(customer => customer.Id).HasColumnName("id");

        builder.Property(customer => customer.AccountNumber)
            .HasColumnName("account_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(customer => customer.Name).HasColumnName("name").HasMaxLength(Customer.NameLength).IsRequired();
        builder.Property(customer => customer.ContactName).HasColumnName("contact_name").HasMaxLength(Customer.NameLength);
        builder.Property(customer => customer.Email).HasColumnName("email").HasMaxLength(Customer.EmailLength);
        builder.Property(customer => customer.Phone).HasColumnName("phone").HasMaxLength(Customer.PhoneLength);

        // Stored by name, not by number: a record read years from now must not depend on today's
        // enum ordering (WP-0.4's rule for the audit trail, and it applies to any stored enum).
        builder.Property(customer => customer.Class)
            .HasColumnName("class")
            .HasConversion<string>()
            .HasMaxLength(Customer.EnumNameLength)
            .IsRequired();

        builder.Property(customer => customer.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(Customer.EnumNameLength)
            .IsRequired();

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(customer => customer.DepositHeld).HasColumnName("deposit_held").HasPrecision(18, 2);

        builder.Property(customer => customer.RegisteredAt).HasColumnName("registered_at");
        builder.Property(customer => customer.StatusChangedAt).HasColumnName("status_changed_at");
        builder.Property(customer => customer.StatusReason).HasColumnName("status_reason").HasMaxLength(Customer.ReasonLength);

        // The account number is quoted by people and matched by machines; the index is what makes
        // "one number, one customer" a database fact rather than a hope the number generator holds.
        builder.HasIndex(customer => customer.AccountNumber).HasDatabaseName("ux_customers_account_number").IsUnique();

        // The registry list filters on status far more often than on anything else.
        builder.HasIndex(customer => customer.Status).HasDatabaseName("ix_customers_status");
    }
}
