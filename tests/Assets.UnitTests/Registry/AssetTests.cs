using GridCore.Modules.Assets.Features.Assets;
using GridCore.Modules.Assets.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Assets.UnitTests.Registry;

/// <summary>The asset aggregate on its own — no database, no host, milliseconds.</summary>
public class AssetTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Engineer = new("subject-1", "Ray Manglona");

    private static Asset Register(
        AssetStatus status = AssetTransitions.Initial,
        AssetCondition condition = AssetCondition.Unknown,
        string? serialNumber = null,
        DateOnly? installedOn = null,
        GeoPosition? position = null) =>
        Asset.Register(
            "AST-000001",
            AssetClass.Transformer,
            "Songsong Substation Transformer T-3",
            Engineer,
            Now,
            serialNumber,
            installedOn: installedOn,
            position: position,
            status: status,
            condition: condition);

    [Fact]
    public void A_new_asset_starts_in_storage_and_ungraded()
    {
        var asset = Register();

        Assert.Equal(AssetStatus.InStorage, asset.Status);
        Assert.Equal(AssetCondition.Unknown, asset.Condition);

        // Not stamped: nobody has looked at it, and a date here would say an inspector had been.
        Assert.Null(asset.ConditionAssessedAt);
    }

    [Fact]
    public void Registering_an_asset_already_graded_stamps_when_it_was_assessed() =>
        Assert.Equal(Now, Register(condition: AssetCondition.Good).ConditionAssessedAt);

    [Fact]
    public void Registration_opens_the_history_with_a_line_of_its_own()
    {
        // Without it, "where did this asset come from" is unanswerable and the history starts at
        // whatever happened to it first.
        var entry = Assert.Single(Register(condition: AssetCondition.Excellent).History);

        Assert.Equal(AssetHistoryEntryType.Registered, entry.EntryType);
        Assert.Null(entry.FromStatus);
        Assert.Equal(AssetStatus.InStorage, entry.ToStatus);
        Assert.Equal(AssetCondition.Excellent, entry.ToCondition);
        Assert.Equal("subject-1", entry.ActorId);
        Assert.Equal("Ray Manglona", entry.ActorName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_asset_with_no_name_cannot_be_registered(string name) =>
        Assert.Throws<AssetValidationException>(() =>
            Asset.Register("AST-000001", AssetClass.Pole, name, Engineer, Now));

    [Fact]
    public void An_asset_with_no_tag_cannot_be_registered() =>
        Assert.Throws<AssetValidationException>(() =>
            Asset.Register("  ", AssetClass.Pole, "Pole R-0472", Engineer, Now));

    [Fact]
    public void A_class_cast_from_an_unmapped_number_is_refused() =>
        // It would be stored by name as "99" and read back as nothing anyone can act on.
        Assert.Throws<AssetValidationException>(() =>
            Asset.Register("AST-000001", (AssetClass)99, "Mystery plant", Engineer, Now));

    [Fact]
    public void An_install_date_in_the_future_is_refused()
    {
        // A register records what exists. A future date is a typo — or a planned job, which is a
        // work order, not an asset record.
        var tomorrow = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(1);

        Assert.Throws<AssetValidationException>(() => Register(installedOn: tomorrow));
    }

    [Fact]
    public void An_install_date_of_today_is_accepted() =>
        Assert.Equal(
            DateOnly.FromDateTime(Now.UtcDateTime),
            Register(installedOn: DateOnly.FromDateTime(Now.UtcDateTime)).InstalledOn);

    [Fact]
    public void A_position_is_read_back_as_the_pair_that_was_set()
    {
        var asset = Register(position: GeoPosition.Create(14.140833m, 145.184722m));

        Assert.Equal(new GeoPosition(14.140833m, 145.184722m), asset.Position);
        Assert.Equal(14.140833m, asset.Latitude);
        Assert.Equal(145.184722m, asset.Longitude);
    }

    [Fact]
    public void An_asset_nobody_has_located_has_no_position()
    {
        var asset = Register();

        Assert.Null(asset.Position);
        Assert.Null(asset.Latitude);
        Assert.Null(asset.Longitude);
    }

    [Fact]
    public void Free_text_is_cleaned_the_way_every_registry_cleans_it()
    {
        var asset = Asset.Register(
            "AST-000001",
            AssetClass.Pole,
            "  Pole R-0472  ",
            Engineer,
            Now,
            serialNumber: "   ",
            locationNote: "  third pole past the church  ");

        Assert.Equal("Pole R-0472", asset.Name);
        Assert.Null(asset.SerialNumber);
        Assert.Equal("third pole past the church", asset.LocationNote);
    }

    [Fact]
    public void Correcting_details_leaves_the_tag_alone()
    {
        // It is stencilled on the plant and quoted by every work order raised against it.
        var asset = Register();

        asset.UpdateDetails(AssetClass.Recloser, "Sinapalo Feeder Recloser R-1", Now);

        Assert.Equal("AST-000001", asset.AssetTag);
        Assert.Equal(AssetClass.Recloser, asset.Class);
        Assert.Equal("Sinapalo Feeder Recloser R-1", asset.Name);
    }

    [Fact]
    public void A_rejected_correction_leaves_the_asset_exactly_as_it_was()
    {
        // Every guard runs before the first assignment, so a half-applied edit is unreachable.
        var asset = Register();
        var tomorrow = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(1);

        Assert.Throws<AssetValidationException>(() =>
            asset.UpdateDetails(AssetClass.Vehicle, "Bucket Truck BT-2", Now, installedOn: tomorrow));

        Assert.Equal(AssetClass.Transformer, asset.Class);
        Assert.Equal("Songsong Substation Transformer T-3", asset.Name);
    }

    [Fact]
    public void Correcting_details_does_not_add_a_history_line()
    {
        // Fixing a typo in a model designation is not something that happened to the plant. The
        // audit trail is where a correction is recorded.
        var asset = Register();

        asset.UpdateDetails(AssetClass.Transformer, "Songsong Substation Transformer T-3", Now, model: "ONAN 1500 kVA");

        Assert.Single(asset.History);
    }

    [Fact]
    public void Installing_an_asset_records_the_move_and_why()
    {
        var asset = Register();

        asset.ChangeStatus(AssetStatus.InService, Engineer, Now.AddDays(1), "Energised on bay 3");

        Assert.Equal(AssetStatus.InService, asset.Status);
        Assert.Equal(Now.AddDays(1), asset.StatusChangedAt);
        Assert.Equal("Energised on bay 3", asset.StatusReason);

        var entry = asset.History.Last();

        Assert.Equal(AssetHistoryEntryType.StatusChanged, entry.EntryType);
        Assert.Equal(AssetStatus.InStorage, entry.FromStatus);
        Assert.Equal(AssetStatus.InService, entry.ToStatus);
        Assert.Equal("Energised on bay 3", entry.Note);
    }

    [Fact]
    public void An_illegal_move_is_refused_and_changes_nothing()
    {
        // Failure path: stock cannot go straight to maintenance — see AssetTransitions.
        var asset = Register();

        var failure = Assert.Throws<AssetWorkflowException>(() =>
            asset.ChangeStatus(AssetStatus.UnderMaintenance, Engineer, Now));

        Assert.Contains("AST-000001", failure.Message, StringComparison.Ordinal);
        Assert.Equal(AssetStatus.InStorage, asset.Status);
        Assert.Single(asset.History);
    }

    [Fact]
    public void Moving_an_asset_to_where_it_already_is_says_so()
    {
        var asset = Register();

        var failure = Assert.Throws<AssetWorkflowException>(() =>
            asset.ChangeStatus(AssetStatus.InStorage, Engineer, Now));

        Assert.Contains("already InStorage", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_moves_out_of_retired()
    {
        var asset = Register();

        asset.ChangeStatus(AssetStatus.Retired, Engineer, Now, "Damaged in storage, scrapped");

        Assert.Empty(asset.AllowedTransitions);
        Assert.False(asset.IsOnTheBooks);
        Assert.Throws<AssetWorkflowException>(() => asset.ChangeStatus(AssetStatus.InStorage, Engineer, Now));
    }

    [Fact]
    public void Grading_an_asset_records_the_finding_and_when()
    {
        var asset = Register();

        asset.AssessCondition(AssetCondition.Poor, Engineer, Now.AddDays(2), "Spalling at the base");

        Assert.Equal(AssetCondition.Poor, asset.Condition);
        Assert.Equal(Now.AddDays(2), asset.ConditionAssessedAt);

        var entry = asset.History.Last();

        Assert.Equal(AssetHistoryEntryType.ConditionAssessed, entry.EntryType);
        Assert.Equal(AssetCondition.Unknown, entry.FromCondition);
        Assert.Equal(AssetCondition.Poor, entry.ToCondition);
        Assert.Equal("Spalling at the base", entry.Note);
    }

    [Fact]
    public void A_grade_that_has_not_changed_is_still_recorded()
    {
        // "Inspected, still Fair" is the finding a maintenance plan is built on. Dropping it would
        // make an inspected asset indistinguishable from one nobody has looked at since last year.
        var asset = Register(condition: AssetCondition.Fair);

        asset.AssessCondition(AssetCondition.Fair, Engineer, Now.AddYears(1), "Annual inspection, no change");

        Assert.Equal(2, asset.History.Count);
        Assert.Equal(Now.AddYears(1), asset.ConditionAssessedAt);
    }

    [Fact]
    public void A_grade_may_improve_as_well_as_worsen()
    {
        // Not a state machine: plant is repaired. Every direction is legal, by design.
        var asset = Register(condition: AssetCondition.Critical);

        asset.AssessCondition(AssetCondition.Good, Engineer, Now.AddDays(30), "Gasket replaced, leak cleared");

        Assert.Equal(AssetCondition.Good, asset.Condition);
    }

    [Fact]
    public void An_undeclared_grade_is_refused() =>
        Assert.Throws<AssetValidationException>(() =>
            Register().AssessCondition((AssetCondition)42, Engineer, Now));

    [Fact]
    public void Maintenance_is_recorded_against_the_asset_with_the_job_it_was_done_under()
    {
        // WP-3.4's line, and the reason the read model exists in WP-1.3.
        var asset = Register();
        var workOrderId = Guid.CreateVersion7(Now);

        asset.ChangeStatus(AssetStatus.InService, Engineer, Now.AddDays(1));
        asset.RecordMaintenance(workOrderId, "Bushings cleaned, oil sample taken", Engineer, Now.AddDays(2));

        var entry = asset.History.Last();

        Assert.Equal(AssetHistoryEntryType.Maintenance, entry.EntryType);
        Assert.Equal(workOrderId, entry.WorkOrderId);
        Assert.Equal("Bushings cleaned, oil sample taken", entry.Note);
        Assert.Null(entry.ToStatus);
    }

    [Fact]
    public void Maintenance_cannot_be_booked_against_retired_plant()
    {
        // Failure path: the cost would land on a job nobody can go and look at.
        var asset = Register();

        asset.ChangeStatus(AssetStatus.Retired, Engineer, Now, "Scrapped");

        Assert.Throws<AssetValidationException>(() =>
            asset.RecordMaintenance(Guid.CreateVersion7(Now), "Anything at all", Engineer, Now));
    }

    [Fact]
    public void A_maintenance_line_with_no_summary_is_refused() =>
        Assert.Throws<AssetValidationException>(() =>
            Register().RecordMaintenance(workOrderId: null, "   ", Engineer, Now));

    [Fact]
    public void A_history_line_must_name_who_made_the_change() =>
        // The system actor has an id; an actor with a blank one is a bug, and a service record
        // nobody signed is worth less than no record at all.
        Assert.Throws<AssetValidationException>(() =>
            Asset.Register("AST-000001", AssetClass.Pole, "Pole R-0472", new RegistryActor("  ", null), Now));
}
