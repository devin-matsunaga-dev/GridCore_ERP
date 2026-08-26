using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.Features.ServiceLocations;

/// <summary>
/// A physical address. A value object owned by the thing that is at it — it has no identity of its
/// own, and two premises at the same address are still two premises.
///
/// Owned by a service location and, since WP-2.11, by a customer profile's mailing address. One
/// shape for both: an address that post goes to and an address a crew navigates to are the same
/// kind of thing, and a second copy is how the two drift into validating differently.
/// </summary>
public sealed class Address
{
    /// <summary>Longest street line stored.</summary>
    public const int LineLength = 200;

    /// <summary>Longest town, village, region or island name stored.</summary>
    public const int PlaceLength = 128;

    /// <summary>Longest postal code stored.</summary>
    public const int PostalCodeLength = 16;

    /// <summary>Longest country name or code stored.</summary>
    public const int CountryLength = 64;

    private Address()
    {
        // EF materialisation.
        Line1 = string.Empty;
        City = string.Empty;
        Region = string.Empty;
        Country = string.Empty;
    }

    /// <summary>Street address, or the description a crew would navigate by.</summary>
    public string Line1 { get; private init; }

    /// <summary>Unit, floor or building, where there is one.</summary>
    public string? Line2 { get; private init; }

    /// <summary>Town or village.</summary>
    public string City { get; private init; }

    /// <summary>State, province or island.</summary>
    public string Region { get; private init; }

    /// <summary>Postal code, where the territory uses one.</summary>
    public string? PostalCode { get; private init; }

    /// <summary>Country, as a name or an ISO code.</summary>
    public string Country { get; private init; }

    /// <summary>The address on one line, for a list, a work order header or an event.</summary>
    public string OneLine =>
        string.Join(", ", new[] { Line1, Line2, City, Region, PostalCode }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Builds an address, trimming each part and refusing an incomplete one.</summary>
    /// <exception cref="RegistryValidationException">A required part is missing.</exception>
    public static Address Create(
        string line1,
        string city,
        string region,
        string country,
        string? line2 = null,
        string? postalCode = null)
    {
        Require(line1, nameof(line1));
        Require(city, nameof(city));
        Require(region, nameof(region));
        Require(country, nameof(country));

        return new Address
        {
            Line1 = Clean(line1, LineLength)!,
            Line2 = Clean(line2, LineLength),
            City = Clean(city, PlaceLength)!,
            Region = Clean(region, PlaceLength)!,
            PostalCode = Clean(postalCode, PostalCodeLength),
            Country = Clean(country, CountryLength)!,
        };
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RegistryValidationException($"'{field}' is required for an address.");
        }
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length is 0)
        {
            return null;
        }

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
