namespace GridCore.Modules.Metering.Features.Meters;

/// <summary>
/// Where a meter stands in its working life. Half of this lifecycle is about <i>where the device
/// is</i>, which is why <see cref="MeterTransitions.IsFitted"/> exists: a meter is either on a
/// premise measuring supply or it is not, and no status may be reached that disagrees with the
/// premise recorded against it.
/// </summary>
public enum MeterStatus
{
    /// <summary>
    /// Held in a store, fitted nowhere. Where a newly registered meter starts.
    /// </summary>
    /// <remarks>
    /// Named for the store rather than "in stock", deliberately. Inventory already uses that phrase
    /// for a catalogue line that is stocked and above its reorder level — a different fact about a
    /// different kind of thing — and the two would collide in the shared status map the whole UI
    /// renders through, which is the same trap the "Low stock" pill avoids by never being "Low".
    /// </remarks>
    InStore = 1,

    /// <summary>Fitted at a premise and measuring supply. The only status a bill may be raised from.</summary>
    Installed = 2,

    /// <summary>
    /// Still fitted, but its readings are not to be trusted — stopped, tampered with, or failing an
    /// accuracy check. Deliberately a status rather than a removal: the meter is physically still on
    /// the wall, still holds the premise, and a crew has to go and exchange it.
    /// </summary>
    Faulty = 3,

    /// <summary>
    /// Taken off a premise and back in the yard, awaiting test. Distinct from
    /// <see cref="InStore"/> because a meter that has just come off a customer's wall must not be
    /// handed straight back out to the next job before somebody has checked it.
    /// </summary>
    Removed = 4,

    /// <summary>
    /// Finished — scrapped or written off. Terminal: readings, bills and disputes all point at the
    /// meter that produced them, so the record stays readable for good.
    /// </summary>
    Retired = 5,
}

/// <summary>
/// The meter state machine, in one place. Kept out of <see cref="Meter"/> so a UI can ask what is
/// legal without holding an entity, matching <c>CustomerTransitions</c>,
/// <c>ServiceAccountTransitions</c> and <c>AssetTransitions</c>.
/// </summary>
/// <remarks>
/// Two of these moves cross the fitted boundary and are therefore <b>not</b> reachable through the
/// status endpoint: <see cref="MeterStatus.InStore"/> → <see cref="MeterStatus.Installed"/> is what
/// <c>POST /assign</c> does, and the moves to <see cref="MeterStatus.Removed"/> are what
/// <c>POST /remove</c> does. Fitting and unfitting a meter are physical acts that also decide which
/// premise the meter holds; letting a bare status change do either would leave a meter marked
/// installed at no premise, or a premise still holding a meter nobody fitted.
/// </remarks>
public static class MeterTransitions
{
    private static readonly Dictionary<MeterStatus, MeterStatus[]> Allowed = new()
    {
        // Straight to Retired covers a meter condemned in its box — a bad batch, or one dropped in
        // the yard. It never went on a wall, so there is nothing to remove it from.
        [MeterStatus.InStore] = [MeterStatus.Installed, MeterStatus.Retired],

        [MeterStatus.Installed] = [MeterStatus.Faulty, MeterStatus.Removed],

        // Back to Installed is the meter that passed its accuracy check on site: nothing was wrong
        // with it, so it keeps the premise it is already on rather than being exchanged.
        [MeterStatus.Faulty] = [MeterStatus.Installed, MeterStatus.Removed],

        // No route back to a premise: a meter goes out again only after it has been checked in as
        // stock, which is the whole reason Removed and InStore are different statuses.
        [MeterStatus.Removed] = [MeterStatus.InStore, MeterStatus.Retired],

        [MeterStatus.Retired] = [],
    };

    /// <summary>Where a new meter starts, unless the caller says otherwise.</summary>
    public const MeterStatus Initial = MeterStatus.InStore;

    /// <summary>The statuses a meter in <paramref name="status"/> may move to.</summary>
    public static IReadOnlyList<MeterStatus> AllowedFrom(MeterStatus status) =>
        Allowed.TryGetValue(status, out var next) ? next : [];

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> is a legal move.</summary>
    public static bool IsAllowed(MeterStatus from, MeterStatus to) => AllowedFrom(from).Contains(to);

    /// <summary>
    /// Whether a meter in <paramref name="status"/> is on a premise. The invariant the whole module
    /// hangs on: a fitted meter holds a service location and an unfitted one holds none, so "which
    /// meter measures this premise" always has exactly one answer or none.
    /// </summary>
    public static bool IsFitted(MeterStatus status) => status is MeterStatus.Installed or MeterStatus.Faulty;

    /// <summary>
    /// Whether this move fits or unfits the meter, and so must go through <c>assign</c> or
    /// <c>remove</c> rather than a bare status change.
    /// </summary>
    public static bool ChangesFitting(MeterStatus from, MeterStatus to) => IsFitted(from) != IsFitted(to);
}
