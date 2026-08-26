using GridCore.Modules.Customers.Features.Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Customers.Features.Contacts;

/// <summary>Maps <see cref="CustomerContact"/> onto <c>customers.customer_contacts</c>.</summary>
public sealed class CustomerContactConfiguration : IEntityTypeConfiguration<CustomerContact>
{
    /// <summary>Longest stored form of a contact-method kind.</summary>
    public const int EnumNameLength = Customer.EnumNameLength;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CustomerContact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customer_contacts");

        builder.HasKey(contact => contact.Id).HasName("pk_customer_contacts");

        builder.Property(contact => contact.Id).HasColumnName("id");
        builder.Property(contact => contact.CustomerId).HasColumnName("customer_id");
        builder.Property(contact => contact.Name).HasColumnName("name").HasMaxLength(CustomerContact.NameLength).IsRequired();
        builder.Property(contact => contact.Relationship).HasColumnName("relationship").HasMaxLength(CustomerContact.RelationshipLength);
        builder.Property(contact => contact.IsAuthorisedToDiscuss).HasColumnName("is_authorised_to_discuss");
        builder.Property(contact => contact.RecordedAt).HasColumnName("recorded_at");

        // Every read of this table is "the contacts of one customer", which is the only shape the
        // 360 asks for and the only one an endpoint offers.
        builder.HasIndex(contact => contact.CustomerId).HasDatabaseName("ix_customer_contacts_customer");

        // The methods are the contact's and nobody else's: they are loaded with it, saved with it,
        // and deleted with it. A backing field rather than the property, because the collection is
        // exposed as IReadOnlyList so the aggregate keeps the only way to add one.
        builder.HasMany(contact => contact.Methods)
            .WithOne()
            .HasForeignKey(method => method.CustomerContactId)
            .HasConstraintName("fk_contact_methods_contact")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata
            .FindNavigation(nameof(CustomerContact.Methods))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Maps <see cref="ContactMethod"/> onto <c>customers.contact_methods</c>.</summary>
public sealed class ContactMethodConfiguration : IEntityTypeConfiguration<ContactMethod>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ContactMethod> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("contact_methods");

        builder.HasKey(method => method.Id).HasName("pk_contact_methods");

        builder.Property(method => method.Id).HasColumnName("id");
        builder.Property(method => method.CustomerContactId).HasColumnName("customer_contact_id");

        // Stored by name, not by number — the rule every stored enum in this module follows.
        builder.Property(method => method.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(CustomerContactConfiguration.EnumNameLength)
            .IsRequired();

        builder.Property(method => method.Value).HasColumnName("value").HasMaxLength(ContactMethod.ValueLength).IsRequired();
        builder.Property(method => method.IsPrimary).HasColumnName("is_primary");
        builder.Property(method => method.RecordedAt).HasColumnName("recorded_at");

        // Plain, NOT a unique index on the primary — and that is a decision, not an omission.
        //
        // A filtered unique index over (contact, kind) WHERE is_primary expresses "one primary per
        // kind" exactly, and it was written that way first. It cannot survive a promotion: demoting
        // the old primary and promoting the new one are two UPDATEs in one SaveChanges, EF decides
        // their order, and Postgres checks a unique index per statement — so whenever the promotion
        // landed first the write failed on a constraint the finished state satisfies. It failed
        // intermittently, because the order follows the rows' Guid ordering. A deferrable constraint
        // is the usual way out and is not available here: Postgres cannot defer a PARTIAL unique
        // index, and the fast tier's SQLite cannot defer anything.
        //
        // So the invariant is the aggregate's, which is where it was always enforced — every path
        // that touches a primary goes through CustomerContact, whose mutators are the only way in,
        // and CustomerContactTests is what says the rule holds. The index that remains is the one
        // the reads actually want: a contact's methods, by kind.
        builder.HasIndex(method => new { method.CustomerContactId, method.Kind })
            .HasDatabaseName("ix_contact_methods_contact_kind");
    }
}
