using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.UnitTests.Infrastructure;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.UnitTests.Registry;

/// <summary>The metering schema as EF actually builds it.</summary>
public sealed class MeterModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Crew = new("technician-1", "Jesse Atalig");

    private static Meter Register(string number, string serialNumber) =>
        Meter.Register(number, serialNumber, MeterType.SinglePhase, Crew, Now);

    [Fact]
    public void The_module_owns_a_schema_of_its_own_and_names_its_tables_in_snake_case()
    {
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        var model = context.Model;

        Assert.Equal(MeteringDbContext.SchemaName, model.GetDefaultSchema());
        Assert.Equal("meters", model.FindEntityType(typeof(Meter))!.GetTableName());
        Assert.Equal("meter_history", model.FindEntityType(typeof(MeterHistoryEntry))!.GetTableName());
    }

    [Theory]
    [InlineData(nameof(Meter.IsFitted))]
    [InlineData(nameof(Meter.AllowedTransitions))]
    [InlineData(nameof(Meter.AllowedStatusChanges))]
    public void The_derived_lifecycle_properties_are_not_columns(string property)
    {
        // They are computed from Status. Mapped, EF would want a backing field it has no way to
        // find, and the model would fail to build at startup rather than in a test.
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        var meter = context.Model.FindEntityType(typeof(Meter))!;

        Assert.Null(meter.FindProperty(property));
        Assert.Null(meter.FindNavigation(property));
    }

    [Fact]
    public void The_premise_is_a_plain_column_with_no_foreign_key()
    {
        // Customers is another module over another schema, so the database cannot enforce this and
        // must not pretend to. It is checked through IServiceLocationDirectory instead.
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        var meter = context.Model.FindEntityType(typeof(Meter))!;

        Assert.NotNull(meter.FindProperty(nameof(Meter.ServiceLocationId)));
        Assert.DoesNotContain(
            meter.GetForeignKeys(),
            key => key.Properties.Any(property => property.Name == nameof(Meter.ServiceLocationId)));
    }

    [Fact]
    public async Task Two_meters_cannot_share_a_meter_number()
    {
        // Failure path at the database, not in code: the unique index is what makes "one number,
        // one meter" true even when two registrations race the number generator.
        using var host = new MeteringTestHost();
        await using var context = host.NewMeteringContext();

        context.Meters.Add(Register("MTR-000001", "SEN-1"));
        context.Meters.Add(Register("MTR-000001", "SEN-2"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_meters_cannot_share_a_serial_number()
    {
        using var host = new MeteringTestHost();
        await using var context = host.NewMeteringContext();

        context.Meters.Add(Register("MTR-000001", "SEN-1"));
        context.Meters.Add(Register("MTR-000002", "SEN-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_meters_cannot_be_fitted_at_one_premise()
    {
        // WP-2.1's headline rule as a database fact. The service answers 409 first; this is what
        // makes the rule true even if two crews book their work at the same moment.
        using var host = new MeteringTestHost();
        await using var context = host.NewMeteringContext();

        var premise = Guid.CreateVersion7();

        var first = Register("MTR-000001", "SEN-1");
        var second = Register("MTR-000002", "SEN-2");

        first.InstallAt(premise, Crew, Now);
        second.InstallAt(premise, Crew, Now);

        context.Meters.AddRange(first, second);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Any_number_of_meters_can_sit_in_a_store_together()
    {
        // The other half of the same unfiltered unique index: a meter that is on no premise holds
        // NULL, and NULLs are distinct on both Postgres and SQLite. That is what lets the index
        // carry the rule with no SQL predicate naming a status to keep in step (WP-1.2's lesson).
        using var host = new MeteringTestHost();
        await using var context = host.NewMeteringContext();

        context.Meters.AddRange(
            Register("MTR-000001", "SEN-1"),
            Register("MTR-000002", "SEN-2"),
            Register("MTR-000003", "SEN-3"));

        await context.SaveChangesAsync();

        Assert.Equal(3, await context.Meters.CountAsync());
    }

    [Fact]
    public void The_premise_index_carries_no_filter_at_all()
    {
        // Asserted rather than assumed: a filter here would be a SQL string the compiler never
        // reads, and the two guard tests WP-1.2 needed exist because such a string had drifted.
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        var index = context.Model
            .FindEntityType(typeof(Meter))!
            .GetIndexes()
            .Single(candidate => candidate.GetDatabaseName() == "ux_meters_service_location");

        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
    }

    [Fact]
    public async Task A_meters_history_is_loaded_through_it_and_survives_a_round_trip()
    {
        using var host = new MeteringTestHost();

        var premise = Guid.CreateVersion7();
        var meter = Register("MTR-000001", "SEN-1");

        meter.InstallAt(premise, Crew, Now.AddMinutes(1), 100m, "Fitted");

        await using (var writing = host.NewMeteringContext())
        {
            writing.Meters.Add(meter);
            await writing.SaveChangesAsync();
        }

        await using var reading = host.NewMeteringContext();

        var stored = await reading.Meters.Include(candidate => candidate.History).SingleAsync();

        Assert.Equal(2, stored.History.Count);
        Assert.Equal(premise, stored.History.Last().ServiceLocationId);
        Assert.Equal(100m, stored.InstallationReading);
    }

    [Fact]
    public async Task An_installation_reading_keeps_its_three_decimal_places()
    {
        using var host = new MeteringTestHost();

        var meter = Register("MTR-000001", "SEN-1");

        meter.InstallAt(Guid.CreateVersion7(), Crew, Now, 14_820.500m);

        await using (var writing = host.NewMeteringContext())
        {
            writing.Meters.Add(meter);
            await writing.SaveChangesAsync();
        }

        await using var reading = host.NewMeteringContext();

        // decimal, never double: a dial reading is money's neighbour and the same rule applies.
        Assert.Equal(14_820.500m, (await reading.Meters.SingleAsync()).InstallationReading);
    }
}
