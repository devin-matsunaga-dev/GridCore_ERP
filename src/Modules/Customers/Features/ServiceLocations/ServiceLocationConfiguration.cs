using GridCore.Modules.Customers.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.ServiceLocations;

/// <summary>Maps <see cref="ServiceLocation"/> onto <c>customers.service_locations</c>.</summary>
public sealed class ServiceLocationConfiguration : IEntityTypeConfiguration<ServiceLocation>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ServiceLocation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("service_locations");

        builder.HasKey(location => location.Id).HasName("pk_service_locations");

        builder.Property(location => location.Id).HasColumnName("id");

        builder.Property(location => location.LocationCode)
            .HasColumnName("location_code")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(location => location.Description).HasColumnName("description").HasMaxLength(ServiceLocation.DescriptionLength);
        builder.Property(location => location.IsActive).HasColumnName("is_active");
        builder.Property(location => location.StatusReason).HasColumnName("status_reason").HasMaxLength(ServiceLocation.ReasonLength);
        builder.Property(location => location.RegisteredAt).HasColumnName("registered_at");

        // Owned, not a table of its own: an address has no identity apart from the premise at it,
        // and inlining the columns keeps a registry list one read rather than a join.
        builder.OwnsOne(location => location.Address, address =>
        {
            address.Property(value => value.Line1).HasColumnName("address_line1").HasMaxLength(Address.LineLength).IsRequired();
            address.Property(value => value.Line2).HasColumnName("address_line2").HasMaxLength(Address.LineLength);
            address.Property(value => value.City).HasColumnName("address_city").HasMaxLength(Address.PlaceLength).IsRequired();
            address.Property(value => value.Region).HasColumnName("address_region").HasMaxLength(Address.PlaceLength).IsRequired();
            address.Property(value => value.PostalCode).HasColumnName("address_postal_code").HasMaxLength(Address.PostalCodeLength);
            address.Property(value => value.Country).HasColumnName("address_country").HasMaxLength(Address.CountryLength).IsRequired();

            // Registries are browsed by place, and the demo world spans three islands.
            address.HasIndex(value => value.Region).HasDatabaseName("ix_service_locations_address_region");
        });

        builder.Navigation(location => location.Address).IsRequired();

        builder.HasIndex(location => location.LocationCode).HasDatabaseName("ux_service_locations_location_code").IsUnique();
    }
}
