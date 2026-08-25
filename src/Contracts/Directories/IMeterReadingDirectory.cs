namespace GridCore.Contracts.Directories;

/// <summary>
/// A meter reading as another module sees it: the figure, what it was measured against, the period
/// it covers, and whether somebody still has to look at it.
/// </summary>
/// <remarks>
/// <para>
/// A DTO, never the entity, for <see cref="ServiceLocationSummary"/>'s reason. It carries the meter
/// <i>number</i> as well as the id because whoever bills from a reading prints it: a bill that
/// could not name the meter it was raised from is one nobody can check.
/// </para>
/// <para>
/// Everything here is stamped on the reading rather than derived on read — consumption, the
/// previous dials, the exception code. That is WP-2.2's decision and it is what makes a bill
/// reproducible years later, after the meter's register width has been corrected or the device has
/// moved to somebody else's wall.
/// </para>
/// </remarks>
/// <param name="Id">Identifier of the reading, in the Metering schema.</param>
/// <param name="MeterId">The meter it came off.</param>
/// <param name="MeterNumber">That meter's number, e.g. <c>MTR-000001</c>.</param>
/// <param name="ServiceLocationId">The premise the meter was measuring when it was read.</param>
/// <param name="ReadingDate">When the dials were read.</param>
/// <param name="Reading">What they read, or <see langword="null"/> for a missing read.</param>
/// <param name="PreviousReading">What the meter last read at this premise.</param>
/// <param name="PreviousReadingDate">When that was — the start of the period this reading closes.</param>
/// <param name="Consumption">
/// Units used over the period, or <see langword="null"/> where there was nothing to measure from.
/// </param>
/// <param name="ExceptionCode">Why the reading is on the worklist, by name; <c>None</c> if it is not.</param>
/// <param name="IsException">Whether it is on the worklist at all.</param>
/// <param name="CycleCode">The reading cycle it belongs to, or <see langword="null"/> for a manual read.</param>
public sealed record MeterReadingSummary(
    Guid Id,
    Guid MeterId,
    string MeterNumber,
    Guid ServiceLocationId,
    DateTimeOffset ReadingDate,
    decimal? Reading,
    decimal? PreviousReading,
    DateTimeOffset? PreviousReadingDate,
    decimal? Consumption,
    string ExceptionCode,
    bool IsException,
    string? CycleCode);

/// <summary>
/// Read access to the meter reading register for modules that are not Metering.
/// </summary>
/// <remarks>
/// <para>
/// The seam ARCHITECTURE.md's boundary rule requires, shaped like
/// <see cref="IServiceLocationDirectory"/>: the interface lives in <c>Contracts</c>, Metering
/// registers the implementation, and Billing (WP-2.3) consumes it without ever reading
/// <c>metering.meter_readings</c>.
/// </para>
/// <para>
/// A <b>read</b> seam rather than the <c>MeterReadingRecorded</c> event, deliberately. WP-2.2 chose
/// not to raise a "cycle finished, go and bill it" event because that would be an instruction
/// rather than a fact — so a billing run asks the register what a cycle produced, at the moment
/// somebody decides to bill it, which is also what lets the run be re-run after the exception
/// worklist has been cleared.
/// </para>
/// </remarks>
public interface IMeterReadingDirectory
{
    /// <summary>One reading, or <see langword="null"/> when there is no such id.</summary>
    Task<MeterReadingSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every reading a reading cycle produced, oldest first — including the ones on the exception
    /// worklist, because deciding what to do with those is the caller's business and hiding them
    /// would make a billing run silently skip meters it never mentioned.
    /// </summary>
    Task<IReadOnlyList<MeterReadingSummary>> ForCycleAsync(
        string cycleCode,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recent readings at a premise, newest first, across however many meters have stood there —
    /// the question "what has this place been using", which belongs to the premise and not to the
    /// device.
    /// </summary>
    Task<IReadOnlyList<MeterReadingSummary>> AtLocationAsync(
        Guid serviceLocationId,
        int limit,
        CancellationToken cancellationToken = default);
}
