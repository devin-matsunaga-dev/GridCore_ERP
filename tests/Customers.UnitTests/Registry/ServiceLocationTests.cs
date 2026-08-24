using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>The premise aggregate and the address value object, without a database.</summary>
public class ServiceLocationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static Address AnAddress() =>
        Address.Create("128 As Nieves Road", "Songsong", "Rota", "MP", postalCode: "96951");

    [Fact]
    public void A_registered_location_is_active_and_carries_its_address()
    {
        var location = ServiceLocation.Register("L-000001", AnAddress(), Now, "Meter on the north wall");

        Assert.True(location.IsActive);
        Assert.Equal("L-000001", location.LocationCode);
        Assert.Equal("Rota", location.Address.Region);
        Assert.Equal(7, location.Id.Version);
    }

    [Fact]
    public void An_address_reads_as_one_line_for_a_list_or_an_event() =>
        Assert.Equal("128 As Nieves Road, Songsong, Rota, 96951", AnAddress().OneLine);

    [Fact]
    public void An_optional_part_is_left_out_of_the_one_line_form_rather_than_showing_as_a_gap() =>
        Assert.Equal(
            "9 Ayuyu Drive, San Roque, Saipan",
            Address.Create("9 Ayuyu Drive", "San Roque", "Saipan", "MP").OneLine);

    [Theory]
    [InlineData("", "Songsong", "Rota", "MP")]
    [InlineData("128 As Nieves Road", " ", "Rota", "MP")]
    [InlineData("128 As Nieves Road", "Songsong", "", "MP")]
    [InlineData("128 As Nieves Road", "Songsong", "Rota", "  ")]
    public void An_incomplete_address_is_refused(string line1, string city, string region, string country) =>
        // Failure path: a crew is dispatched to this address. "Somewhere on Rota" is not a premise.
        Assert.Throws<RegistryValidationException>(() => Address.Create(line1, city, region, country));

    [Fact]
    public void A_location_without_a_code_is_refused() =>
        Assert.Throws<RegistryValidationException>(() => ServiceLocation.Register("  ", AnAddress(), Now));

    [Fact]
    public void Deactivating_a_premise_records_why()
    {
        var location = ServiceLocation.Register("L-000001", AnAddress(), Now);

        location.UpdateDetails(AnAddress(), location.Description, isActive: false, "Structure demolished after the storm.");

        Assert.False(location.IsActive);
        Assert.Equal("Structure demolished after the storm.", location.StatusReason);
    }

    [Fact]
    public void Correcting_an_address_leaves_the_deactivation_reason_alone()
    {
        // A typo fix must not erase why the premise is out of service — the flag did not move, so
        // the reason still describes the state the premise is actually in.
        var location = ServiceLocation.Register("L-000001", AnAddress(), Now);

        location.UpdateDetails(AnAddress(), null, isActive: false, "Structure demolished after the storm.");
        location.UpdateDetails(
            Address.Create("128 As Nieves Rd", "Songsong", "Rota", "MP", postalCode: "96951"),
            null,
            isActive: false,
            statusReason: null);

        Assert.Equal("Structure demolished after the storm.", location.StatusReason);
        Assert.Equal("128 As Nieves Rd", location.Address.Line1);
    }
}
