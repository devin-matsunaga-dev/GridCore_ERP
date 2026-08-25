using GridCore.Contracts.Directories;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;

namespace GridCore.Modules.Metering.Seeding;

/// <summary>
/// The meters on the demo utility's premises: one on most of the seeded service locations, one in
/// stock, one taken off a premise somebody moved out of, and one condemned before it ever went on a
/// wall. So the register opens with every status on screen, and WP-2.2 has meters to read.
/// </summary>
/// <remarks>
/// <para>
/// The premises come from <see cref="IServiceLocationDirectory"/>, not from a query — this module
/// may not read the customers schema, and the directory is the seam that exists so it does not have
/// to (ARCHITECTURE.md's boundary rule). A seeder of its own, running after the Customers ones in
/// its own unit of work, is what lets it see rows they have already committed.
/// </para>
/// <para>
/// One premise here — <c>L-000010</c> — is metered with <b>no service account open on it</b>, and
/// that is the point rather than an oversight: a meter is fitted to a place, so a new build can be
/// metered before anybody is billed there. It is the visible half of the owner's call that "one
/// meter per premise" and "one open account per premise" are separate rules that do not know about
/// each other. <c>L-000007</c>, whose account was closed when the tenant moved out, is left
/// unmetered for the same reason read from the other side.
/// </para>
/// <para>
/// Numbers are assigned here rather than through <see cref="IMeterNumberGenerator"/>: the generator
/// reads the highest number already committed, and inside the seeding transaction none of these
/// rows are visible to a query yet. Starting the series at 1 is what lets a meter registered
/// afterwards continue it.
/// </para>
/// </remarks>
public sealed class MetersDemoSeeder(
    MeteringDbContext database,
    IServiceLocationDirectory serviceLocations,
    TimeProvider clock) : IDemoSeeder
{
    /// <summary>Who the seeded meters are attributed to — a stand-in colleague, holding no permissions.</summary>
    public static DemoActor Fitter { get; } = new("technician", "Jesse Atalig (demo)");

    /// <summary>
    /// <see cref="Fitter"/> as the meter history records them. Every seeded line therefore carries
    /// the <c>demo:</c> prefix, so a history entry can never be mistaken for one a real crew made.
    /// </summary>
    private static RegistryActor Attribution { get; } = RegistryActor.Of(Fitter);

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second set of meters.</remarks>
    public string Name => "metering.meters";

    /// <inheritdoc />
    /// <remarks>After the customer registries (200, 300), whose premises this one fits meters to.</remarks>
    public int Order => 600;

    /// <inheritdoc />
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var premises = (await serviceLocations
                .ListServiceableAsync(ServiceLocationDirectoryPageSize, cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(premise => premise.LocationCode, premise => premise.Id, StringComparer.Ordinal);

        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. A step per write keeps the register list and each history
        // stable between runs.
        // Fitted well over a year ago, not this second. A demo world whose meters were all installed
        // at startup could carry no reading history at all — MeterReading.Record refuses a reading
        // dated before the meter went on the wall — and a register whose every device was fitted in
        // the same millisecond reads as seed data rather than as a utility.
        var now = clock.GetUtcNow().AddDays(-FittedDaysAgo);
        var step = 0;

        DateTimeOffset Next() => now.AddMilliseconds(step++);

        var ordinal = 0;

        foreach (var device in DemoMeters)
        {
            var meter = Meter.Register(
                RegistryNumbers.Format(MeterNumbers.MeterNumberPrefix, ++ordinal),
                device.SerialNumber,
                device.Type,
                Attribution,
                Next(),
                device.RegisterDigits,
                device.Manufacturer,
                device.Model,
                device.RegisteredNote);

            if (device.LocationCode is { } code)
            {
                // A demo world that quietly leaves half its premises unmetered because a place name
                // was edited is worse than one that refuses to start and says which row is missing.
                if (!premises.TryGetValue(code, out var premiseId))
                {
                    throw new InvalidOperationException(
                        $"Demo service location '{code}' was not seeded or is not in service; CustomersDemoSeeder and this seeder have drifted apart.");
                }

                // Walked through the real aggregate rather than assigned a status, so every seeded
                // meter carries the history the transitions produce — and an illegal demo lifecycle
                // fails here rather than shipping a state the machine says is unreachable.
                meter.InstallAt(premiseId, Attribution, Next(), device.InstallationReading, device.InstalledNote);
            }

            if (device.Removed is { } removal)
            {
                meter.Remove(Attribution, Next(), removal);
            }

            if (device.EndStatus is { } status)
            {
                meter.ChangeStatus(status, Attribution, Next(), device.EndStatusReason);
            }

            database.Meters.Add(meter);
        }

        // No SaveChanges: the runner's unit of work saves this and the seed record in one
        // transaction, which is what makes a half-seeded demo world impossible.
    }

    /// <summary>
    /// How long ago the demo meters were registered and fitted. Long enough for
    /// <see cref="MeterReadingsDemoSeeder"/> to lay a year of reading cycles after it.
    /// </summary>
    private const int FittedDaysAgo = 400;

    /// <summary>
    /// How many premises to ask the directory for. The demo world has ten; the cap is the
    /// directory's own page size, so this can never quietly read a partial register.
    /// </summary>
    private const int ServiceLocationDirectoryPageSize = 200;

    /// <summary>
    /// The demo register: every meter type, every status, and the two premises that prove metering
    /// and billing are separate questions.
    /// </summary>
    private static IReadOnlyList<DemoMeter> DemoMeters { get; } =
    [
        new("SEN-4471102", MeterType.SinglePhase, "Sensus", "iConA", LocationCode: "L-000001",
            InstallationReading: 0m, InstalledNote: "New connection, meter set on the north wall"),

        new("SEN-4471188", MeterType.SinglePhase, "Sensus", "iConA", LocationCode: "L-000002",
            InstallationReading: 14_820.500m, InstalledNote: "Transfer of service, dials read in with the outgoing occupant"),

        new("ITR-9930041", MeterType.ThreePhase, "Itron", "Centron II", LocationCode: "L-000003",
            InstallationReading: 61_204.000m, InstalledNote: "Three-phase supply, chiller load"),

        new("ITR-9930112", MeterType.CurrentTransformer, "Itron", "Centron II", RegisterDigits: 6, LocationCode: "L-000004",
            InstallationReading: 388_115.250m, InstalledNote: "CT-metered intake, ratio witnessed by the inspector"),

        // Fitted and then flagged faulty: still on the wall, still holding the premise, and waiting
        // for a crew to exchange it. The account at this premise is Disconnected, which is a
        // different fact about a different thing.
        new("SEN-4470096", MeterType.SinglePhase, "Sensus", "iConA", LocationCode: "L-000005",
            InstallationReading: 22_101.000m, InstalledNote: "New connection, meter at the property line",
            EndStatus: MeterStatus.Faulty, EndStatusReason: "Dials stopped between reads, exchange raised"),

        new("LAG-2210773", MeterType.Demand, "Landis+Gyr", "E650", RegisterDigits: 7, LocationCode: "L-000006",
            InstallationReading: 1_204_880.750m, InstalledNote: "Hotel main intake, demand register commissioned"),

        new("ITR-9930255", MeterType.ThreePhase, "Itron", "Centron II", RegisterDigits: 6, LocationCode: "L-000009",
            InstallationReading: 402_991.000m, InstalledNote: "Cold store, three-phase supply"),

        // Metered with no account open on it. A new build's supply is live and measured before
        // anybody is billed there — the two rules are independent.
        new("SEN-4471290", MeterType.SinglePhase, "Sensus", "iConA", LocationCode: "L-000010",
            InstallationReading: 0m, InstalledNote: "Long service drop energised, premise not yet let"),

        // Never left the store.
        new("SEN-4471301", MeterType.SinglePhase, "Sensus", "iConA",
            RegisteredNote: "Received from the September delivery"),

        // Fitted, then taken off when the tenant moved out — so L-000007 is free for the next
        // occupant, and the history is where "what measured that premise" is now answered.
        new("SEN-4470512", MeterType.SinglePhase, "Sensus", "iConA", LocationCode: "L-000007",
            InstallationReading: 9_640.250m, InstalledNote: "Tenant's supply energised",
            Removed: "Tenant moved out, final reading taken and meter withdrawn"),

        // Condemned in its box, so it never went on a wall and there is nothing to remove it from.
        new("SEN-4471344", MeterType.SinglePhase, "Sensus", "iConA",
            RegisteredNote: "Received from the September delivery",
            EndStatus: MeterStatus.Retired, EndStatusReason: "Failed bench accuracy check on arrival, scrapped"),
    ];

    /// <param name="SerialNumber">The manufacturer's serial number stamped on the device.</param>
    /// <param name="Type">How it measures the service.</param>
    /// <param name="Manufacturer">Who made it.</param>
    /// <param name="Model">Their model designation.</param>
    /// <param name="RegisterDigits">
    /// How many whole digits its register carries. Domestic meters keep the default five; the
    /// commercial and CT-metered intakes below carry more, because their installation readings are
    /// larger than a five-digit register could display at all.
    /// </param>
    /// <param name="LocationCode">The seeded premise it is fitted at, if any.</param>
    /// <param name="InstallationReading">What the dials read as it went on.</param>
    /// <param name="InstalledNote">Why it was fitted.</param>
    /// <param name="RegisteredNote">Why it was registered, for a meter that stays in the store.</param>
    /// <param name="Removed">Why it came off, for a meter that was later withdrawn.</param>
    /// <param name="EndStatus">A final lifecycle move that leaves the meter where it is.</param>
    /// <param name="EndStatusReason">Why that move was made.</param>
    private sealed record DemoMeter(
        string SerialNumber,
        MeterType Type,
        string Manufacturer,
        string Model,
        int RegisterDigits = Meter.DefaultRegisterDigits,
        string? LocationCode = null,
        decimal? InstallationReading = null,
        string? InstalledNote = null,
        string? RegisteredNote = null,
        string? Removed = null,
        MeterStatus? EndStatus = null,
        string? EndStatusReason = null);
}
