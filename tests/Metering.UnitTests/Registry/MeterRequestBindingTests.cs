using System.Text.Json;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Platform.Serialization;

namespace GridCore.Modules.Metering.UnitTests.Registry;

/// <summary>
/// The bodies the SPA actually sends, read with the host's own JSON conventions — the metering
/// half of what <c>RegistryRequestBindingTests</c> covers for Customers, and for the same reason:
/// a request DTO carrying an enum was unreadable from a browser until the host was taught to read
/// an enum's name, and no test had ever crossed JSON in that direction.
/// </summary>
public class MeterRequestBindingTests
{
    private static readonly JsonSerializerOptions Options = GridCoreJson.Options();

    [Fact]
    public void A_meter_is_registered_from_the_type_name_the_API_returns()
    {
        var body = JsonSerializer.Deserialize<RegisterMeterRequest>(
            """{"serialNumber":"SEN-E2E-0001","type":"SinglePhase","manufacturer":"Sensus","note":"New connection"}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal("SEN-E2E-0001", body.SerialNumber);
        Assert.Equal(MeterType.SinglePhase, body.Type);

        // The register width defaults rather than arriving as zero, which no arithmetic can run on.
        Assert.Equal(Meter.DefaultRegisterDigits, body.RegisterDigits);
    }

    [Fact]
    public void A_meter_is_fitted_from_a_premise_and_the_dials_it_went_on_at()
    {
        var locationId = Guid.CreateVersion7();

        var body = JsonSerializer.Deserialize<AssignMeterRequest>(
            $$"""{"serviceLocationId":"{{locationId}}","installationReading":4200,"note":"Meter set on the north wall"}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal(locationId, body.ServiceLocationId);
        Assert.Equal(4200m, body.InstallationReading);
    }

    [Fact]
    public void A_reading_cycle_is_run_from_its_code_and_seed()
    {
        var body = JsonSerializer.Deserialize<RunReadingCycleRequest>(
            """{"cycleCode":"DEMO-20260825-0930","seed":4471}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal("DEMO-20260825-0930", body.CycleCode);
        Assert.Equal(4471, body.Seed);
    }

    [Fact]
    public void A_reading_is_recorded_as_a_decimal_and_not_as_a_double()
    {
        // numeric(18,3), and the wire must not round it on the way in.
        var body = JsonSerializer.Deserialize<RecordMeterReadingRequest>(
            """{"reading":15267.503,"note":"Read off the card"}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal(15267.503m, body.Reading);
    }

    [Fact]
    public void A_meter_type_that_is_not_one_of_ours_is_refused_rather_than_defaulted()
    {
        // Failure path, and SinglePhase is the zero member: a defaulted value would register a
        // current-transformer installation as a domestic meter and read it that way for ever.
        var thrown = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegisterMeterRequest>(
            """{"serialNumber":"SEN-E2E-0001","type":"Hydraulic"}""",
            Options));

        Assert.Equal("$.type", thrown.Path);
    }
}
