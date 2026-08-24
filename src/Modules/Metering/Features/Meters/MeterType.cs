namespace GridCore.Modules.Metering.Features.Meters;

/// <summary>
/// What kind of meter a device is — how it measures the service, not what it measures. GridCore's
/// demonstration utility distributes electricity, so these are the metering arrangements a
/// distribution utility actually holds in its store.
/// </summary>
/// <remarks>
/// The type is a property of the <i>device</i>, not of the premise it is fitted at: a house
/// upgraded to a three-phase supply gets a different meter, not a re-typed one. Correcting a type
/// is a details edit; changing the metering arrangement at a premise is a removal and an
/// installation.
/// </remarks>
public enum MeterType
{
    /// <summary>The ordinary domestic meter on a single-phase service.</summary>
    SinglePhase = 1,

    /// <summary>A three-phase meter, for a commercial or small industrial service.</summary>
    ThreePhase = 2,

    /// <summary>
    /// A meter fed through current transformers, for a service too large to measure directly. What
    /// it reads is scaled by the transformer ratio — a distinction WP-2.2's consumption maths has
    /// to respect, and the reason the arrangement is recorded on the register rather than inferred.
    /// </summary>
    CurrentTransformer = 3,

    /// <summary>
    /// A meter that records peak demand alongside energy. Registered as its own type because the
    /// utility maintains and reads it differently; WP-2.3's tariffs bill energy only, so nothing
    /// downstream treats it specially yet.
    /// </summary>
    Demand = 4,
}
