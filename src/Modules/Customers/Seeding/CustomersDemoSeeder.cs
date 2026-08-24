using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
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

    /// <inheritdoc />
    public Task SeedAsync(CancellationToken cancellationToken)
    {
        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. A step per row keeps the registry list stable.
        var now = clock.GetUtcNow();
        var step = 0;

        DateTimeOffset Next() => now.AddMilliseconds(step++);

        database.Customers.AddRange(
            DemoCustomers.Select((customer, index) =>
                Customer.Register(
                    RegistryNumbers.Format(RegistryNumbers.CustomerPrefix, index + 1),
                    customer.Name,
                    customer.Class,
                    Next(),
                    customer.ContactName,
                    customer.Email,
                    customer.Phone,
                    customer.DepositHeld,
                    customer.Status)));

        database.ServiceLocations.AddRange(
            DemoLocations.Select((location, index) =>
                ServiceLocation.Register(
                    RegistryNumbers.Format(RegistryNumbers.ServiceLocationPrefix, index + 1),
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
    /// <param name="DepositHeld">Deposit the utility holds.</param>
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
