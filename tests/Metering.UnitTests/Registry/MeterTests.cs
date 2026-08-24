using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Metering.UnitTests.Registry;

/// <summary>
/// The aggregate on its own — no database, no host. Everything asserted here is a rule the meter
/// enforces for any caller, including a seeder or a later module that reaches the service directly.
/// </summary>
public sealed class MeterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Crew = new("technician-1", "Jesse Atalig");

    private static Meter NewMeter(MeterType type = MeterType.SinglePhase) =>
        Meter.Register("MTR-000001", "SEN-4471102", type, Crew, Now, "Sensus", "iConA");

    [Fact]
    public void A_registered_meter_starts_in_stock_and_on_no_premise()
    {
        var meter = NewMeter();

        Assert.Equal(MeterStatus.InStore, meter.Status);
        Assert.Null(meter.ServiceLocationId);
        Assert.False(meter.IsFitted);
        Assert.Equal(7, meter.Id.Version);
    }

    [Fact]
    public void Registration_opens_the_history_so_the_meter_never_came_from_nowhere()
    {
        var entry = Assert.Single(NewMeter().History);

        Assert.Equal(MeterHistoryEntryType.Registered, entry.EntryType);
        Assert.Null(entry.FromStatus);
        Assert.Equal(MeterStatus.InStore, entry.ToStatus);
        Assert.Equal("technician-1", entry.ActorId);
        Assert.Equal("Jesse Atalig", entry.ActorName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_meter_without_a_serial_number_is_refused(string serialNumber) =>
        // Required here as well as in the validator, because a seeder does not go through the edge.
        Assert.Throws<MeterValidationException>(() =>
            Meter.Register("MTR-000001", serialNumber, MeterType.SinglePhase, Crew, Now));

    [Fact]
    public void A_meter_type_GridCore_does_not_declare_is_refused() =>
        Assert.Throws<MeterValidationException>(() =>
            Meter.Register("MTR-000001", "SEN-1", (MeterType)99, Crew, Now));

    [Fact]
    public void Fitting_a_meter_records_where_it_went_and_what_the_dials_read()
    {
        var premise = Guid.CreateVersion7();
        var meter = NewMeter();

        meter.InstallAt(premise, Crew, Now.AddHours(1), 14_820.500m, "Transfer of service");

        Assert.Equal(MeterStatus.Installed, meter.Status);
        Assert.Equal(premise, meter.ServiceLocationId);
        Assert.Equal(Now.AddHours(1), meter.InstalledAt);
        Assert.Equal(14_820.500m, meter.InstallationReading);
        Assert.True(meter.IsFitted);

        var line = meter.History.Last();

        Assert.Equal(MeterHistoryEntryType.Installed, line.EntryType);
        Assert.Equal(MeterStatus.InStore, line.FromStatus);
        Assert.Equal(MeterStatus.Installed, line.ToStatus);
        Assert.Equal(premise, line.ServiceLocationId);
        Assert.Equal("Transfer of service", line.Note);
    }

    [Fact]
    public void A_meter_already_on_a_premise_cannot_be_fitted_to_another()
    {
        var meter = NewMeter();
        var first = Guid.CreateVersion7();

        meter.InstallAt(first, Crew, Now);

        var refused = Assert.Throws<MeterWorkflowException>(() => meter.InstallAt(Guid.CreateVersion7(), Crew, Now));

        Assert.Contains("remove it", refused.Message, StringComparison.OrdinalIgnoreCase);

        // The guard ran before the first mutation, so the meter still describes what actually
        // happened — WP-1.4's lesson, which cost a test failure to notice.
        Assert.Equal(first, meter.ServiceLocationId);
        Assert.Equal(2, meter.History.Count);
    }

    [Fact]
    public void A_retired_meter_cannot_be_fitted()
    {
        var meter = NewMeter();

        meter.ChangeStatus(MeterStatus.Retired, Crew, Now, "Failed bench check");

        Assert.Throws<MeterWorkflowException>(() => meter.InstallAt(Guid.CreateVersion7(), Crew, Now));
    }

    [Fact]
    public void A_meter_cannot_be_fitted_at_no_premise_at_all() =>
        Assert.Throws<MeterValidationException>(() => NewMeter().InstallAt(Guid.Empty, Crew, Now));

    [Fact]
    public void A_negative_installation_reading_is_refused() =>
        Assert.Throws<MeterValidationException>(() =>
            NewMeter().InstallAt(Guid.CreateVersion7(), Crew, Now, -1m));

    [Fact]
    public void A_reading_finer_than_the_register_is_refused_rather_than_rounded()
    {
        // The same call WP-1.1 made for a deposit finer than a cent: the column would truncate a
        // number nobody chose, and the central rounding helper still has no home (WP-2.3 owns it).
        var refused = Assert.Throws<MeterValidationException>(() =>
            NewMeter().InstallAt(Guid.CreateVersion7(), Crew, Now, 12.0001m));

        Assert.Contains("decimal places", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_meter_frees_the_premise_but_the_history_still_names_it()
    {
        var premise = Guid.CreateVersion7();
        var meter = NewMeter();

        meter.InstallAt(premise, Crew, Now, 100m);
        meter.Remove(Crew, Now.AddDays(30), "Tenant moved out, final reading taken");

        Assert.Equal(MeterStatus.Removed, meter.Status);
        Assert.Null(meter.ServiceLocationId);
        Assert.Null(meter.InstalledAt);
        Assert.Null(meter.InstallationReading);

        var line = meter.History.Last();

        // This is what keeps "which meter measured that premise in March" answerable once the
        // device is on somebody else's wall.
        Assert.Equal(MeterHistoryEntryType.Removed, line.EntryType);
        Assert.Equal(premise, line.ServiceLocationId);
        Assert.Equal(MeterStatus.Installed, line.FromStatus);
    }

    [Fact]
    public void A_faulty_meter_is_still_fitted_and_can_still_be_removed()
    {
        var premise = Guid.CreateVersion7();
        var meter = NewMeter();

        meter.InstallAt(premise, Crew, Now);
        meter.ChangeStatus(MeterStatus.Faulty, Crew, Now.AddDays(1), "Dials stopped between reads");

        Assert.True(meter.IsFitted);
        Assert.Equal(premise, meter.ServiceLocationId);

        meter.Remove(Crew, Now.AddDays(2), "Exchanged");

        Assert.Equal(MeterStatus.Removed, meter.Status);
        Assert.Null(meter.ServiceLocationId);
    }

    [Fact]
    public void A_meter_on_no_premise_cannot_be_removed()
    {
        var refused = Assert.Throws<MeterWorkflowException>(() => NewMeter().Remove(Crew, Now));

        Assert.Contains("not fitted anywhere", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_status_change_may_not_fit_a_meter_because_it_has_no_premise_to_fit_it_to()
    {
        var refused = Assert.Throws<MeterWorkflowException>(() =>
            NewMeter().ChangeStatus(MeterStatus.Installed, Crew, Now));

        // Named, so the caller is told which endpoint does the job rather than being left to guess.
        Assert.Contains("Assign it instead", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_status_change_may_not_unfit_a_meter_because_nothing_would_record_which_premise_it_freed()
    {
        var meter = NewMeter();

        meter.InstallAt(Guid.CreateVersion7(), Crew, Now);

        var refused = Assert.Throws<MeterWorkflowException>(() =>
            meter.ChangeStatus(MeterStatus.Removed, Crew, Now));

        Assert.Contains("Remove it instead", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_illegal_lifecycle_move_is_refused_naming_both_ends()
    {
        var meter = NewMeter();

        meter.ChangeStatus(MeterStatus.Retired, Crew, Now, "Scrapped");

        var refused = Assert.Throws<MeterWorkflowException>(() =>
            meter.ChangeStatus(MeterStatus.InStore, Crew, Now));

        Assert.Contains("Retired", refused.Message, StringComparison.Ordinal);
        Assert.Contains("InStore", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_to_the_status_it_is_already_in_says_so()
    {
        var refused = Assert.Throws<MeterWorkflowException>(() =>
            NewMeter().ChangeStatus(MeterStatus.InStore, Crew, Now));

        Assert.Contains("already InStore", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lifecycle_move_that_leaves_the_meter_where_it_is_records_the_premise_it_is_on()
    {
        var premise = Guid.CreateVersion7();
        var meter = NewMeter();

        meter.InstallAt(premise, Crew, Now);
        meter.ChangeStatus(MeterStatus.Faulty, Crew, Now.AddDays(1), "Dials stopped");

        var line = meter.History.Last();

        Assert.Equal(MeterHistoryEntryType.StatusChanged, line.EntryType);
        Assert.Equal(premise, line.ServiceLocationId);
    }

    [Fact]
    public void Correcting_the_details_cannot_move_the_meter_or_its_number()
    {
        var premise = Guid.CreateVersion7();
        var meter = NewMeter();

        meter.InstallAt(premise, Crew, Now);
        meter.UpdateDetails("ITR-9930041", MeterType.ThreePhase, "Itron", "Centron II");

        Assert.Equal("ITR-9930041", meter.SerialNumber);
        Assert.Equal(MeterType.ThreePhase, meter.Type);
        Assert.Equal("MTR-000001", meter.MeterNumber);
        Assert.Equal(premise, meter.ServiceLocationId);
        Assert.Equal(MeterStatus.Installed, meter.Status);
    }

    [Fact]
    public void Free_text_is_trimmed_and_whitespace_becomes_nothing()
    {
        var meter = Meter.Register("MTR-000001", "  SEN-1  ", MeterType.SinglePhase, Crew, Now, "   ", " iConA ");

        Assert.Equal("SEN-1", meter.SerialNumber);
        Assert.Null(meter.Manufacturer);
        Assert.Equal("iConA", meter.Model);
    }

    [Fact]
    public void Allowed_status_changes_never_offer_a_move_the_status_endpoint_would_refuse()
    {
        var meter = NewMeter();

        // In stock, the machine allows Installed and Retired — but fitting needs a premise, so the
        // status endpoint can only ever do the second. A UI rendering buttons from the full list
        // would show one that always 409s.
        Assert.Equal([MeterStatus.Retired], meter.AllowedStatusChanges);
        Assert.Equal([MeterStatus.Installed, MeterStatus.Retired], meter.AllowedTransitions);
    }

    [Fact]
    public void An_installed_meter_can_only_be_flagged_faulty_without_a_removal()
    {
        var meter = NewMeter();

        meter.InstallAt(Guid.CreateVersion7(), Crew, Now);

        Assert.Equal([MeterStatus.Faulty], meter.AllowedStatusChanges);
    }
}
