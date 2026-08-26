using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceLocations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Profile;

/// <summary>Maps <see cref="CustomerProfile"/> onto <c>customers.customer_profiles</c>.</summary>
public sealed class CustomerProfileConfiguration : IEntityTypeConfiguration<CustomerProfile>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CustomerProfile> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customer_profiles");

        // The customer IS the key. One profile per customer is then a database fact rather than
        // something a service remembers to check, and there is no second id for a caller to quote.
        builder.HasKey(profile => profile.CustomerId).HasName("pk_customer_profiles");

        builder.Property(profile => profile.CustomerId).HasColumnName("customer_id").ValueGeneratedNever();

        builder.Property(profile => profile.BillDeliveryChannel)
            .HasColumnName("bill_delivery_channel")
            .HasConversion<string>()
            .HasMaxLength(Customer.EnumNameLength)
            .IsRequired();

        builder.Property(profile => profile.OutageNotices).HasColumnName("outage_notices");
        builder.Property(profile => profile.DunningNotices).HasColumnName("dunning_notices");

        builder.Property(profile => profile.PreferredLanguage)
            .HasColumnName("preferred_language")
            .HasConversion<string>()
            .HasMaxLength(Customer.EnumNameLength)
            .IsRequired();

        builder.Property(profile => profile.UpdatedAt).HasColumnName("updated_at");

        // Owned and OPTIONAL, which is the whole design: all six columns null means "post goes to
        // the service address". The same value object the premise registry stores, because it is the
        // same kind of thing and two address shapes in one schema is how they drift.
        builder.OwnsOne(profile => profile.MailingAddress, address =>
        {
            address.Property(value => value.Line1).HasColumnName("mailing_line1").HasMaxLength(Address.LineLength);
            address.Property(value => value.Line2).HasColumnName("mailing_line2").HasMaxLength(Address.LineLength);
            address.Property(value => value.City).HasColumnName("mailing_city").HasMaxLength(Address.PlaceLength);
            address.Property(value => value.Region).HasColumnName("mailing_region").HasMaxLength(Address.PlaceLength);
            address.Property(value => value.PostalCode).HasColumnName("mailing_postal_code").HasMaxLength(Address.PostalCodeLength);
            address.Property(value => value.Country).HasColumnName("mailing_country").HasMaxLength(Address.CountryLength);
        });

        builder.Navigation(profile => profile.MailingAddress).IsRequired(false);
    }
}
