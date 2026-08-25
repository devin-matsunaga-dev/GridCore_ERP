using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>
/// What a new reading is measured against: the dials as they were last known to read at this
/// premise, and what the premise normally uses.
/// </summary>
/// <remarks>
/// <para>
/// Two different questions, deliberately answered from two different places.
/// </para>
/// <para>
/// The <b>previous dial reading</b> has to come from <i>this meter on its current fitting</i>.
/// Dials do not carry over: when a meter is exchanged the new one starts wherever its own register
/// stands, and measuring from the old meter's last reading would bill the difference between two
/// unrelated devices. That is what <see cref="Reading"/> and <see cref="ReadAt"/> are, and why they
/// fall back to what was recorded when the meter went on the wall rather than to zero.
/// </para>
/// <para>
/// <b>What the premise normally uses</b> is the opposite: it belongs to the <i>place</i>, not the
/// device. A house that used twenty units a day last month uses about twenty this month whether or
/// not the meter was changed in between, so <see cref="TypicalDailyConsumption"/> is drawn from
/// every recent reading at the premise, across exchanges. Keeping it per meter would blind the
/// high-usage check for a whole cycle after every exchange — exactly when a wrongly fitted or
/// mis-read meter is most likely.
/// </para>
/// </remarks>
/// <param name="Reading">The dials as last known, or <see langword="null"/> if this meter has never been read here.</param>
/// <param name="ReadAt">When that was.</param>
/// <param name="TypicalDailyConsumption">What the premise normally uses in a day, where it has history.</param>
public sealed record ReadingBaseline(decimal? Reading, DateTimeOffset? ReadAt, decimal? TypicalDailyConsumption)
{
    /// <summary>No history at all — a meter fitted with no reading taken, at a premise never read.</summary>
    public static ReadingBaseline None { get; } = new(null, null, null);

    /// <summary>
    /// Derives the baseline for <paramref name="meter"/> from recent readings <b>at the premise it
    /// is fitted to</b>, newest first.
    /// </summary>
    /// <remarks>
    /// Pure: the caller does the one query, this decides what the rows mean. Which is what lets
    /// every rule above — the fitting boundary, the fallback to the installation reading, the
    /// cross-meter average — be tested exhaustively with no database.
    /// </remarks>
    /// <param name="meter">The meter about to be read. Must be fitted.</param>
    /// <param name="recentAtPremise">
    /// Recent readings at the same premise, newest first, across however many meters have stood
    /// there. Readings from other premises are ignored rather than trusted.
    /// </param>
    public static ReadingBaseline From(Meter meter, IReadOnlyList<MeterReading> recentAtPremise)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(recentAtPremise);

        if (meter.ServiceLocationId is not { } premise)
        {
            return None;
        }

        // Deduplicated: the caller fetches the exact previous reading separately from the premise
        // window, so the same row can legitimately arrive twice and must not weight the average twice.
        var atPremise = recentAtPremise
            .Where(line => line.ServiceLocationId == premise)
            .DistinctBy(line => line.Id)
            .ToList();

        // This meter, on this fitting, actually read. A missing read is skipped: it moved no dials,
        // so measuring from it would report a whole period's consumption as nothing.
        var previous = atPremise.FirstOrDefault(line =>
            line.MeterId == meter.Id
            && line.Reading is not null
            && (meter.InstalledAt is not { } fitted || line.ReadingDate >= fitted));

        // Every line that produced a daily figure, whichever meter produced it. A premise's usage
        // profile survives an exchange; a device's does not.
        var daily = atPremise
            .Select(line => line.DailyConsumption)
            .OfType<decimal>()
            .ToList();

        return new ReadingBaseline(
            previous?.Reading ?? meter.InstallationReading,
            previous?.ReadingDate ?? meter.InstalledAt,
            daily.Count is 0 ? null : decimal.Round(daily.Sum() / daily.Count, MeterReading.DecimalPlaces));
    }
}

/// <summary>
/// One reading taken off one meter, and everything derived from it at the moment it was taken.
/// </summary>
/// <remarks>
/// <para>
/// The reading register is <b>append-only</b>, the same shape as WP-1.4's stock ledger and for the
/// same reason: a figure a bill was raised from must still say what it said years later, so a
/// correction is a new reading rather than an edit to an old one.
/// </para>
/// <para>
/// <b>Consumption is stamped, not computed on read.</b> The dials, the previous dials, the units
/// between them, whether the register rolled over and why the line was flagged are all written down
/// here, exactly as WP-1.4 stamps a quantity-on-hand against every stock movement. Re-deriving them
/// later would mean re-deriving them from a meter whose register width may since have been
/// corrected, or which may by then be on somebody else's wall — and a bill nobody can reproduce is
/// a bill nobody can defend.
/// </para>
/// <para>
/// The premise is stamped for the reason <c>metering.meter_history</c> stamps it: a removal clears
/// the meter's own column, and "what did this premise use in March" has to survive the exchange.
/// It is also what lets the high-usage baseline span the meters that have stood there.
/// </para>
/// <para>
/// Deliberately <b>not</b> a navigation collection on <see cref="Meter"/>. Recording one reading
/// must not load every reading a meter has ever produced — WP-1.4's rule that a delivery of ten
/// connectors does not read five years of movements, applied to the register that will grow fastest
/// in the whole product.
/// </para>
/// </remarks>
public sealed class MeterReading
{
    /// <summary>Decimal places a dial reading and a consumption figure carry.</summary>
    public const int DecimalPlaces = Meter.DialDecimalPlaces;

    /// <summary>Total digits stored for a dial reading or a consumption figure.</summary>
    public const int Precision = Meter.DialPrecision;

    /// <summary>Longest note recorded against a reading.</summary>
    public const int NoteLength = MeterHistoryEntry.NoteLength;

    /// <summary>Longest cycle code stored, e.g. <c>2026-08</c>.</summary>
    public const int CycleCodeLength = 32;

    private MeterReading()
    {
        // EF materialisation.
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this reading. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The meter it came off.</summary>
    public Guid MeterId { get; private init; }

    /// <summary>
    /// The premise the meter was measuring when it was read. Stamped on the line rather than read
    /// off the meter, which will not still say so once the device is exchanged.
    /// </summary>
    public Guid ServiceLocationId { get; private init; }

    /// <summary>The date the dials were read — not when the row was written.</summary>
    public DateTimeOffset ReadingDate { get; private init; }

    /// <summary>What the dials read, or <see langword="null"/> for a missing read.</summary>
    public decimal? Reading { get; private init; }

    /// <summary>Where the reading came from.</summary>
    public MeterReadingSource Source { get; private init; }

    /// <summary>What this meter last read at this premise, where anything did.</summary>
    public decimal? PreviousReading { get; private init; }

    /// <summary>When that was.</summary>
    public DateTimeOffset? PreviousReadingDate { get; private init; }

    /// <summary>
    /// Units used since <see cref="PreviousReadingDate"/>. <see langword="null"/> when there is
    /// nothing to measure from — a missing read, or the first reading on a meter fitted without one.
    /// </summary>
    public decimal? Consumption { get; private init; }

    /// <summary>Whether the register wrapped past its last digit during this period.</summary>
    public bool RolledOver { get; private init; }

    /// <summary>Why this reading is held for somebody to look at, if it is.</summary>
    public ReadingExceptionCode ExceptionCode { get; private init; }

    /// <summary>The reading cycle this line belongs to, or <see langword="null"/> for a manual read.</summary>
    public string? CycleCode { get; private init; }

    /// <summary>What the reader wanted recorded against it.</summary>
    public string? Note { get; private init; }

    /// <summary>Subject id of whoever recorded it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When the reading was entered in the register.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>Days the consumption covers, where there is a period at all.</summary>
    public int? Days =>
        PreviousReadingDate is { } from ? ReadingAssessment.DaysBetween(from, ReadingDate) : null;

    /// <summary>
    /// Units per day over the period. The comparable figure: reading periods are not equal lengths,
    /// so this is what a baseline is built from and what a screen should chart.
    /// </summary>
    public decimal? DailyConsumption =>
        Consumption is { } used && Days is { } days
            ? decimal.Round(used / days, DecimalPlaces)
            : null;

    /// <summary>Whether this reading is on the exception worklist.</summary>
    public bool IsException => ExceptionCode is not ReadingExceptionCode.None;

    /// <summary>
    /// Records a reading against a fitted meter, working out consumption, rollover and the exception
    /// code from <paramref name="baseline"/>.
    /// </summary>
    /// <param name="meter">The meter read. Must be fitted — see the remarks.</param>
    /// <param name="baseline">What to measure against, from <see cref="ReadingBaseline.From"/>.</param>
    /// <param name="reading">What the dials read, or <see langword="null"/> for a missing read.</param>
    /// <param name="readingDate">When the dials were read.</param>
    /// <param name="source">Where the reading came from.</param>
    /// <param name="actor">Who recorded it.</param>
    /// <param name="now">The clock, for the row's own identity and timestamp.</param>
    /// <param name="cycleCode">The reading cycle it belongs to, for a cycle read.</param>
    /// <param name="note">What the reader wanted recorded.</param>
    /// <remarks>
    /// <b>Only a fitted meter can be read</b>, and <c>Faulty</c> counts as fitted (WP-2.1): a meter
    /// suspected of running slow is still on the wall, still holds the premise, and is read on the
    /// next visit — which is often how the fault is proved. A meter in a store measures nothing.
    /// </remarks>
    /// <exception cref="MeterWorkflowException">
    /// The meter is not fitted anywhere, or the reading is dated before the one it follows.
    /// </exception>
    /// <exception cref="MeterValidationException">
    /// The reading is negative, finer than the register stores, larger than the register can
    /// display, or dated in the future.
    /// </exception>
    public static MeterReading Record(
        Meter meter,
        ReadingBaseline baseline,
        decimal? reading,
        DateTimeOffset readingDate,
        MeterReadingSource source,
        RegistryActor actor,
        DateTimeOffset now,
        string? cycleCode = null,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(actor);

        // Every guard before the first derivation, WP-1.4's ordering rule. Nothing here mutates, but
        // a half-built reading handed back to a caller is the same lie in a different shape.
        if (!meter.IsFitted || meter.ServiceLocationId is not { } premise)
        {
            throw new MeterWorkflowException(
                $"Meter {meter.MeterNumber} is {meter.Status} and is not fitted anywhere, so there is nothing for it to have measured.");
        }

        if (!Enum.IsDefined(source))
        {
            throw new MeterValidationException($"'{source}' is not a {nameof(MeterReadingSource)} GridCore declares.");
        }

        if (readingDate > now)
        {
            // A reading dated ahead of the clock would sit at the head of the register and make
            // every reading after it look like it went backwards.
            throw new MeterValidationException($"A reading cannot be dated in the future; '{readingDate:O}' is after {now:O}.");
        }

        if (reading is { } dials)
        {
            if (decimal.Round(dials, DecimalPlaces) != dials)
            {
                // Refused rather than rounded, as every value finer than its column has been since
                // WP-1.1. CONVENTIONS.md's central rounding helper still has no home (WP-2.3 owns it).
                throw new MeterValidationException(
                    $"A meter reading is stored to {DecimalPlaces} decimal places; '{dials}' is finer than that.");
            }

            // Negative and over-capacity readings are both refused here, naming the register width.
            if (!ConsumptionCalculator.Fits(dials, meter.RegisterDigits))
            {
                throw new MeterValidationException(
                    $"Meter {meter.MeterNumber} has a {meter.RegisterDigits}-digit register and cannot read {dials}.");
            }
        }

        if (baseline.ReadAt is { } lastReadAt && readingDate < lastReadAt)
        {
            // A 409, not a 400: whether this date is legal depends on what is already in the
            // register, which no validator at the edge can see. Out-of-order readings would make
            // every consumption figure after them arithmetic on the wrong pair of dials.
            throw new MeterWorkflowException(
                $"Meter {meter.MeterNumber} was last read on {lastReadAt:yyyy-MM-dd}; a reading cannot be dated before that.");
        }

        var measured = Measure(reading, baseline, meter.RegisterDigits);

        var days = baseline.ReadAt is { } from ? ReadingAssessment.DaysBetween(from, readingDate) : 1;

        return new MeterReading
        {
            Id = Guid.CreateVersion7(now),
            MeterId = meter.Id,
            ServiceLocationId = premise,
            ReadingDate = readingDate,
            Reading = reading,
            Source = source,
            PreviousReading = reading is null ? null : baseline.Reading,
            PreviousReadingDate = reading is null ? null : baseline.ReadAt,
            Consumption = measured?.Consumption,
            RolledOver = measured?.RolledOver ?? false,
            ExceptionCode = ReadingAssessment.Classify(reading, measured?.Consumption, days, baseline.TypicalDailyConsumption),
            CycleCode = RegistryText.Clean(cycleCode, CycleCodeLength),
            Note = RegistryText.Clean(note, NoteLength),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new MeterValidationException("A meter reading must name who recorded it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }

    private static ConsumptionResult? Measure(decimal? reading, ReadingBaseline baseline, int registerDigits) =>
        reading is { } dials && baseline.Reading is { } previous
            ? ConsumptionCalculator.Between(previous, dials, registerDigits)
            : null;
}
