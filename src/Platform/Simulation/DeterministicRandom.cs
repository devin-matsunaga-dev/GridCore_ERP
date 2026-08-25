namespace GridCore.Platform.Simulation;

/// <summary>
/// A small, fixed pseudo-random generator — SplitMix64 — shared by GridCore's provider simulators.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="Random"/>.</b> A seeded <c>Random</c> is only documented to be repeatable
/// within one process on one runtime version; the .NET team has changed the seeded algorithm before
/// and reserves the right to again. The work package's requirement is that a seed reproduces a
/// batch — including which meters came back as exceptions — so the sequence has to be part of
/// GridCore rather than part of whatever runtime it happens to be running on. Twenty lines is a
/// cheaper price than a demo whose numbers move after a framework bump.
/// </para>
/// <para>
/// SplitMix64 is chosen because it is stateless apart from one 64-bit counter and needs no warm-up:
/// a stream can be created per meter from a mixed seed and produce good values immediately, which
/// is what lets the simulator give each meter its own independent stream.
/// </para>
/// <para>
/// <b>In Platform rather than in a module.</b> It arrived with the meter reading simulator
/// (WP-2.2) and moved here when the payment sandbox (WP-2.5) became the second caller — a
/// simulator lives in the module that owns its boundary, but the stream it draws from is the same
/// stream, and two copies of a generator are two sequences one framework bump apart from
/// disagreeing. The vendor and crew simulators are the third and fourth callers.
/// </para>
/// <para>
/// Not cryptographic, and nothing here should ever be used where that matters.
/// </para>
/// </remarks>
public sealed class DeterministicRandom
{
    private ulong _state;

    /// <summary>Starts a stream from <paramref name="seed"/>.</summary>
    public DeterministicRandom(ulong seed) => _state = seed;

    /// <summary>
    /// Starts the stream belonging to one subject within one run — the same seed, scope and subject
    /// always give the same stream, and a different subject gives an unrelated one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-subject streams are what make a batch stable as its membership changes: adding a meter to
    /// a reading cycle must not shift every other meter's reading, which is exactly what would
    /// happen if one stream were drawn from in list order.
    /// </para>
    /// <para>
    /// The subject is a <b>string</b>, and for meters it is the meter number rather than the id.
    /// That is the whole reason a seed is worth having: a Guid v7 carries random bits, so two
    /// freshly seeded demo databases hold the same meters under different ids, and a stream keyed on
    /// the id would give every machine a different demo world. The number the utility knows the
    /// device by is the same everywhere.
    /// </para>
    /// </remarks>
    /// <param name="seed">The run's seed.</param>
    /// <param name="scope">
    /// What run this is, usually a cycle code. Mixed in so that the same seed used for August and
    /// for September does not hand every meter the same outcome twice — a demo world where the same
    /// house is unread every single month reads as a bug, not as a simulation.
    /// </param>
    /// <param name="subject">
    /// The subject the stream belongs to — a meter number, a payment number: always the number the
    /// utility knows the thing by, never its id.
    /// </param>
    public static DeterministicRandom For(int seed, string scope, string subject)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(subject);

        var state = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL;

        // Ordinal by construction: char values, never culture-dependent casing or collation, so the
        // same inputs give the same stream on every machine.
        foreach (var character in scope)
        {
            state = Mix(state + character);
        }

        // Separated so ("2026-08", "1") and ("2026-0", "81") cannot collide.
        state = Mix(state + 0xFFFF);

        foreach (var character in subject)
        {
            state = Mix(state + character);
        }

        return new DeterministicRandom(state);
    }

    /// <summary>The next 64 bits of the stream.</summary>
    public ulong Next()
    {
        _state += 0x9E3779B97F4A7C15UL;

        return Mix(_state);
    }

    /// <summary>The next value in <c>[0, 1)</c>, to full decimal precision the caller can round.</summary>
    public decimal NextUnit() =>
        // 53 bits, the same mantissa width a double would have carried, taken as a decimal so no
        // consumption figure ever passes through binary floating point.
        (decimal)(Next() >> 11) / 9_007_199_254_740_992m;

    /// <summary>The next value in <c>[minimum, maximum)</c>.</summary>
    public decimal NextDecimal(decimal minimum, decimal maximum) =>
        minimum + (NextUnit() * (maximum - minimum));

    /// <summary>Whether an event of probability <paramref name="chance"/> happens next.</summary>
    public bool Chance(decimal chance) => NextUnit() < chance;

    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;

        return value ^ (value >> 31);
    }
}
