using GridCore.Modules.Assets.Features.Assets;
using GridCore.Modules.Assets.Features.Shared;

namespace GridCore.Modules.Assets.UnitTests.Registry;

/// <summary>Where a piece of plant stands, as a value.</summary>
public class GeoPositionTests
{
    [Fact]
    public void A_position_on_Rota_is_accepted()
    {
        var position = GeoPosition.Create(14.140833m, 145.184722m);

        Assert.Equal(14.140833m, position.Latitude);
        Assert.Equal(145.184722m, position.Longitude);
    }

    [Theory]
    [InlineData(-90, -180)]
    [InlineData(90, 180)]
    [InlineData(0, 0)]
    public void The_edges_of_the_planet_are_on_it(double latitude, double longitude) =>
        Assert.Equal(
            new GeoPosition((decimal)latitude, (decimal)longitude),
            GeoPosition.Create((decimal)latitude, (decimal)longitude));

    [Theory]
    [InlineData(90.000001, 0)]
    [InlineData(-90.000001, 0)]
    [InlineData(0, 180.000001)]
    [InlineData(0, -180.000001)]
    public void A_position_off_the_planet_is_refused(double latitude, double longitude) =>
        Assert.Throws<AssetValidationException>(() =>
            GeoPosition.Create((decimal)latitude, (decimal)longitude));

    [Fact]
    public void A_coordinate_finer_than_the_column_is_refused_rather_than_rounded() =>
        // Same rule as WP-1.1's deposit: numeric(9,6) would have truncated silently to a position
        // nobody surveyed.
        Assert.Throws<AssetValidationException>(() => GeoPosition.Create(14.1408331m, 145.184722m));

    [Fact]
    public void Neither_half_of_a_pair_is_no_position() =>
        Assert.Null(GeoPosition.From(null, null));

    [Theory]
    [InlineData(14.140833, null)]
    [InlineData(null, 145.184722)]
    public void Half_a_pair_is_refused(double? latitude, double? longitude) =>
        // The failure this type exists for: a latitude on its own is a line of latitude, and a crew
        // sent to one would be driving round the island looking for a pole.
        Assert.Throws<AssetValidationException>(() =>
            GeoPosition.From((decimal?)latitude, (decimal?)longitude));

    [Fact]
    public void Both_halves_make_a_position() =>
        Assert.Equal(
            new GeoPosition(14.140833m, 145.184722m),
            GeoPosition.From(14.140833m, 145.184722m));

    [Fact]
    public void Two_positions_at_the_same_place_are_the_same_value() =>
        // A value, not an entity — which is what lets a test compare one without an id.
        Assert.Equal(GeoPosition.Create(14.14m, 145.18m), GeoPosition.Create(14.14m, 145.18m));

    [Fact]
    public void A_position_renders_as_an_operator_would_write_it() =>
        Assert.Equal("14.140833, 145.184722", GeoPosition.Create(14.140833m, 145.184722m).ToString());
}
