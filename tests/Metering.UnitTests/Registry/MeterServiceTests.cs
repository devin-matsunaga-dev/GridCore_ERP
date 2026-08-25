using GridCore.Contracts.Events;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Modules.Metering.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.UnitTests.Registry;

/// <summary>
/// The register over the real EF model on SQLite in-memory. What these assert that the aggregate
/// tests cannot: the numbering series, the cross-module premise check, the audit entry and the
/// outbox row that have to commit in the same transaction as the meter row.
/// </summary>
public sealed class MeterServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Now);
    private readonly MeteringTestHost _host;

    public MeterServiceTests() =>
        _host = new MeteringTestHost(_clock, new FakeCurrentUser("technician-1", "Jesse Atalig"));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Registration_issues_the_next_meter_number_in_the_series()
    {
        var first = await _host.RegisterMeterAsync("SEN-1");
        var second = await _host.RegisterMeterAsync("SEN-2");

        Assert.Equal("MTR-000001", first.Meter.MeterNumber);
        Assert.Equal("MTR-000002", second.Meter.MeterNumber);
    }

    [Fact]
    public async Task Registration_writes_the_meter_its_history_and_its_audit_entry_in_one_transaction()
    {
        var registered = await _host.RegisterMeterAsync("SEN-1");

        await using var metering = _host.NewMeteringContext();
        await using var platform = _host.NewPlatformContext();

        var stored = await metering.Meters.Include(meter => meter.History).SingleAsync();
        var audited = await platform.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.MeterRegistered);

        Assert.Equal(registered.Meter.Id, stored.Id);
        Assert.Single(stored.History);
        Assert.Equal(AuditEntityTypes.Meter, audited.EntityType);
        Assert.Equal(stored.Id.ToString(), audited.EntityId);
        Assert.Null(audited.BeforeJson);
        Assert.Contains("MTR-000001", audited.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_publishes_the_fact_through_the_outbox()
    {
        await _host.RegisterMeterAsync("SEN-1", MeterType.ThreePhase);

        var published = _host.Events.Single<MeterRegistered>();

        Assert.Equal("MTR-000001", published.MeterNumber);
        Assert.Equal("SEN-1", published.SerialNumber);
        Assert.Equal("ThreePhase", published.MeterType);
        Assert.Equal("InStore", published.Status);
    }

    [Fact]
    public async Task One_physical_meter_cannot_be_registered_twice()
    {
        await _host.RegisterMeterAsync("SEN-1");

        var refused = await Assert.ThrowsAsync<MeterWorkflowException>(() => _host.RegisterMeterAsync("SEN-1"));

        // Names the meter it collides with, so the caller can go and look at it rather than
        // guessing why the register said no.
        Assert.Contains("MTR-000001", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_meter_with_no_serial_number_is_a_bad_request_not_a_conflict() =>
        // The distinction matters: 409 would tell the caller something else has that serial, when
        // in fact they never gave one.
        await Assert.ThrowsAsync<MeterValidationException>(() => _host.RegisterMeterAsync("   "));

    [Fact]
    public async Task Assigning_a_meter_fits_it_at_the_premise_and_publishes_the_installation()
    {
        var premise = _host.ServiceLocations.Add("L-000001");
        var meter = await _host.RegisterMeterAsync("SEN-1");

        _clock.Advance(TimeSpan.FromHours(2));

        var fitted = await _host.WithMetersAsync(meters =>
            meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise, 14_820.500m, "Transfer of service")));

        Assert.Equal(MeterStatus.Installed, fitted.Meter.Status);
        Assert.Equal(premise, fitted.Meter.ServiceLocationId);
        Assert.Equal(14_820.500m, fitted.Meter.InstallationReading);

        // The premise came back resolved through the directory, so a screen does not have to make a
        // second round trip to learn what the id means.
        Assert.Equal("L-000001", fitted.ServiceLocation?.LocationCode);

        var published = _host.Events.Single<MeterInstalled>();

        Assert.Equal(premise, published.ServiceLocationId);
        Assert.Equal("MTR-000001", published.MeterNumber);
        Assert.Equal(Now.AddHours(2), published.OccurredAt);
    }

    [Fact]
    public async Task Assigning_a_meter_asks_the_customers_module_rather_than_reading_its_tables()
    {
        var premise = _host.ServiceLocations.Add("L-000001");
        var meter = await _host.RegisterMeterAsync("SEN-1");

        await _host.WithMetersAsync(meters => meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise)));

        // The seam is the assertion. Metering has no customers schema to read — the whole module
        // boundary is one interface, which is what lets this test run without a container.
        Assert.Contains(premise, _host.ServiceLocations.Lookups);
    }

    [Fact]
    public async Task A_premise_the_customers_module_does_not_know_is_a_404()
    {
        var meter = await _host.RegisterMeterAsync("SEN-1");

        await Assert.ThrowsAsync<ServiceLocationNotFoundException>(() =>
            _host.WithMetersAsync(meters => meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(Guid.CreateVersion7()))));
    }

    [Fact]
    public async Task A_premise_that_is_out_of_service_cannot_be_metered()
    {
        var premise = _host.ServiceLocations.Add("L-000009", isActive: false);
        var meter = await _host.RegisterMeterAsync("SEN-1");

        var refused = await Assert.ThrowsAsync<MeterWorkflowException>(() =>
            _host.WithMetersAsync(meters => meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise))));

        Assert.Contains("L-000009", refused.Message, StringComparison.Ordinal);
        Assert.Contains("not in service", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_active_meter_per_service_location_is_enforced()
    {
        // The rule WP-2.1 exists to prove. It is a database fact as well
        // (ux_meters_service_location); this is the 409 that reaches a caller before the index has
        // to reject anything.
        var premise = _host.ServiceLocations.Add("L-000001");
        var first = await _host.RegisterMeterAsync("SEN-1");
        var second = await _host.RegisterMeterAsync("SEN-2");

        await _host.WithMetersAsync(meters => meters.AssignAsync(first.Meter.Id, new AssignMeterInput(premise)));

        var refused = await Assert.ThrowsAsync<MeterWorkflowException>(() =>
            _host.WithMetersAsync(meters => meters.AssignAsync(second.Meter.Id, new AssignMeterInput(premise))));

        Assert.Contains("already metered by MTR-000001", refused.Message, StringComparison.Ordinal);

        // And the losing meter is untouched: still in stock, still available for the next job.
        await using var metering = _host.NewMeteringContext();
        var stored = await metering.Meters.SingleAsync(meter => meter.Id == second.Meter.Id);

        Assert.Equal(MeterStatus.InStore, stored.Status);
        Assert.Null(stored.ServiceLocationId);
    }

    [Fact]
    public async Task A_premise_can_be_metered_again_once_the_meter_on_it_has_been_removed()
    {
        var premise = _host.ServiceLocations.Add("L-000001");
        var first = await _host.RegisterMeterAsync("SEN-1");
        var second = await _host.RegisterMeterAsync("SEN-2");

        await _host.WithMetersAsync(meters => meters.AssignAsync(first.Meter.Id, new AssignMeterInput(premise)));
        await _host.WithMetersAsync(meters => meters.RemoveAsync(first.Meter.Id, "Exchanged"));

        var replacement = await _host.WithMetersAsync(meters =>
            meters.AssignAsync(second.Meter.Id, new AssignMeterInput(premise, 0m, "Exchange meter fitted")));

        Assert.Equal(premise, replacement.Meter.ServiceLocationId);
    }

    [Fact]
    public async Task Removing_a_meter_publishes_the_premise_it_left_unmetered()
    {
        var premise = _host.ServiceLocations.Add("L-000001");
        var meter = await _host.RegisterMeterAsync("SEN-1");

        await _host.WithMetersAsync(meters => meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise)));

        var removed = await _host.WithMetersAsync(meters => meters.RemoveAsync(meter.Meter.Id, "Tenant moved out"));

        Assert.Null(removed.Meter.ServiceLocationId);
        Assert.Null(removed.ServiceLocation);

        var published = _host.Events.Single<MeterRemoved>();

        // Read before the removal cleared it: without this the event would carry an empty premise
        // and nothing downstream could tell which supply had stopped being measured.
        Assert.Equal(premise, published.ServiceLocationId);
        Assert.Equal("Tenant moved out", published.Reason);
    }

    [Fact]
    public async Task A_status_change_that_leaves_the_meter_where_it_is_publishes_nothing()
    {
        var premise = _host.ServiceLocations.Add("L-000001");
        var meter = await _host.RegisterMeterAsync("SEN-1");

        await _host.WithMetersAsync(meters => meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise)));

        _host.Events.Published.Clear();

        var faulty = await _host.WithMetersAsync(meters =>
            meters.ChangeStatusAsync(meter.Meter.Id, MeterStatus.Faulty, "Dials stopped between reads"));

        Assert.Equal(MeterStatus.Faulty, faulty.Meter.Status);
        Assert.Equal(premise, faulty.Meter.ServiceLocationId);

        // Nothing about what measures this premise changed, which is the only fact another module
        // gates on — publishing here would be noise in every inbox.
        Assert.Empty(_host.Events.Published);
    }

    [Fact]
    public async Task A_status_change_is_still_audited_with_a_before_and_an_after()
    {
        var meter = await _host.RegisterMeterAsync("SEN-1");

        await _host.WithMetersAsync(meters =>
            meters.ChangeStatusAsync(meter.Meter.Id, MeterStatus.Retired, "Failed bench check"));

        await using var platform = _host.NewPlatformContext();
        var audited = await platform.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.MeterStatusChanged);

        // Invariant 1: every write endpoint produces an audit entry with before and after.
        Assert.Contains("InStore", audited.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("Retired", audited.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correcting_a_serial_onto_one_another_meter_already_has_is_refused()
    {
        await _host.RegisterMeterAsync("SEN-1");
        var second = await _host.RegisterMeterAsync("SEN-2");

        await Assert.ThrowsAsync<MeterWorkflowException>(() =>
            _host.WithMetersAsync(meters =>
                meters.UpdateAsync(second.Meter.Id, new UpdateMeterInput("SEN-1", MeterType.SinglePhase))));
    }

    [Fact]
    public async Task Correcting_a_meters_own_serial_to_the_same_value_is_not_a_collision_with_itself()
    {
        var meter = await _host.RegisterMeterAsync("SEN-1");

        var corrected = await _host.WithMetersAsync(meters =>
            meters.UpdateAsync(meter.Meter.Id, new UpdateMeterInput("SEN-1", MeterType.ThreePhase, Manufacturer: "Itron")));

        Assert.Equal(MeterType.ThreePhase, corrected.Meter.Type);
        Assert.Equal("Itron", corrected.Meter.Manufacturer);
    }

    [Fact]
    public async Task Reading_a_meter_that_is_not_there_answers_nothing_rather_than_throwing() =>
        Assert.Null(await _host.WithMetersAsync(meters => meters.FindAsync(Guid.CreateVersion7())));

    [Fact]
    public async Task Writing_to_a_meter_that_is_not_there_is_a_404() =>
        await Assert.ThrowsAsync<MeterNotFoundException>(() =>
            _host.WithMetersAsync(meters => meters.RemoveAsync(Guid.CreateVersion7(), null)));

    [Fact]
    public async Task The_history_of_a_meter_that_is_not_there_is_a_404_not_an_empty_list() =>
        // An empty list would say the meter exists and nothing has happened to it, which cannot be
        // true — every meter is registered with an opening line.
        await Assert.ThrowsAsync<MeterNotFoundException>(() =>
            _host.WithMetersAsync(meters => meters.HistoryAsync(Guid.CreateVersion7())));

    [Fact]
    public async Task A_meters_history_reads_oldest_first_and_can_be_narrowed_to_one_kind_of_line()
    {
        var premise = _host.ServiceLocations.Add("L-000001");
        var meter = await _host.RegisterMeterAsync("SEN-1");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _host.WithMetersAsync(meters => meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise)));

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _host.WithMetersAsync(meters => meters.RemoveAsync(meter.Meter.Id, "Exchanged"));

        var history = await _host.WithMetersAsync(meters => meters.HistoryAsync(meter.Meter.Id));

        Assert.Equal(
            [MeterHistoryEntryType.Registered, MeterHistoryEntryType.Installed, MeterHistoryEntryType.Removed],
            history.Select(entry => entry.EntryType));

        var installations = await _host.WithMetersAsync(meters =>
            meters.HistoryAsync(meter.Meter.Id, MeterHistoryEntryType.Installed));

        Assert.Equal(premise, Assert.Single(installations).ServiceLocationId);
    }

    [Fact]
    public async Task The_list_filters_on_the_premise_so_a_customer_page_can_ask_what_meters_here()
    {
        var premise = _host.ServiceLocations.Add("L-000001");
        var fitted = await _host.RegisterMeterAsync("SEN-1");
        await _host.RegisterMeterAsync("SEN-2");

        await _host.WithMetersAsync(meters => meters.AssignAsync(fitted.Meter.Id, new AssignMeterInput(premise)));

        var here = await _host.WithMetersAsync(meters => meters.ListAsync(new MeterQuery(ServiceLocationId: premise)));

        Assert.Equal("MTR-000001", Assert.Single(here).Meter.MeterNumber);
    }

    [Fact]
    public async Task The_list_filters_on_whether_a_meter_is_on_a_premise_at_all()
    {
        var premise = _host.ServiceLocations.Add("L-000001");
        var fitted = await _host.RegisterMeterAsync("SEN-1");
        await _host.RegisterMeterAsync("SEN-2");

        await _host.WithMetersAsync(meters => meters.AssignAsync(fitted.Meter.Id, new AssignMeterInput(premise)));

        var onPremises = await _host.WithMetersAsync(meters => meters.ListAsync(new MeterQuery(Fitted: true)));
        var inStores = await _host.WithMetersAsync(meters => meters.ListAsync(new MeterQuery(Fitted: false)));

        Assert.Equal("MTR-000001", Assert.Single(onPremises).Meter.MeterNumber);
        Assert.Equal("MTR-000002", Assert.Single(inStores).Meter.MeterNumber);
    }

    [Fact]
    public async Task The_list_resolves_every_premise_in_one_call_across_the_module_boundary()
    {
        var first = _host.ServiceLocations.Add("L-000001");
        var second = _host.ServiceLocations.Add("L-000002");

        var one = await _host.RegisterMeterAsync("SEN-1");
        var two = await _host.RegisterMeterAsync("SEN-2");

        await _host.WithMetersAsync(meters => meters.AssignAsync(one.Meter.Id, new AssignMeterInput(first)));
        await _host.WithMetersAsync(meters => meters.AssignAsync(two.Meter.Id, new AssignMeterInput(second)));

        _host.ServiceLocations.Lookups.Clear();

        var page = await _host.WithMetersAsync(meters => meters.ListAsync(new MeterQuery()));

        Assert.Equal(2, page.Count);
        Assert.All(page, record => Assert.NotNull(record.ServiceLocation));

        // Batched, not one lookup per row: a full page asking separately would be 200 round trips
        // across the boundary for a list the register answers in one.
        Assert.Equal(new[] { first, second }.Order(), _host.ServiceLocations.Lookups.Order());
    }

    [Fact]
    public async Task A_list_of_meters_in_a_store_does_not_ask_the_other_module_anything()
    {
        await _host.RegisterMeterAsync("SEN-1");
        await _host.RegisterMeterAsync("SEN-2");

        _host.ServiceLocations.Lookups.Clear();

        var page = await _host.WithMetersAsync(meters => meters.ListAsync(new MeterQuery()));

        Assert.Equal(2, page.Count);
        Assert.Empty(_host.ServiceLocations.Lookups);
    }

    [Fact]
    public async Task The_search_matches_the_meter_number_and_the_serial()
    {
        await _host.RegisterMeterAsync("SEN-4471102");
        await _host.RegisterMeterAsync("ITR-9930041");

        var bySerial = await _host.WithMetersAsync(meters => meters.ListAsync(new MeterQuery(Search: "itr-99")));
        var byNumber = await _host.WithMetersAsync(meters => meters.ListAsync(new MeterQuery(Search: "MTR-000001")));

        Assert.Equal("ITR-9930041", Assert.Single(bySerial).Meter.SerialNumber);
        Assert.Equal("SEN-4471102", Assert.Single(byNumber).Meter.SerialNumber);
    }

    [Fact]
    public async Task The_list_never_returns_more_than_the_registers_page_size()
    {
        await _host.RegisterMeterAsync("SEN-1");

        var page = await _host.WithMetersAsync(meters => meters.ListAsync(new MeterQuery(Limit: int.MaxValue)));

        Assert.True(page.Count <= MeterService.MaxPageSize);
    }
}
