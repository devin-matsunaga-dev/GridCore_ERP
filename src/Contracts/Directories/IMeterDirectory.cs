namespace GridCore.Contracts.Directories;

/// <summary>
/// A meter as another module sees it: what it is called, what it is, and which premise it is
/// measuring. Nothing about its dials, its history or its register width.
/// </summary>
/// <remarks>
/// A DTO, never the entity — the rule <see cref="ServiceLocationSummary"/> and
/// <see cref="ServiceAccountSummary"/> follow, and for the same reason: <c>Meter</c> is an EF type
/// in the Metering schema with a history collection hanging off it, and handing it across the
/// boundary would let a caller walk into tables it must never read.
/// </remarks>
/// <param name="Id">Identifier of the meter, in the Metering schema.</param>
/// <param name="MeterNumber">The number the utility knows it by, e.g. <c>MTR-000001</c>.</param>
/// <param name="SerialNumber">The manufacturer's serial stamped on the device.</param>
/// <param name="Type">How it measures the service, by name — Contracts takes no dependency on the module's enum.</param>
/// <param name="Status">Where it stands in its working life, by name.</param>
/// <param name="ServiceLocationId">
/// The premise it is measuring, or <see langword="null"/> when it is not fitted anywhere. A meter
/// is fitted to a premise and never to an account (WP-2.1), so this is the only address a caller
/// gets, and resolving it to whoever is served there is the caller's own step.
/// </param>
/// <param name="IsFitted">Whether it is on a wall at all.</param>
public sealed record MeterSummary(
    Guid Id,
    string MeterNumber,
    string SerialNumber,
    string Type,
    string Status,
    Guid? ServiceLocationId,
    bool IsFitted);

/// <summary>
/// Read access to the meter register for modules that are not Metering — the fifth cross-module
/// read seam in GridCore.
/// </summary>
/// <remarks>
/// <para>
/// Shaped exactly like <see cref="IServiceLocationDirectory"/> and
/// <see cref="IServiceAccountDirectory"/>: the interface lives in <c>Contracts</c>, the Metering
/// module registers the implementation, and a consumer takes the dependency without ever learning
/// that a <c>metering</c> schema exists.
/// </para>
/// <para>
/// Customers (WP-2.9) is the first consumer, and needs it for a resolution the CSR search box
/// promises: a caller quotes the number off the meter on their wall, and the rep has to arrive at
/// the customer. A meter names a premise, Customers knows who is served at a premise, and this seam
/// is the first of those two hops. Note which module owns which half — Metering could not answer
/// "whose meter is this" without reading the customers schema, which is exactly the thing neither
/// module may do.
/// </para>
/// <para>
/// It is deliberately <b>not</b> <see cref="IMeterReadingDirectory"/>'s job. That seam hands over
/// <i>readings</i> so a bill can be raised from them; this one hands over the <i>device</i> so it
/// can be found by name. A reading is not a good way to look up a meter that has never been read.
/// </para>
/// <para>
/// Read-only, for <see cref="IServiceLocationDirectory"/>'s reason: registering, fitting, removing
/// and retiring a meter stay behind <c>IMeterService</c> inside Metering. A second module that
/// could move a meter is a second module that owns the register.
/// </para>
/// </remarks>
public interface IMeterDirectory
{
    /// <summary>
    /// The meter carrying exactly <paramref name="meterNumber"/>, or <see langword="null"/> when
    /// there is none. Matched case-insensitively against the whole number, so it is an index seek on
    /// <c>ux_meters_meter_number</c> — the shape a lookup of a quoted number should have, and the
    /// reason this is a method of its own rather than a special case of
    /// <see cref="SearchByNumberAsync"/>.
    /// </summary>
    Task<MeterSummary?> FindByNumberAsync(string meterNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Meters whose number <i>contains</i> <paramref name="term"/>, case-insensitively, at most
    /// <paramref name="limit"/> of them. What answers a half-remembered number read off a device in
    /// somebody's yard.
    /// </summary>
    /// <remarks>
    /// Unordered beyond the register's own key order, and capped: this is a candidate set for a
    /// caller that is going to rank it, not an answer. Exactness is
    /// <see cref="FindByNumberAsync"/>'s question.
    /// </remarks>
    Task<IReadOnlyList<MeterSummary>> SearchByNumberAsync(
        string term,
        int limit,
        CancellationToken cancellationToken = default);
}
