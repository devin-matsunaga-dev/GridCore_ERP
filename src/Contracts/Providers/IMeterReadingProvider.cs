namespace GridCore.Contracts.Providers;

/// <summary>
/// One meter the utility wants read, as the reading provider is told about it.
/// </summary>
/// <remarks>
/// Everything a provider needs to produce a plausible dial reading and nothing it does not: no
/// customer, no account, no premise address. A real AMI head-end is handed a device identifier and
/// its last known state, and that is exactly what this carries — which is what lets the simulator
/// be swapped for one by DI config alone (ARCHITECTURE.md's provider rule).
/// </remarks>
/// <param name="MeterId">The meter in the Metering schema.</param>
/// <param name="MeterNumber">The number the utility knows it by, for the provider's own logs.</param>
/// <param name="MeterType">How the meter measures the service, by name — never the module's enum.</param>
/// <param name="RegisterDigits">
/// How many whole digits the meter's register carries. What decides where the dials roll back to
/// zero, so a provider can produce a reading that has wrapped rather than one that cannot exist.
/// </param>
/// <param name="LastReading">
/// The dials as they were last known to read — the previous reading, or what was recorded when the
/// meter was fitted. <see langword="null"/> for a meter that has never been read.
/// </param>
/// <param name="LastReadAt">When that was, so a provider can size consumption to the period.</param>
public sealed record MeterReadingRequest(
    Guid MeterId,
    string MeterNumber,
    string MeterType,
    int RegisterDigits,
    decimal? LastReading,
    DateTimeOffset? LastReadAt);

/// <summary>A batch of meters to read, as one billing cycle.</summary>
/// <remarks>
/// The <paramref name="Seed" /> is what makes a run reproducible: the same cycle asked for twice
/// with the same seed must produce the same readings, including the same exceptions. That is a
/// requirement rather than a nicety — a demonstration whose numbers move between runs cannot be
/// reconciled, and a test that cannot predict an exception cannot assert one.
/// </remarks>
/// <param name="CycleCode">What the utility calls this reading run, e.g. <c>2026-08</c>.</param>
/// <param name="ReadAt">The date the meters are read as at.</param>
/// <param name="Seed">Seed for the provider's own randomness. Same seed, same batch.</param>
/// <param name="Meters">The meters to read, in no particular order.</param>
public sealed record MeterReadingCycle(
    string CycleCode,
    DateTimeOffset ReadAt,
    int Seed,
    IReadOnlyList<MeterReadingRequest> Meters);

/// <summary>What a provider came back with for one meter.</summary>
/// <param name="MeterId">Which meter.</param>
/// <param name="Reading">
/// What the dials read, or <see langword="null"/> when the meter could not be read at all — a
/// locked gate, a flooded box, a dead comms module. A missing read is a real outcome of a reading
/// cycle, so it is reported rather than silently dropped from the batch.
/// </param>
/// <param name="ReadAt">When this meter was read.</param>
/// <param name="Note">What the provider wants recorded against it, where anything is.</param>
public sealed record MeterReadingResult(
    Guid MeterId,
    decimal? Reading,
    DateTimeOffset ReadAt,
    string? Note);

/// <summary>Everything a provider produced for one cycle.</summary>
/// <param name="CycleCode">The cycle that was read.</param>
/// <param name="ReadAt">The date it was read as at.</param>
/// <param name="Seed">The seed the batch was produced from.</param>
/// <param name="Readings">One result per meter asked for.</param>
public sealed record MeterReadingBatch(
    string CycleCode,
    DateTimeOffset ReadAt,
    int Seed,
    IReadOnlyList<MeterReadingResult> Readings);

/// <summary>
/// Where meter readings come from — the simulation seam for metering, and the reason no domain code
/// in GridCore ever calls a simulator by name (ARCHITECTURE.md invariant 6).
/// </summary>
/// <remarks>
/// <para>
/// The MVP's implementation generates a cycle batch with realistic high-usage, zero-usage and
/// missing-read exceptions. Production swaps it for an AMI head-end or a hand-held reader's import
/// through DI configuration, with nothing in the Metering module changed: the module knows how to
/// turn a dial reading into consumption, and nothing about how the number was obtained.
/// </para>
/// <para>
/// A provider <b>reads meters</b>. It never decides what a reading means: consumption, rollover and
/// the exception codes are the Metering module's own work, computed from what came back. A provider
/// that classified its own readings would be a provider that had to be trusted, and a real one
/// cannot be.
/// </para>
/// </remarks>
public interface IMeterReadingProvider
{
    /// <summary>
    /// What this provider is, for the audit entry a reading run leaves behind. A record of where
    /// numbers came from outlives whichever implementation was configured at the time.
    /// </summary>
    string Name { get; }

    /// <summary>Reads every meter in <paramref name="cycle"/> and returns one result for each.</summary>
    Task<MeterReadingBatch> ReadCycleAsync(MeterReadingCycle cycle, CancellationToken cancellationToken = default);
}
