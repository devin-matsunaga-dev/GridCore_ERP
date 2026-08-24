namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>
/// How good a state an asset is in, as an inspector graded it. Deliberately <b>not</b> a state
/// machine: a condition is an assessment, and the next inspection may find it better (it was
/// repaired) or worse (it weathered a typhoon) with nothing illegal about either direction. That is
/// the difference between this and <see cref="AssetStatus"/>, which is a lifecycle and is guarded.
/// </summary>
public enum AssetCondition
{
    /// <summary>Never assessed. Where an inherited or newly received asset starts, and an honest answer until somebody looks at it.</summary>
    Unknown = 1,

    /// <summary>As new. No defects.</summary>
    Excellent = 2,

    /// <summary>Sound. Normal wear for its age.</summary>
    Good = 3,

    /// <summary>Serviceable, with defects worth planning work against.</summary>
    Fair = 4,

    /// <summary>Deteriorated. Should be scheduled for replacement or major work.</summary>
    Poor = 5,

    /// <summary>At risk of failure. Wants attention now.</summary>
    Critical = 6,
}
