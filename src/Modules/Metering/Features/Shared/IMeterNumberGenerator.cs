using GridCore.Modules.Metering.Data;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Metering.Features.Shared;

/// <summary>
/// The prefix this module's registry numbers are issued under. The <i>shape</i> of a number is the
/// platform's (<see cref="RegistryNumbers"/>); what letters a meter number carries is the Metering
/// module's own business.
/// </summary>
public static class MeterNumbers
{
    /// <summary>
    /// Prefix of a meter number, e.g. <c>MTR-000001</c>. Three letters, like <c>AST-</c> and
    /// <c>ITM-</c> and for the same reason: it is read aloud in the field and quoted on a bill, and
    /// <c>M-000001</c> would be one character away from too many other things.
    /// </summary>
    public const string MeterNumberPrefix = "MTR-";
}

/// <summary>
/// Issues the next meter number. A seam, so the numbering scheme is one registration away from
/// changing — a utility migrating from a legacy register usually has to keep its own.
/// </summary>
public interface IMeterNumberGenerator
{
    /// <summary>The next unused meter number.</summary>
    Task<string> NextMeterNumberAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Continues the meter series from the highest number already issued, inside the caller's
/// transaction.
/// </summary>
/// <remarks>
/// One <see cref="RegistryNumberSeries.NextAsync"/> over this module's own column; the race with a
/// concurrent registration and the ordering trade it depends on are documented there, because every
/// registry shares them.
/// </remarks>
public sealed class SequentialMeterNumberGenerator(MeteringDbContext database) : IMeterNumberGenerator
{
    /// <inheritdoc />
    public Task<string> NextMeterNumberAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            MeterNumbers.MeterNumberPrefix,
            database.Meters
                .Where(meter => meter.MeterNumber.StartsWith(MeterNumbers.MeterNumberPrefix))
                .OrderByDescending(meter => meter.MeterNumber)
                .Select(meter => meter.MeterNumber),
            cancellationToken);
}
