using FluentValidation;
using GridCore.Modules.Assets.Features.Assets;

namespace GridCore.Modules.Assets.UnitTests.Registry;

/// <summary>
/// Edge validation, which is where a malformed <i>body</i> is refused. Whether a move is legal or a
/// serial is taken depends on the register's current state, which a validator cannot see — those
/// are the service's 409s and are tested there.
/// </summary>
public class AssetValidatorTests
{
    private static IReadOnlyList<string> Failures<TRequest>(IValidator<TRequest> validator, TRequest request) =>
        [.. validator.Validate(request).Errors.Select(failure => failure.PropertyName)];

    private static RegisterAssetRequest Valid() =>
        new(AssetClass.Transformer, "Songsong Substation Transformer T-3");

    [Fact]
    public void A_well_formed_registration_passes() =>
        Assert.Empty(Failures(new RegisterAssetRequestValidator(), Valid()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_asset_needs_a_name(string name) =>
        Assert.NotEmpty(Failures(new RegisterAssetRequestValidator(), Valid() with { Name = name }));

    [Fact]
    public void A_name_longer_than_the_column_is_refused() =>
        Assert.NotEmpty(Failures(
            new RegisterAssetRequestValidator(),
            Valid() with { Name = new string('a', Asset.NameLength + 1) }));

    [Fact]
    public void A_class_cast_from_an_unmapped_number_is_refused() =>
        Assert.NotEmpty(Failures(new RegisterAssetRequestValidator(), Valid() with { Class = (AssetClass)99 }));

    [Theory]
    [InlineData(90.1, 0)]
    [InlineData(-90.1, 0)]
    [InlineData(0, 180.1)]
    [InlineData(0, -180.1)]
    public void A_position_off_the_planet_is_refused_at_the_edge(double latitude, double longitude) =>
        Assert.NotEmpty(Failures(
            new RegisterAssetRequestValidator(),
            Valid() with { Latitude = (decimal)latitude, Longitude = (decimal)longitude }));

    [Theory]
    [InlineData(14.140833, null)]
    [InlineData(null, 145.184722)]
    public void Half_a_position_is_refused_at_the_edge(double? latitude, double? longitude) =>
        // Caught here as a 400 naming the field rather than reaching the aggregate, which would
        // answer the same failure as a 400 out of a deeper layer.
        Assert.Contains(
            "latitude",
            Failures(
                new RegisterAssetRequestValidator(),
                Valid() with { Latitude = (decimal?)latitude, Longitude = (decimal?)longitude }));

    [Fact]
    public void A_complete_position_passes() =>
        Assert.Empty(Failures(
            new RegisterAssetRequestValidator(),
            Valid() with { Latitude = 14.140833m, Longitude = 145.184722m }));

    [Fact]
    public void An_undeclared_status_is_refused() =>
        Assert.NotEmpty(Failures(
            new ChangeAssetStatusRequestValidator(),
            new ChangeAssetStatusRequest((AssetStatus)99)));

    [Fact]
    public void A_declared_status_passes_whether_or_not_the_move_is_legal() =>
        // Deliberate: legality depends on where the asset is now, and that answer is a 409 from the
        // aggregate. A validator that guessed would refuse legal moves.
        Assert.Empty(Failures(
            new ChangeAssetStatusRequestValidator(),
            new ChangeAssetStatusRequest(AssetStatus.UnderMaintenance, "Withdrawn for gasket replacement")));

    [Fact]
    public void A_reason_longer_than_the_column_is_refused() =>
        Assert.NotEmpty(Failures(
            new ChangeAssetStatusRequestValidator(),
            new ChangeAssetStatusRequest(AssetStatus.InService, new string('a', Asset.ReasonLength + 1))));

    [Fact]
    public void An_undeclared_condition_is_refused() =>
        Assert.NotEmpty(Failures(
            new AssessAssetConditionRequestValidator(),
            new AssessAssetConditionRequest((AssetCondition)42)));

    [Fact]
    public void Any_declared_condition_passes() =>
        // No transition rule at all: plant is repaired and plant weathers storms.
        Assert.All(
            Enum.GetValues<AssetCondition>(),
            condition => Assert.Empty(Failures(
                new AssessAssetConditionRequestValidator(),
                new AssessAssetConditionRequest(condition))));

    [Fact]
    public void A_serial_number_longer_than_the_column_is_refused() =>
        Assert.NotEmpty(Failures(
            new UpdateAssetRequestValidator(),
            new UpdateAssetRequest(AssetClass.Pole, "Pole R-0472", SerialNumber: new string('a', Asset.SerialNumberLength + 1))));
}
