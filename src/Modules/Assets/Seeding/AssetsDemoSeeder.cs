using GridCore.Modules.Assets.Data;
using GridCore.Modules.Assets.Features.Assets;
using GridCore.Modules.Assets.Features.Shared;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;

namespace GridCore.Modules.Assets.Seeding;

/// <summary>
/// A small demo plant register — the transformers, poles, spans, switchgear and vehicles a
/// distribution utility on three islands would actually hold, so WP-3.x has assets to raise work
/// orders against and WP-1.5's registry screens have something to render.
/// </summary>
/// <remarks>
/// <para>
/// The places are real, and so are the coordinates: the demo utility is Rota Utilities, so its
/// plant stands on <b>Rota</b>, <b>Saipan</b> and <b>Tinian</b>, at positions that fall on those
/// islands rather than in the sea. A pin in the wrong ocean is the sort of detail a demonstration
/// audience notices immediately.
/// </para>
/// <para>
/// This seeder is independent of the Customers ones. An asset is not attached to a premise (see
/// <see cref="Asset"/>), so nothing here reads a row another module wrote and the order only has to
/// leave room for the registries that came before it.
/// </para>
/// <para>
/// Tags are assigned here rather than through <see cref="IAssetNumberGenerator"/>: the generator
/// reads the highest tag already committed, and inside the seeding transaction none of these rows
/// are visible to a query yet. Starting the series at 1 is what lets an asset registered afterwards
/// continue it correctly.
/// </para>
/// </remarks>
public sealed class AssetsDemoSeeder(AssetsDbContext database, TimeProvider clock) : IDemoSeeder
{
    /// <summary>Who the seeded plant is attributed to — a stand-in colleague, holding no permissions.</summary>
    public static DemoActor Planner { get; } = new("supervisor", "Ray Manglona (demo)");

    /// <summary>
    /// <see cref="Planner"/> as the asset history records them. Every seeded line therefore carries
    /// the <c>demo:</c> prefix, so a history entry can never be mistaken for one a real engineer made.
    /// </summary>
    private static RegistryActor Attribution { get; } = RegistryActor.Of(Planner);

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second copy of this register.</remarks>
    public string Name => "assets.registry";

    /// <inheritdoc />
    /// <remarks>After the customer registries (200, 300) and before the work orders that will be raised against this plant.</remarks>
    public int Order => 400;

    /// <inheritdoc />
    public Task SeedAsync(CancellationToken cancellationToken)
    {
        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. A step per write keeps the register list and each history
        // stable between runs.
        var now = clock.GetUtcNow();
        var step = 0;

        DateTimeOffset Next() => now.AddMilliseconds(step++);

        var ordinal = 0;

        foreach (var plant in DemoAssets)
        {
            var asset = Asset.Register(
                RegistryNumbers.Format(AssetNumbers.AssetTagPrefix, ++ordinal),
                plant.Class,
                plant.Name,
                Attribution,
                Next(),
                plant.SerialNumber,
                plant.Manufacturer,
                plant.Model,
                plant.InstalledOn,
                plant.Position,
                plant.LocationNote,
                note: plant.RegisteredNote);

            // Walked through the real transitions rather than assigned a status, so every seeded
            // asset carries the history those transitions produce — and an illegal demo lifecycle
            // fails here rather than shipping a state the machine says is unreachable.
            foreach (var (status, reason) in plant.Lifecycle)
            {
                asset.ChangeStatus(status, Attribution, Next(), reason);
            }

            // After the lifecycle, so an asset withdrawn for work carries the grading that explains
            // why it was withdrawn rather than a condition recorded before anybody looked.
            if (plant.Assessment is { } assessment)
            {
                asset.AssessCondition(assessment.Condition, Attribution, Next(), assessment.Finding);
            }

            database.Assets.Add(asset);
        }

        // No SaveChanges: the runner's unit of work saves this and the seed record in one
        // transaction, which is what makes a half-seeded demo world impossible.
        return Task.CompletedTask;
    }

    private static DateOnly Installed(int year, int month, int day) => new(year, month, day);

    private static GeoPosition At(decimal latitude, decimal longitude) => GeoPosition.Create(latitude, longitude);

    /// <summary>
    /// The demo plant. Every class appears at least once, every status is represented, and the
    /// conditions span the scale — those are the shapes a register screen and a maintenance plan
    /// have to render.
    /// </summary>
    private static IReadOnlyList<DemoAsset> DemoAssets { get; } =
    [
        // Rota — Songsong and Sinapalo.
        new(AssetClass.Substation, "Songsong Substation", At(14.140833m, 145.184722m),
            "Main intake substation serving Songsong village",
            Manufacturer: null, Model: null, SerialNumber: null,
            InstalledOn: Installed(1998, 6, 15),
            RegisteredNote: "Carried over from the legacy plant register",
            Lifecycle: [(AssetStatus.InService, "Energised, in continuous service since commissioning")],
            Assessment: new(AssetCondition.Fair, "Fence and earthing sound; switchroom roof showing corrosion")),

        new(AssetClass.Transformer, "Songsong Substation Transformer T-3", At(14.140900m, 145.184800m),
            "Bay 3, east side of the switchyard",
            "ABB", "ONAN 1500 kVA", "ABB-T-884213",
            Installed(2009, 3, 2),
            "Carried over from the legacy plant register",
            [(AssetStatus.InService, "Energised on bay 3")],
            new(AssetCondition.Good, "Oil sample clear; bushings clean")),

        new(AssetClass.Recloser, "Sinapalo Feeder Recloser R-1", At(14.157500m, 145.229400m),
            "Pole-mounted at the Sinapalo road junction",
            "Schneider Electric", "N-Series", "SE-R-55190",
            Installed(2016, 11, 8),
            "Installed with the Sinapalo feeder rebuild",
            [(AssetStatus.InService, "Commissioned and set to the feeder protection schedule")],
            new(AssetCondition.Excellent, "Operated correctly on the last two feeder faults")),

        new(AssetClass.Pole, "Pole R-0472, As Nieves Road", At(14.143611m, 145.196389m),
            "Third pole past the church, seaward side",
            SerialNumber: null, Manufacturer: null, Model: "Class 4 concrete",
            InstalledOn: Installed(2004, 9, 20),
            RegisteredNote: "Carried over from the legacy plant register",
            Lifecycle: [(AssetStatus.InService, "Carrying the As Nieves lateral")],
            Assessment: new(AssetCondition.Poor, "Spalling at the base and exposed reinforcement; scheduled for replacement")),

        new(AssetClass.ConductorSpan, "Span R-0472 to R-0473, As Nieves Road", At(14.143900m, 145.196900m),
            "Overhead lateral crossing the road",
            SerialNumber: null, Manufacturer: null, Model: "ACSR Raven 1/0",
            InstalledOn: Installed(2004, 9, 20),
            RegisteredNote: "Carried over from the legacy plant register",
            Lifecycle: [(AssetStatus.InService, "In service with the As Nieves lateral")],
            Assessment: new(AssetCondition.Fair, "Sag within tolerance; vegetation clearance wants cutting back")),

        // Saipan — Garapan and Chalan Kanoa.
        new(AssetClass.Transformer, "Garapan Beach Road Transformer T-11", At(15.212500m, 145.719400m),
            "Pad-mounted behind the hotel service yard",
            "Eaton", "Cooper VFI 750 kVA", "EAT-T-330417",
            Installed(2013, 5, 30),
            "Carried over from the legacy plant register",
            [
                (AssetStatus.InService, "Energised on the Beach Road lateral"),
                (AssetStatus.UnderMaintenance, "Withdrawn after an oil leak was reported by the hotel"),
            ],
            new(AssetCondition.Critical, "Active oil leak at the lower gasket; unit isolated pending gasket replacement")),

        new(AssetClass.Switchgear, "Chalan Kanoa Switch Cabinet SW-6", At(15.146900m, 145.705600m),
            "Kerbside cabinet at the market junction",
            "Schneider Electric", "RM6", "SE-SW-71204",
            Installed(2018, 2, 12),
            "Installed with the Chalan Kanoa reinforcement",
            [(AssetStatus.InService, "Commissioned on the market junction ring")],
            new(AssetCondition.Good, "Interlocks operate freely; cabinet dry")),

        new(AssetClass.Generator, "Saipan Standby Generator G-2", At(15.190000m, 145.750000m),
            "Standby set at the operations depot",
            "Caterpillar", "C32 900 kW", "CAT-G-902551",
            Installed(2011, 8, 4),
            "Carried over from the legacy plant register",
            [(AssetStatus.InService, "Available on standby, exercised monthly")],
            new(AssetCondition.Good, "Monthly load test passed; coolant and oil within limits")),

        // Tinian — San Jose.
        new(AssetClass.Transformer, "San Jose Village Transformer T-7", At(14.964700m, 145.622800m),
            "Pole-mounted at the corner of Broadway and 8th",
            "ABB", "ONAN 500 kVA", "ABB-T-884502",
            Installed(2007, 4, 18),
            "Carried over from the legacy plant register",
            [(AssetStatus.InService, "Energised on the San Jose lateral")],
            new(AssetCondition.Fair, "Some tank corrosion; oil sample acceptable")),

        new(AssetClass.Pole, "Pole T-0118, Broadway", At(14.964900m, 145.623100m),
            "Corner of Broadway and 8th, carrying T-7",
            SerialNumber: null, Manufacturer: null, Model: "Class 3 wood",
            InstalledOn: Installed(2007, 4, 18),
            RegisteredNote: "Carried over from the legacy plant register",
            Lifecycle: [(AssetStatus.InService, "Carrying the San Jose lateral and transformer T-7")],
            Assessment: new(AssetCondition.Fair, "Sound at the groundline; woodpecker damage at 4 m")),

        // Plant in the yard, never installed — the InStorage case a register has to show.
        new(AssetClass.Transformer, "Spare Transformer, 500 kVA", Position: null,
            "Lower Base Warehouse, bay 2",
            "ABB", "ONAN 500 kVA", "ABB-T-901337",
            InstalledOn: null,
            RegisteredNote: "Received as network spare; not yet allocated",
            Lifecycle: [],
            Assessment: new(AssetCondition.Excellent, "Factory sealed, oil test certificate on file")),

        new(AssetClass.Vehicle, "Bucket Truck BT-2", At(14.141100m, 145.185500m),
            "Operations depot, Songsong",
            "Altec", "AT40-G", "ALT-V-118902",
            Installed(2019, 1, 22),
            "Fleet asset, tracked with the plant register",
            [
                (AssetStatus.InService, "In service with the Rota line crew"),
                (AssetStatus.UnderMaintenance, "Off the road for boom hydraulic service"),
            ],
            new(AssetCondition.Fair, "Boom hydraulics weeping at the upper cylinder; parts on order")),

        // Retired plant — terminal, and still on the register because the jobs and costs booked
        // against it have to stay readable.
        new(AssetClass.Transformer, "Songsong Transformer T-1 (retired)", At(14.140700m, 145.184600m),
            "Removed from bay 1; replaced by T-3",
            "General Electric", "ONAN 750 kVA", "GE-T-114220",
            Installed(1998, 6, 15),
            "Carried over from the legacy plant register",
            [
                (AssetStatus.InService, "Original bay 1 unit"),
                (AssetStatus.UnderMaintenance, "Withdrawn after winding failure"),
                (AssetStatus.Retired, "Beyond economic repair; scrapped and disposal recorded"),
            ],
            null),
    ];

    /// <param name="Condition">How the inspector graded it.</param>
    /// <param name="Finding">What they found.</param>
    private sealed record DemoAssessment(AssetCondition Condition, string Finding);

    /// <param name="Class">What kind of plant it is.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Position">Where it stands, where a position is known.</param>
    /// <param name="LocationNote">Where it is, in a crew's words.</param>
    /// <param name="Manufacturer">Who made it.</param>
    /// <param name="Model">Their model designation.</param>
    /// <param name="SerialNumber">The manufacturer's serial number, where the plant carries one.</param>
    /// <param name="InstalledOn">When it was installed.</param>
    /// <param name="RegisteredNote">Why it is on the register, for the opening history line.</param>
    /// <param name="Lifecycle">The transitions to walk it through, in order.</param>
    /// <param name="Assessment">The grading to record afterwards, where one has been done.</param>
    private sealed record DemoAsset(
        AssetClass Class,
        string Name,
        GeoPosition? Position,
        string LocationNote,
        string? Manufacturer,
        string? Model,
        string? SerialNumber,
        DateOnly? InstalledOn,
        string RegisteredNote,
        IReadOnlyList<(AssetStatus Status, string Reason)> Lifecycle,
        DemoAssessment? Assessment);
}
