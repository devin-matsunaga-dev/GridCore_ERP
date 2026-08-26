using GridCore.Contracts.Services;

namespace GridCore.Contracts.UnitTests.Services;

/// <summary>
/// The service vocabulary every module shares (WP-2.17). What these prove: the list is what the
/// deposit schedule, the tariff catalogue and the meter guard all key on, and exactly one member is
/// unmetered — which is the fact three modules act on rather than each deciding for itself.
/// </summary>
public class ServiceTypeTests
{
    [Fact]
    public void The_four_services_GridCore_declares_are_the_ones_the_reference_names() =>
        Assert.Equal(
            [ServiceType.Electricity, ServiceType.Water, ServiceType.Gas, ServiceType.Wastewater],
            ServiceTypes.All);

    [Theory]
    [InlineData(ServiceType.Electricity)]
    [InlineData(ServiceType.Water)]
    [InlineData(ServiceType.Gas)]
    public void A_metered_service_is_measured_by_a_device_at_the_premise(ServiceType serviceType) =>
        Assert.True(ServiceTypes.IsMetered(serviceType));

    [Fact]
    public void Wastewater_is_the_one_unmetered_service() =>
        // The whole reason WP-2.17 needed a fourth member: there is no wastewater meter, so it is a
        // shape the rest of GridCore has to refuse rather than one it merely never produces.
        Assert.False(ServiceTypes.IsMetered(ServiceType.Wastewater));

    [Fact]
    public void A_value_outside_the_enum_is_not_declared() =>
        // A cast integer arriving from a wire or a stale row: refused by name rather than silently
        // treated as whichever member shares its number.
        Assert.False(ServiceTypes.IsDeclared((ServiceType)99));

    [Fact]
    public void Every_declared_member_says_so() =>
        Assert.All(ServiceTypes.All, serviceType => Assert.True(ServiceTypes.IsDeclared(serviceType)));
}
