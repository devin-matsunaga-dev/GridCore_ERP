using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Applications;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Seeding;

/// <summary>
/// Puts two applications in the review queue, so the desk WP-2.18 built opens with work on it
/// rather than as an empty table that makes a feature which exists look like one that does not —
/// the call <see cref="AccountTransitionsDemoSeeder"/> and <see cref="CustomerNotesDemoSeeder"/>
/// both made.
/// </summary>
/// <remarks>
/// <para>
/// <b>TWO, both still open, and neither carrying a document.</b> That is not a thin demo, it is the
/// honest one. A seeded document would mean inventing a scanned lease, writing it into MinIO from a
/// startup path, and then having the demo world refuse to start on the morning the object store is
/// slow — a cost paid by every developer for a file nobody looks at. What the two rows demonstrate
/// instead is the rule this package exists for: an application with an unsatisfied checklist
/// <i>cannot be approved</i>, and the way to see the approval work is to upload something, which is
/// one drag onto the screen.
/// </para>
/// <para>
/// <b>And no approved one.</b> Approving writes a service account, which would mean this seeder
/// minting an account number behind <see cref="ServiceAccountsDemoSeeder"/>'s back and the two
/// drifting the first time either list changes. A demo that wants an approved application gets one
/// by approving one — which takes two clicks and is the thing worth watching.
/// </para>
/// <para>
/// The premises are the two the demo world leaves free: <c>L-000007</c>, released when the tenant's
/// account was closed, and <c>L-000010</c>, which has never had an account. Anything else would
/// collide with <c>ux_service_applications_open_premise</c> — deliberately, since applying for a
/// supply somebody already takes is exactly what that index refuses.
/// </para>
/// </remarks>
public sealed class ServiceApplicationsDemoSeeder(CustomersDbContext database, TimeProvider clock) : IDemoSeeder
{
    /// <summary>Who the seeded applications are attributed to — the same stand-in colleague the accounts carry.</summary>
    private static RegistryActor Attribution { get; } = RegistryActor.Of(ServiceAccountsDemoSeeder.Agent);

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second set of applications.</remarks>
    public string Name => "customers.service-applications";

    /// <inheritdoc />
    /// <remarks>After <see cref="CustomerNotesDemoSeeder"/> (400), and after the accounts whose premises these avoid.</remarks>
    public int Order => 450;

    /// <inheritdoc />
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var customers = await database.Customers
            .ToDictionaryAsync(customer => customer.AccountNumber, customer => customer, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var locations = await database.ServiceLocations
            .ToDictionaryAsync(location => location.LocationCode, location => location.Id, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. A step per write keeps the queue's ordering stable.
        var now = clock.GetUtcNow();
        var step = 0;

        DateTimeOffset Next() => now.AddMilliseconds(step++);

        var ordinal = 0;

        foreach (var seed in Applications)
        {
            // A demo world that quietly skips its applications because a customer was renamed is
            // worse than one that refuses to start and says which row is missing.
            if (!customers.TryGetValue(seed.CustomerAccountNumber, out var customer))
            {
                throw new InvalidOperationException(
                    $"Demo customer '{seed.CustomerAccountNumber}' was not seeded; "
                    + $"{nameof(CustomersDemoSeeder)} and this seeder have drifted apart.");
            }

            if (!locations.TryGetValue(seed.LocationCode, out var locationId))
            {
                throw new InvalidOperationException(
                    $"Demo service location '{seed.LocationCode}' was not seeded; "
                    + $"{nameof(CustomersDemoSeeder)} and this seeder have drifted apart.");
            }

            var application = ServiceApplication.Submit(
                RegistryNumbers.Format(CustomerNumbers.ServiceApplicationPrefix, ++ordinal),
                customer,
                locationId,
                ServiceType.Electricity,
                seed.RequestedOn(now),
                seed.Notes,
                replacesApplicationId: null,
                Attribution,
                Next());

            // Walked through the real transition rather than assigned a status, so a seeded
            // application can never hold a state the machine says is unreachable.
            if (seed.PickedUp)
            {
                application.StartReview(Attribution, Next());
            }

            database.ServiceApplications.Add(application);
        }

        // No SaveChanges: the runner's unit of work saves this and the seed record in one
        // transaction, which is what makes a half-seeded demo world impossible.
    }

    /// <summary>
    /// The two applications the demo world opens with: one still in the queue, one a reviewer has
    /// picked up — which are the only two statuses a desk ever has to triage between.
    /// </summary>
    private static IReadOnlyList<DemoApplication> Applications { get; } =
    [
        // Residential: photo ID and proof of occupancy. Filed against the premise the Taisacan
        // household released when their tenant moved out, which is the ordinary way a supply comes
        // back onto the desk.
        new(
            "C-000002",
            "L-000007",
            PickedUp: false,
            DaysAhead: 7,
            "Tenant moved out; taking the supply back in the household's own name."),

        // Commercial: the same two documents AND a business licence, so the queue shows two
        // checklists of different lengths side by side — which is the whole reason the application
        // type exists.
        new(
            "C-000008",
            "L-000010",
            PickedUp: true,
            DaysAhead: 21,
            "Second cold store on Marpo Heights Road; three-phase supply requested."),
    ];

    /// <param name="CustomerAccountNumber">Which seeded customer is applying.</param>
    /// <param name="LocationCode">Which seeded premise — one of the two the demo world leaves free.</param>
    /// <param name="PickedUp">Whether a reviewer has taken it out of the queue.</param>
    /// <param name="DaysAhead">How far ahead supply is wanted, from the seeding instant.</param>
    /// <param name="Notes">What the desk wrote when it was filed.</param>
    private sealed record DemoApplication(
        string CustomerAccountNumber,
        string LocationCode,
        bool PickedUp,
        int DaysAhead,
        string Notes)
    {
        /// <summary>The day supply is wanted, relative to when the demo world was seeded.</summary>
        public DateOnly RequestedOn(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime).AddDays(DaysAhead);
    }
}
