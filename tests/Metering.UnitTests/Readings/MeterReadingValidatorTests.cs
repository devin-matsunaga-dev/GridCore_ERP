using FluentValidation;
using GridCore.Modules.Metering.Features.Readings;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>Edge validation for the reading register: what a validator can see from the body alone.</summary>
public sealed class MeterReadingValidatorTests
{
    private static IReadOnlyList<string> Failures<TRequest>(IValidator<TRequest> validator, TRequest request) =>
        [.. validator.Validate(request).Errors.Select(failure => failure.PropertyName)];

    [Fact]
    public void A_well_formed_reading_passes() =>
        Assert.Empty(Failures(
            new RecordMeterReadingRequestValidator(),
            new RecordMeterReadingRequest(15_120.750m, Note: "Read off the card")));

    [Fact]
    public void A_reading_with_no_value_passes_because_that_is_a_missing_read() =>
        // Legitimate and load-bearing: refusing it would leave the utility with no evidence that
        // anybody went to the meter at all.
        Assert.Empty(Failures(new RecordMeterReadingRequestValidator(), new RecordMeterReadingRequest(null, Note: "No access")));

    [Fact]
    public void A_negative_reading_is_refused() =>
        // "Reading.Value", not "Reading": the rule is written over the nullable's value, the same
        // shape WP-2.1's installation-reading rules take.
        Assert.Contains(
            $"{nameof(RecordMeterReadingRequest.Reading)}.Value",
            Failures(new RecordMeterReadingRequestValidator(), new RecordMeterReadingRequest(-1m)),
            StringComparer.Ordinal);

    [Fact]
    public void A_reading_finer_than_the_register_stores_is_refused_rather_than_rounded() =>
        Assert.Contains(
            $"{nameof(RecordMeterReadingRequest.Reading)}.Value",
            Failures(new RecordMeterReadingRequestValidator(), new RecordMeterReadingRequest(540.0001m)),
            StringComparer.Ordinal);

    [Fact]
    public void A_note_longer_than_the_column_is_refused() =>
        Assert.Contains(
            nameof(RecordMeterReadingRequest.Note),
            Failures(
                new RecordMeterReadingRequestValidator(),
                new RecordMeterReadingRequest(540m, Note: new string('n', MeterReading.NoteLength + 1))),
            StringComparer.Ordinal);

    [Fact]
    public void A_well_formed_cycle_passes() =>
        Assert.Empty(Failures(new RunReadingCycleRequestValidator(), new RunReadingCycleRequest("2026-08", Seed: 4471)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_cycle_with_no_code_is_refused(string cycleCode) =>
        // The cycle code is the idempotency key behind ux_meter_readings_meter_cycle, so an empty one
        // would let every run collide with every other.
        Assert.Contains(
            nameof(RunReadingCycleRequest.CycleCode),
            Failures(new RunReadingCycleRequestValidator(), new RunReadingCycleRequest(cycleCode)),
            StringComparer.Ordinal);

    [Fact]
    public void A_cycle_code_longer_than_the_column_is_refused() =>
        Assert.Contains(
            nameof(RunReadingCycleRequest.CycleCode),
            Failures(
                new RunReadingCycleRequestValidator(),
                new RunReadingCycleRequest(new string('c', MeterReading.CycleCodeLength + 1))),
            StringComparer.Ordinal);
}
