namespace GridCore.Platform.Seeding;

/// <summary>
/// The record that a demo seeder has run. Its existence is what makes seeding idempotent: the
/// runner skips any seeder already named here, so starting the host ten times seeds one demo world.
/// </summary>
public sealed class DemoSeedRecord
{
    /// <summary>Longest seeder name stored.</summary>
    public const int NameLength = 256;

    private DemoSeedRecord()
    {
        // EF materialisation.
        Name = string.Empty;
    }

    /// <summary>The seeder's <see cref="IDemoSeeder.Name"/>. Also the primary key.</summary>
    public string Name { get; private init; }

    /// <summary>When it ran.</summary>
    public DateTimeOffset SeededAt { get; private init; }

    /// <summary>Records a completed run.</summary>
    public static DemoSeedRecord For(string name, DateTimeOffset seededAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new DemoSeedRecord
        {
            Name = name.Length > NameLength ? name[..NameLength] : name,
            SeededAt = seededAt,
        };
    }
}
