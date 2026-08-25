using FluentValidation;
using GridCore.Modules.Metering.Features.Readings;

namespace GridCore.Modules.Metering.Features.Meters;

/// <summary>
/// The field rules shared by registering and correcting a meter, over <see cref="IMeterDetails"/>
/// so neither DTO has to stand in for the other.
/// </summary>
/// <typeparam name="TRequest">The request body being validated.</typeparam>
public abstract class MeterDetailsValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : IMeterDetails
{
    /// <summary>Builds the rules.</summary>
    protected MeterDetailsValidator()
    {
        // Required, unlike an asset's serial: every meter carries one stamped on the device, and it
        // is what identifies it when the utility's own plate has weathered off.
        RuleFor(request => request.SerialNumber).NotEmpty().MaximumLength(Meter.SerialNumberLength);
        RuleFor(request => request.Type).IsInEnum();

        // A width outside this range is not a meter GridCore can compute a rollover for. The
        // aggregate refuses it too — this is here so a mistyped nameplate reads as a 400 rather than
        // reaching the register at all.
        RuleFor(request => request.RegisterDigits)
            .InclusiveBetween(ConsumptionCalculator.MinRegisterDigits, ConsumptionCalculator.MaxRegisterDigits);

        RuleFor(request => request.Manufacturer!).MaximumLength(Meter.ModelLength);
        RuleFor(request => request.Model!).MaximumLength(Meter.ModelLength);
    }
}

/// <summary>Rules for entering a meter in the register.</summary>
/// <remarks>
/// There is no status to validate: a meter is always registered into stock, because fitting one is
/// an act with a premise, a reading and a reason that only <c>POST /assign</c> can supply.
/// </remarks>
public sealed class RegisterMeterRequestValidator : MeterDetailsValidator<RegisterMeterRequest>
{
    /// <summary>Builds the rules.</summary>
    public RegisterMeterRequestValidator() =>
        RuleFor(request => request.Note!).MaximumLength(Meter.ReasonLength);
}

/// <summary>Rules for correcting a meter's device details.</summary>
public sealed class UpdateMeterRequestValidator : MeterDetailsValidator<UpdateMeterRequest>;

/// <summary>Rules for fitting a meter at a premise.</summary>
public sealed class AssignMeterRequestValidator : AbstractValidator<AssignMeterRequest>
{
    /// <summary>Builds the rules.</summary>
    public AssignMeterRequestValidator()
    {
        // Whether the premise exists is the service's question, not this one's: it lives in another
        // module's registry, which no validator at this edge can see. All that is checked here is
        // that the caller named one at all.
        RuleFor(request => request.ServiceLocationId).NotEmpty();

        RuleFor(request => request.InstallationReading!.Value)
            .GreaterThanOrEqualTo(0m)
            .When(request => request.InstallationReading.HasValue)
            .WithMessage("An installation reading cannot be negative.");

        // Refused rather than rounded, as every value finer than its column has been since WP-1.1:
        // CONVENTIONS.md's central rounding helper still has no home (WP-2.3 owns it).
        RuleFor(request => request.InstallationReading!.Value)
            .Must(reading => decimal.Round(reading, Meter.DialDecimalPlaces) == reading)
            .When(request => request.InstallationReading.HasValue)
            .WithMessage($"A meter reading is stored to {Meter.DialDecimalPlaces} decimal places.");

        RuleFor(request => request.Note!).MaximumLength(Meter.ReasonLength);
    }
}

/// <summary>Rules for taking a meter off a premise.</summary>
/// <remarks>
/// The reason is optional, deliberately unlike WP-1.4's stock adjustment. An adjustment moves a
/// count with nothing physically moving, which is why it has to be explained; a removal is a crew
/// taking a device off a wall, and the history line already records who, when and from where.
/// </remarks>
public sealed class RemoveMeterRequestValidator : AbstractValidator<RemoveMeterRequest>
{
    /// <summary>Builds the rules.</summary>
    public RemoveMeterRequestValidator() =>
        RuleFor(request => request.Reason!).MaximumLength(Meter.ReasonLength);
}

/// <summary>Rules for moving a meter through its lifecycle.</summary>
/// <remarks>
/// Only that the status is one GridCore declares. Whether the move is <i>legal</i> depends on where
/// the meter is now, which is the aggregate's 409 rather than a validator's 400.
/// </remarks>
public sealed class ChangeMeterStatusRequestValidator : AbstractValidator<ChangeMeterStatusRequest>
{
    /// <summary>Builds the rules.</summary>
    public ChangeMeterStatusRequestValidator()
    {
        RuleFor(request => request.Status).IsInEnum();
        RuleFor(request => request.Reason!).MaximumLength(Meter.ReasonLength);
    }
}
