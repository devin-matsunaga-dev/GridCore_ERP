using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Seeding;

/// <summary>
/// Joins the seeded customers to the seeded premises and walks the accounts through their
/// lifecycle, so the demo world opens with all four statuses on screen rather than eight identical
/// pills — and so WP-2.x has accounts with a real service history to bill and read meters against.
/// </summary>
/// <remarks>
/// <para>
/// A seeder of its own rather than more rows in <see cref="CustomersDemoSeeder"/>: a seeder's
/// <c>Name</c> is its dedupe key, so extending one that has already run on a developer's database
/// would seed nothing. It also runs in its own transaction, which is what lets it query the
/// customers and premises the previous seeder committed — inside one transaction those rows are
/// not yet visible to a query.
/// </para>
/// <para>
/// Numbers are assigned here rather than through <see cref="IRegistryNumberGenerator"/>, for the
/// same reason: starting the series at 1 is what lets an account opened afterwards continue it.
/// </para>
/// </remarks>
public sealed class ServiceAccountsDemoSeeder(CustomersDbContext database, TimeProvider clock) : IDemoSeeder
{
    /// <summary>Who the seeded accounts are attributed to — a stand-in colleague, holding no permissions.</summary>
    public static DemoActor Agent { get; } = new("customer-service", "Ana Cruz (demo)");

    /// <summary>
    /// <see cref="Agent"/> as the account history records them. Every seeded line therefore carries
    /// the <c>demo:</c> prefix, so a history entry can never be mistaken for one a real agent made.
    /// </summary>
    private static RegistryActor Attribution { get; } = RegistryActor.Of(Agent);

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second set of accounts.</remarks>
    public string Name => "customers.service-accounts";

    /// <inheritdoc />
    /// <remarks>After <see cref="CustomersDemoSeeder"/> (200), whose rows this one reads.</remarks>
    public int Order => 300;

    /// <inheritdoc />
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var customers = await database.Customers
            .ToDictionaryAsync(customer => customer.AccountNumber, customer => customer.Id, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var locations = await database.ServiceLocations
            .ToDictionaryAsync(location => location.LocationCode, location => location.Id, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. A step per write keeps the list and each history stable.
        var now = clock.GetUtcNow();
        var step = 0;

        DateTimeOffset Next() => now.AddMilliseconds(step++);

        var ordinal = 0;

        foreach (var pairing in Pairings)
        {
            // A demo world that quietly skips half its accounts because a place name was edited is
            // worse than one that refuses to start and says which row is missing.
            if (!customers.TryGetValue(pairing.CustomerAccountNumber, out var customerId))
            {
                throw new InvalidOperationException(
                    $"Demo customer '{pairing.CustomerAccountNumber}' was not seeded; {nameof(CustomersDemoSeeder)} and this seeder have drifted apart.");
            }

            if (!locations.TryGetValue(pairing.LocationCode, out var locationId))
            {
                throw new InvalidOperationException(
                    $"Demo service location '{pairing.LocationCode}' was not seeded; {nameof(CustomersDemoSeeder)} and this seeder have drifted apart.");
            }

            var account = ServiceAccount.Open(
                RegistryNumbers.Format(CustomerNumbers.ServiceAccountPrefix, ++ordinal),
                customerId,
                locationId,

                // Every seeded account is an electric one: the demo world is a distribution utility
                // with meters and readings behind it, and a water or wastewater account here would be
                // a premise with a deposit and no bill anybody could raise against it.
                ServiceType.Electricity,
                Attribution,
                Next(),
                pairing.OpenedReason);

            // Walked through the real transitions rather than assigned a status, so every seeded
            // account carries the history those transitions produce — and an illegal demo pairing
            // fails here rather than shipping a state the machine says is unreachable.
            foreach (var (status, reason) in pairing.Lifecycle)
            {
                switch (status)
                {
                    case ServiceAccountStatus.Active:
                        account.StartService(Attribution, Next(), reason);
                        break;
                    case ServiceAccountStatus.Disconnected:
                        account.StopService(Attribution, Next(), reason);
                        break;
                    case ServiceAccountStatus.Closed:
                        account.Close(Attribution, Next(), reason);
                        break;
                    default:
                        throw new InvalidOperationException($"A demo account cannot be walked to {status}.");
                }
            }

            database.ServiceAccounts.Add(account);
        }

        // No SaveChanges: the runner's unit of work saves this and the seed record in one
        // transaction, which is what makes a half-seeded demo world impossible.
    }

    /// <summary>
    /// Which customer is served at which premise, and how each account got to where it is. One
    /// account of every status, one customer holding two accounts, and one premise released by a
    /// closure — the shapes a registry screen has to render.
    /// </summary>
    private static IReadOnlyList<DemoPairing> Pairings { get; } =
    [
        new("C-000001", "L-000001", "New connection requested at the counter",
            [(ServiceAccountStatus.Active, "Connection completed, meter energised")]),

        new("C-000002", "L-000002", "Transfer of service from the previous occupant",
            [(ServiceAccountStatus.Active, "Connection completed, meter energised")]),

        new("C-000003", "L-000003", "Commercial connection, three-phase supply",
            [(ServiceAccountStatus.Active, "Connection completed after load inspection")]),

        new("C-000004", "L-000004", "Institutional connection",
            [(ServiceAccountStatus.Active, "Connection completed, standby generator witnessed")]),

        // Served, then cut for arrears — the customer record is Suspended and the account is
        // Disconnected, which is exactly the pair of statuses that has to be readable separately.
        new("C-000005", "L-000005", "New connection requested by telephone",
            [
                (ServiceAccountStatus.Active, "Connection completed, meter energised"),
                (ServiceAccountStatus.Disconnected, "Disconnected for non-payment"),
            ]),

        new("C-000006", "L-000006", "Hotel main intake",
            [(ServiceAccountStatus.Active, "Connection completed, transformer pad commissioned")]),

        // A second account for the Taisacan household, closed when they moved — so the premise is
        // free again and the customer 360 page has somebody with more than one account to show.
        new("C-000002", "L-000007", "Second premise, tenant's supply",
            [
                (ServiceAccountStatus.Active, "Connection completed, meter energised"),
                (ServiceAccountStatus.Closed, "Tenant moved out, final reading taken"),
            ]),

        // Still Pending: asked for, not yet connected. The prospect who has not become a customer.
        new("C-000007", "L-000008", "New connection requested, awaiting inspection", []),

        new("C-000008", "L-000009", "Cold store connection, three-phase supply",
            [(ServiceAccountStatus.Active, "Connection completed after load inspection")]),
    ];

    /// <param name="CustomerAccountNumber">Which seeded customer.</param>
    /// <param name="LocationCode">Which seeded premise.</param>
    /// <param name="OpenedReason">Why the account was opened.</param>
    /// <param name="Lifecycle">The transitions to walk it through, in order.</param>
    private sealed record DemoPairing(
        string CustomerAccountNumber,
        string LocationCode,
        string OpenedReason,
        IReadOnlyList<(ServiceAccountStatus Status, string Reason)> Lifecycle);
}
