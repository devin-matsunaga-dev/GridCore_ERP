namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>
/// Why a reading is held for somebody to look at before it becomes a bill — the utility's "read
/// code", raised by <see cref="ReadingAssessment"/> when a reading is recorded.
/// </summary>
/// <remarks>
/// <para>
/// Named for the code rather than for an exception: nothing here is a CLR failure, and none of it
/// stops a reading being recorded. A flagged reading is still a reading — it is in the register,
/// it is auditable, and it is exactly what the exception worklist exists to show. Refusing to
/// record it would leave the utility with no evidence of what the meter actually said.
/// </para>
/// <para>
/// One code per reading, and they are mutually exclusive by construction: a meter that could not be
/// read has no consumption to be high or zero.
/// </para>
/// </remarks>
public enum ReadingExceptionCode
{
    /// <summary>An ordinary read. Nothing to look at.</summary>
    None = 1,

    /// <summary>
    /// The premise used far more per day than it usually does. Most often a leak, a new load, or a
    /// transposed digit — and occasionally a genuinely hot month, which is why it is a flag rather
    /// than a refusal.
    /// </summary>
    HighUsage = 2,

    /// <summary>
    /// The dials have not moved since the last reading. An empty property or a stopped meter, and
    /// the difference matters: one is billed at nothing, the other means the meter is faulty and
    /// the customer has been under-billed since it stopped.
    /// </summary>
    ZeroUsage = 3,

    /// <summary>
    /// The meter could not be read at all — a locked gate, a flooded box, a dead comms module. The
    /// line records that somebody tried and failed, and carries no reading and no consumption.
    /// </summary>
    MissingRead = 4,
}

/// <summary>Where a reading came from.</summary>
/// <remarks>
/// Recorded on every line because it is the first question asked of a disputed bill. Estimation is
/// deliberately absent: GridCore bills what a meter said, and a missing read stays missing until
/// somebody goes back — an estimate is a number the utility invented, and inventing one is a
/// decision the MVP does not make.
/// </remarks>
public enum MeterReadingSource
{
    /// <summary>Keyed in by a person — a crew's card, a customer's own read, a re-read after a query.</summary>
    Manual = 1,

    /// <summary>Produced by a reading cycle run through <c>IMeterReadingProvider</c>.</summary>
    Cycle = 2,
}
