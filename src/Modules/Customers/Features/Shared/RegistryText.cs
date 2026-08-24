namespace GridCore.Modules.Customers.Features.Shared;

/// <summary>
/// How the registry treats the free text a caller types. Shared so a description, a reason and a
/// name cannot drift into storing "  " differently from each other.
/// </summary>
public static class RegistryText
{
    /// <summary>
    /// Trims <paramref name="value"/>, turns whitespace-only into <see langword="null"/>, and caps
    /// it at <paramref name="maxLength"/> so a caller cannot get a 500 out of the column's width.
    /// </summary>
    public static string? Clean(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

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
