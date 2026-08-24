using GridCore.Platform.Data;

namespace GridCore.Modules.Inventory.Features.Warehouses;

/// <summary>
/// A place stock is held. Reference data: stock cannot be received, issued or counted without
/// somewhere to be, so warehouses ship by migration rather than with the demo world.
/// </summary>
/// <remarks>
/// There are no quantities here. Stock on hand belongs to the item-in-warehouse relationship WP-1.4
/// adds; a warehouse is only the place.
/// </remarks>
public sealed class Warehouse
{
    /// <summary>Longest warehouse code stored.</summary>
    public const int CodeLength = 16;

    /// <summary>Longest warehouse name stored.</summary>
    public const int NameLength = 128;

    /// <summary>Longest site description stored.</summary>
    public const int LocationLength = 256;

    private Warehouse()
    {
        // EF materialisation.
        Code = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Identifier of this warehouse.</summary>
    public Guid Id { get; private init; }

    /// <summary>The code a person quotes, e.g. <c>MAIN</c>. Unique across warehouses.</summary>
    public string Code { get; private init; }

    /// <summary>What the warehouse is called.</summary>
    public string Name { get; private init; }

    /// <summary>Where it is, as free text. No GIS — SPEC.md defers that.</summary>
    public string? Location { get; private init; }

    /// <summary>
    /// Whether stock may still move through it. Closing a warehouse is a state change, never a
    /// delete: its past movements are history that later reports read.
    /// </summary>
    public bool IsActive { get; private init; }

    /// <summary>
    /// Builds a reference warehouse. The id is derived from the code so the migration seeds the
    /// same row every time it is generated — see <see cref="ReferenceId"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A required value is missing or too long.</exception>
    public static Warehouse Reference(string code, string name, string? location, bool isActive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(code.Length, CodeLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, NameLength);

        if (location is { Length: > LocationLength })
        {
            throw new ArgumentException($"Warehouse '{code}' has a location longer than {LocationLength} characters.", nameof(location));
        }

        // Codes are quoted by people and matched by machines; one canonical case means "rota" and
        // "ROTA" can never become two warehouses.
        if (!string.Equals(code, code.ToUpperInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"Warehouse code '{code}' must be upper case.", nameof(code));
        }

        return new Warehouse
        {
            Id = ReferenceId.For(DefaultWarehouses.AuthoredAt, code),
            Code = code,
            Name = name,
            Location = location,
            IsActive = isActive,
        };
    }
}
