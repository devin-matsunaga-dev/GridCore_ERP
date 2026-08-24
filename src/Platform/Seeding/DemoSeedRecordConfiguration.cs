using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Platform.Seeding;

/// <summary>Maps <see cref="DemoSeedRecord"/> onto <c>platform.demo_seed_records</c>.</summary>
public sealed class DemoSeedRecordConfiguration : IEntityTypeConfiguration<DemoSeedRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DemoSeedRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("demo_seed_records");

        // The seeder's name is the natural key, and the uniqueness that makes seeding idempotent is
        // then the primary key rather than a check the runner has to remember to make.
        builder.HasKey(record => record.Name).HasName("pk_demo_seed_records");

        builder.Property(record => record.Name).HasColumnName("name").HasMaxLength(DemoSeedRecord.NameLength);
        builder.Property(record => record.SeededAt).HasColumnName("seeded_at").IsRequired();
    }
}
