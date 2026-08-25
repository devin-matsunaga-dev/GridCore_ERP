using FluentValidation;
using GridCore.Modules.Metering.Features.Meters;

namespace GridCore.Modules.Metering.UnitTests.Registry;

/// <summary>
/// Edge validation: the 400s a caller gets before the register is ever touched. Everything the
/// aggregate refuses with a 409 is deliberately absent here — legality depends on where the meter
/// is now, which a validator cannot see.
/// </summary>
public sealed class MeterValidatorTests
{
    private static IReadOnlyList<string> Failures<TRequest>(AbstractValidator<TRequest> validator, TRequest request) =>
        [.. validator.Validate(request).Errors.Select(failure => failure.PropertyName)];

    [Fact]
    public void A_registration_needs_a_serial_number() =>
        Assert.Contains(
            nameof(RegisterMeterRequest.SerialNumber),
            Failures(new RegisterMeterRequestValidator(), new RegisterMeterRequest("  ", MeterType.SinglePhase)));

    [Fact]
    public void A_registration_needs_a_meter_type_GridCore_declares() =>
        Assert.Contains(
            nameof(RegisterMeterRequest.Type),
            Failures(new RegisterMeterRequestValidator(), new RegisterMeterRequest("SEN-1", (MeterType)99)));

    [Fact]
    public void A_well_formed_registration_passes() =>
        Assert.Empty(Failures(
            new RegisterMeterRequestValidator(),
            new RegisterMeterRequest("SEN-4471102", MeterType.SinglePhase, Manufacturer: "Sensus", Model: "iConA", Note: "September delivery")));

    [Fact]
    public void A_serial_number_longer_than_the_column_is_refused() =>
        Assert.Contains(
            nameof(UpdateMeterRequest.SerialNumber),
            Failures(
                new UpdateMeterRequestValidator(),
                new UpdateMeterRequest(new string('x', Meter.SerialNumberLength + 1), MeterType.SinglePhase)));

    [Fact]
    public void An_assignment_needs_a_premise() =>
        Assert.Contains(
            nameof(AssignMeterRequest.ServiceLocationId),
            Failures(new AssignMeterRequestValidator(), new AssignMeterRequest(Guid.Empty)));

    [Fact]
    public void An_assignment_may_carry_no_reading_at_all() =>
        // A crew that did not write the dials down is a gap WP-2.2 has to handle, not a bad request.
        Assert.Empty(Failures(new AssignMeterRequestValidator(), new AssignMeterRequest(Guid.CreateVersion7())));

    [Fact]
    public void A_negative_installation_reading_is_refused() =>
        Assert.NotEmpty(Failures(
            new AssignMeterRequestValidator(),
            new AssignMeterRequest(Guid.CreateVersion7(), -0.001m)));

    [Fact]
    public void A_reading_finer_than_the_register_is_refused_rather_than_rounded() =>
        Assert.NotEmpty(Failures(
            new AssignMeterRequestValidator(),
            new AssignMeterRequest(Guid.CreateVersion7(), 12.0001m)));

    [Fact]
    public void A_reading_at_the_registers_own_precision_passes() =>
        Assert.Empty(Failures(
            new AssignMeterRequestValidator(),
            new AssignMeterRequest(Guid.CreateVersion7(), 14_820.500m)));

    [Fact]
    public void A_removal_does_not_have_to_be_explained() =>
        // Deliberately unlike WP-1.4's stock adjustment, which does: an adjustment moves a count
        // with nothing physically moving. A removal is a crew taking a device off a wall, and the
        // history line already records who, when and from where.
        Assert.Empty(Failures(new RemoveMeterRequestValidator(), new RemoveMeterRequest()));

    [Fact]
    public void A_status_change_needs_a_status_GridCore_declares() =>
        Assert.Contains(
            nameof(ChangeMeterStatusRequest.Status),
            Failures(new ChangeMeterStatusRequestValidator(), new ChangeMeterStatusRequest((MeterStatus)0)));

    [Fact]
    public void A_status_change_to_a_declared_status_passes_edge_validation_even_when_it_is_illegal() =>
        // Installed is never reachable through the status endpoint, but that is the aggregate's
        // 409 to give: whether it is legal depends on where this meter is, which is state a
        // validator has no access to. A 400 here would say the request was malformed, which it is not.
        Assert.Empty(Failures(new ChangeMeterStatusRequestValidator(), new ChangeMeterStatusRequest(MeterStatus.Installed)));
}
