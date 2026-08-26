using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;

namespace GridCore.Modules.Customers.Seeding;

/// <summary>
/// A small demo world of customers and the premises they are served at — the registry half of the
/// dataset SPEC.md describes, for the modules that hang off it in WP-1.2 onwards.
/// </summary>
/// <remarks>
/// <para>
/// The places are real: the demo utility is Rota Utilities, so its customers live on <b>Rota</b>,
/// <b>Saipan</b> and <b>Tinian</b> — the three main Northern Mariana Islands — in villages that
/// exist, with the postal codes those islands actually use. Invented districts read as filler in a
/// demonstration; a real place name costs nothing and makes the screen believable.
/// </para>
/// <para>
/// Numbers are assigned here rather than through <see cref="IRegistryNumberGenerator"/>: the
/// generator reads the highest number already committed, and inside the seeding transaction none
/// of these rows are visible to a query yet. Starting the series at 1 is what lets a customer
/// registered afterwards continue it correctly.
/// </para>
/// </remarks>
public sealed class CustomersDemoSeeder(CustomersDbContext database, TimeProvider clock) : IDemoSeeder
{
    /// <summary>The island the demo utility is based on.</summary>
    public const string Rota = "Rota";

    /// <summary>The largest of the three demo islands.</summary>
    public const string Saipan = "Saipan";

    /// <summary>The third demo island.</summary>
    public const string Tinian = "Tinian";

    /// <summary>Country every demo address sits in (the Northern Mariana Islands).</summary>
    public const string Country = "MP";

    /// <summary>The three islands the demo world is spread across.</summary>
    public static IReadOnlyList<string> Islands { get; } = [Rota, Saipan, Tinian];

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second copy of this registry.</remarks>
    public string Name => "customers.registry";

    /// <inheritdoc />
    /// <remarks>
    /// After the platform's own queue and before anything that needs a customer to exist: meters,
    /// bills and work orders all attach to a location seeded here.
    /// </remarks>
    public int Order => 200;

    /// <summary>
    /// The colleague seeded deposits are attributed to. A demo stand-in, never a real identity —
    /// <see cref="DemoActor"/> holds no permissions, which is exactly right here: the ledger entries
    /// below are written as entities, not through <c>ICustomerDepositService</c>, so nothing is
    /// being authorised.
    /// </summary>
    private static readonly DemoActor Cashier = new("cashier", "Rita Atalig");

    /// <inheritdoc />
    public Task SeedAsync(CancellationToken cancellationToken)
    {
        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. A step per row keeps the registry list stable.
        var now = clock.GetUtcNow();
        var step = 0;

        DateTimeOffset Next() => now.AddMilliseconds(step++);

        var customers = DemoCustomers.Select((customer, index) =>
        {
            var registered = Customer.Register(
                RegistryNumbers.Format(CustomerNumbers.CustomerPrefix, index + 1),
                customer.Name,
                customer.Class,
                Next(),
                customer.ContactName,
                customer.Email,
                customer.Phone,
                customer.Status);

            return (Registered: registered, customer.DepositHeld);
        }).ToList();

        database.Customers.AddRange(customers.Select(pair => pair.Registered));

        // Since WP-2.12 a customer's deposit is the sum of its ledger entries, so a seeded balance
        // has to arrive as one — a demo world whose deposit tab was empty while the header said
        // $450 would be demonstrating the bug this package exists to remove. Written as entities
        // rather than through the service for the reason every seeder is: the runner's unit of work
        // owns the transaction, and a seeded row is not somebody exercising a permission.
        database.DepositEntries.AddRange(
            customers
                .Where(pair => pair.DepositHeld > 0m)
                .Select(pair => DepositEntry.Collect(
                    pair.Registered,
                    pair.DepositHeld,
                    DepositRules.Currency,
                    isInterestBearing: false,
                    "Security deposit taken when service was connected.",
                    RegistryActor.Of(Cashier),
                    Next())));

        database.ServiceLocations.AddRange(
            DemoLocations.Select((location, index) =>
                ServiceLocation.Register(
                    RegistryNumbers.Format(CustomerNumbers.ServiceLocationPrefix, index + 1),
                    Address.Create(location.Line1, location.City, location.Region, Country, postalCode: location.PostalCode),
                    Next(),
                    location.Description)));

        // No SaveChanges: the runner's unit of work saves this and the seed record in one
        // transaction, which is what makes a half-seeded demo world impossible.
        return Task.CompletedTask;
    }

    private static IReadOnlyList<DemoCustomer> DemoCustomers { get; } =
    [
        new("Sablan Family Residence", CustomerClass.Residential, CustomerStatus.Active, "Maria Sablan", "maria.sablan@example.com", "+1-670-532-0114", 75.00m),
        new("Taisacan Household", CustomerClass.Residential, CustomerStatus.Active, "Joaquin Taisacan", "j.taisacan@example.com", "+1-670-532-0188", 75.00m),
        new("Songsong Village Market", CustomerClass.Commercial, CustomerStatus.Active, "Elena Manglona", "accounts@songsongmarket.example.com", "+1-670-532-1200", 450.00m),
        new("Rota Health Centre", CustomerClass.Commercial, CustomerStatus.Active, "Facilities Office", "facilities@rotahealth.example.com", "+1-670-532-9411", 1_200.00m),
        new("Camacho Residence", CustomerClass.Residential, CustomerStatus.Suspended, "Rosa Camacho", "rosa.camacho@example.com", "+1-670-234-7756", 75.00m),
        new("Garapan Beachfront Hotel", CustomerClass.Commercial, CustomerStatus.Active, "Night Manager", "ops@garapanbeach.example.com", "+1-670-234-3300", 2_500.00m),
        new("Aldan Household", CustomerClass.Residential, CustomerStatus.Prospect, "Peter Aldan", "p.aldan@example.com", "+1-670-433-2091", 0m),
        new("Tinian Cold Storage", CustomerClass.Commercial, CustomerStatus.Active, "Warehouse Office", "office@tiniancold.example.com", "+1-670-433-8180", 900.00m),
    ];

    private static IReadOnlyList<DemoLocation> DemoLocations { get; } =
    [
        new("128 As Nieves Road", "Songsong", Rota, "96951", "Single-storey house, meter on the north wall"),
        new("14 Tatachog Street", "Songsong", Rota, "96951", "Meter cabinet shared with the neighbouring lot"),
        new("1 Market Row", "Songsong", Rota, "96951", "Three-phase supply, chiller load"),
        new("Route 100, Sinapalo", "Sinapalo", Rota, "96951", "Health centre — standby generator on site"),
        new("87 Airport Road", "Sinapalo", Rota, "96951", "Meter at the property line, dogs on site"),
        new("22 Beach Road", "Garapan", Saipan, "96950", "Hotel main intake, transformer pad at the rear"),
        new("450 Middle Road", "Chalan Kanoa", Saipan, "96950", "Duplex, two meters on one riser"),
        new("9 Ayuyu Drive", "San Roque", Saipan, "96950", "New connection pending inspection"),
        new("3 Broadway", "San Jose", Tinian, "96952", "Cold store, three-phase supply"),
        new("62 Marpo Heights Road", "Marpo Heights", Tinian, "96952", "Long service drop from the pole across the road"),
    ];

    /// <param name="Name">Who they are.</param>
    /// <param name="Class">Residential or commercial.</param>
    /// <param name="Status">Where they stand, so the registry shows more than one pill.</param>
    /// <param name="ContactName">Who to ask for.</param>
    /// <param name="Email">Where to email them.</param>
    /// <param name="Phone">Where to call them.</param>
    /// <param name="DepositHeld">Deposit the utility holds — seeded as a ledger collection, not a field.</param>
    private sealed record DemoCustomer(
        string Name,
        CustomerClass Class,
        CustomerStatus Status,
        string ContactName,
        string Email,
        string Phone,
        decimal DepositHeld);

    /// <param name="Line1">Street address.</param>
    /// <param name="City">Village.</param>
    /// <param name="Region">Island.</param>
    /// <param name="PostalCode">Postal code of that island.</param>
    /// <param name="Description">What a crew would need to know.</param>
    private sealed record DemoLocation(
        string Line1,
        string City,
        string Region,
        string PostalCode,
        string Description);
}
