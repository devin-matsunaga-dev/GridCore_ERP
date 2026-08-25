using FluentValidation;

namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>Rules for recording one reading by hand.</summary>
/// <remarks>
/// Only what a validator can see from the body alone. Whether the meter is fitted, whether the
/// reading fits its register and whether it is dated before the last one all depend on rows this
/// edge cannot read — they are the aggregate's 400s and 409s.
/// </remarks>
public sealed class RecordMeterReadingRequestValidator : AbstractValidator<RecordMeterReadingRequest>
{
    /// <summary>Builds the rules.</summary>
    public RecordMeterReadingRequestValidator()
    {
        // A null reading is legitimate and means "the meter could not be read": a missing read is a
        // real outcome, and refusing to record it would leave the utility with no evidence anybody
        // went. A negative one is not.
        RuleFor(request => request.Reading!.Value)
            .GreaterThanOrEqualTo(0m)
            .When(request => request.Reading.HasValue)
            .WithMessage("A meter reading cannot be negative.");

        // Refused rather than rounded, as every value finer than its column has been since WP-1.1.
        RuleFor(request => request.Reading!.Value)
            .Must(reading => decimal.Round(reading, MeterReading.DecimalPlaces) == reading)
            .When(request => request.Reading.HasValue)
            .WithMessage($"A meter reading is stored to {MeterReading.DecimalPlaces} decimal places.");

        RuleFor(request => request.Note!).MaximumLength(MeterReading.NoteLength);
    }
}

/// <summary>Rules for running a reading cycle.</summary>
public sealed class RunReadingCycleRequestValidator : AbstractValidator<RunReadingCycleRequest>
{
    /// <summary>Builds the rules.</summary>
    public RunReadingCycleRequestValidator()
    {
        // The cycle code is the idempotency key behind ux_meter_readings_meter_cycle, so an empty
        // one would let every run collide with every other.
        RuleFor(request => request.CycleCode).NotEmpty().MaximumLength(MeterReading.CycleCodeLength);
    }
}
