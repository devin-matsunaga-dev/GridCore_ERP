using System.Security.Cryptography;
using System.Text;

namespace GridCore.Platform.Data;

/// <summary>
/// Stable identifiers for reference data shipped by a migration.
/// </summary>
/// <remarks>
/// <para>
/// Runtime entities take <c>Guid.CreateVersion7(now)</c>, but a migration cannot: EF compares the
/// seeded rows against the model snapshot, so an id that changed on every model build would rewrite
/// the chart of accounts on every <c>dotnet ef migrations add</c>. Reference rows therefore derive
/// their id from a fixed instant and their own natural key, which is deterministic <i>and</i> still
/// a real version 7 UUID — the layout is what makes the primary-key index order chronologically,
/// and reference data that sorted differently from everything else would be a trap for later
/// queries.
/// </para>
/// <para>
/// The derivation is not a secret and is not meant to be: it is a hash used as a spreading
/// function, so two codes never collide and the same code always yields the same row.
/// </para>
/// </remarks>
public static class ReferenceId
{
    /// <summary>
    /// The id of the reference row identified by <paramref name="key"/>.
    /// </summary>
    /// <param name="seededAt">
    /// The instant the reference set was authored. Fixed per set and never changed afterwards —
    /// changing it changes every id in the set, which to the database is a different set of rows.
    /// </param>
    /// <param name="key">The row's natural key, e.g. an account code. Case sensitive.</param>
    public static Guid For(DateTimeOffset seededAt, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Span<byte> bytes = stackalloc byte[16];

        // Bytes 0-5: the 48-bit big-endian Unix millisecond timestamp, exactly as version 7 lays it
        // out, so these ids sort chronologically against runtime ones.
        var milliseconds = seededAt.ToUnixTimeMilliseconds();

        for (var index = 0; index < 6; index++)
        {
            bytes[index] = (byte)(milliseconds >> ((5 - index) * 8));
        }

        // Bytes 6-15: what version 7 fills with randomness, filled here from the key instead.
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        hash[..10].CopyTo(bytes[6..]);

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant

        return new Guid(bytes, bigEndian: true);
    }
}
