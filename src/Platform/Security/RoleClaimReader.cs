using System.Security.Claims;
using System.Text.Json;

namespace GridCore.Platform.Security;

/// <summary>
/// Reads role names out of an access token's claims. Providers disagree about where roles live —
/// Keycloak nests them in a <c>realm_access</c> JSON object, others emit flat <c>roles</c> claims —
/// so the location is a configured path rather than a hard-coded claim type.
/// Pure and static: the whole claim-shape problem is unit-testable without an identity provider.
/// </summary>
public static class RoleClaimReader
{
    private const char PathSeparator = '.';

    /// <summary>
    /// Reads the roles at <paramref name="rolesClaimPath"/>. A malformed or missing claim yields no
    /// roles rather than an exception: a token GridCore cannot read must mean no access, never a 500.
    /// </summary>
    public static IReadOnlyList<string> ReadRoles(ClaimsPrincipal principal, string rolesClaimPath)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(rolesClaimPath);

        var segments = rolesClaimPath.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return [];
        }

        var roles = new List<string>();

        foreach (var claim in principal.FindAll(segments[0]))
        {
            roles.AddRange(ReadClaimValue(claim.Value, segments.AsSpan(1)));
        }

        return roles.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Resolves one claim value: a plain string when the path ends here, otherwise a JSON object or
    /// array walked down the remaining segments.
    /// </summary>
    private static IEnumerable<string> ReadClaimValue(string value, ReadOnlySpan<string> remainingSegments)
    {
        if (remainingSegments.IsEmpty && !LooksLikeJson(value))
        {
            return [value];
        }

        // ToArray: the span cannot cross the iterator boundary below.
        return ReadJsonClaimValue(value, remainingSegments.ToArray());
    }

    private static IEnumerable<string> ReadJsonClaimValue(string value, string[] remainingSegments)
    {
        JsonElement element;

        try
        {
            using var document = JsonDocument.Parse(value);
            element = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A claim that should have been JSON but is not grants nothing.
            return [];
        }

        foreach (var segment in remainingSegments)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return [];
            }
        }

        return element.ValueKind switch
        {
            JsonValueKind.Array => element
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList(),
            JsonValueKind.String => [element.GetString()!],
            _ => [],
        };
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.AsSpan().TrimStart();

        return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
    }
}
