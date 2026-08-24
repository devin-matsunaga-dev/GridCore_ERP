using FluentValidation;

namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>
/// The field rules shared by registering and correcting an asset, over
/// <see cref="IAssetDetails"/> so neither DTO has to stand in for the other.
/// </summary>
/// <typeparam name="TRequest">The request body being validated.</typeparam>
public abstract class AssetDetailsValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : IAssetDetails
{
    /// <summary>Builds the rules.</summary>
    protected AssetDetailsValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(Asset.NameLength);
        RuleFor(request => request.Class).IsInEnum();
        RuleFor(request => request.SerialNumber!).MaximumLength(Asset.SerialNumberLength);
        RuleFor(request => request.Manufacturer!).MaximumLength(Asset.ModelLength);
        RuleFor(request => request.Model!).MaximumLength(Asset.ModelLength);
        RuleFor(request => request.LocationNote!).MaximumLength(Asset.LocationNoteLength);

        RuleFor(request => request.Latitude!.Value)
            .InclusiveBetween(-90m, 90m)
            .When(request => request.Latitude.HasValue);

        RuleFor(request => request.Longitude!.Value)
            .InclusiveBetween(-180m, 180m)
            .When(request => request.Longitude.HasValue);

        // Both or neither. A latitude on its own is a line of latitude, not a place — and a crew
        // sent to one would be driving round the island looking for a pole.
        RuleFor(request => request)
            .Must(request => request.Latitude.HasValue == request.Longitude.HasValue)
            .WithName("latitude")
            .WithMessage("A position needs both 'latitude' and 'longitude'; one on its own is a line, not a place.");
    }
}

/// <summary>Rules for entering an asset in the register.</summary>
/// <remarks>
/// The status and condition an asset may be registered <i>in</i> are validated as declared values
/// only. Nothing here can be a transition rule, because a new asset has no previous state to move
/// from — the lifecycle guard applies from the second write onwards.
/// </remarks>
public sealed class RegisterAssetRequestValidator : AssetDetailsValidator<RegisterAssetRequest>
{
    /// <summary>Builds the rules.</summary>
    public RegisterAssetRequestValidator()
    {
        RuleFor(request => request.Status).IsInEnum();
        RuleFor(request => request.Condition).IsInEnum();
        RuleFor(request => request.Note!).MaximumLength(Asset.ReasonLength);
    }
}

/// <summary>Rules for correcting an asset's details.</summary>
public sealed class UpdateAssetRequestValidator : AssetDetailsValidator<UpdateAssetRequest>;

/// <summary>Rules for moving an asset through its lifecycle.</summary>
/// <remarks>
/// Only that the status is one GridCore declares. Whether the move is <i>legal</i> depends on where
/// the asset is now, which the validator cannot see and <see cref="AssetTransitions"/> can — so
/// that answer is a 409 from the aggregate, not a 400 from here.
/// </remarks>
public sealed class ChangeAssetStatusRequestValidator : AbstractValidator<ChangeAssetStatusRequest>
{
    /// <summary>Builds the rules.</summary>
    public ChangeAssetStatusRequestValidator()
    {
        RuleFor(request => request.Status).IsInEnum();
        RuleFor(request => request.Reason!).MaximumLength(Asset.ReasonLength);
    }
}

/// <summary>Rules for recording an inspector's grading.</summary>
/// <remarks>
/// No transition rule at all, and that is deliberate: any grade may follow any other, because plant
/// is repaired and plant weathers storms. See <see cref="AssetCondition"/>.
/// </remarks>
public sealed class AssessAssetConditionRequestValidator : AbstractValidator<AssessAssetConditionRequest>
{
    /// <summary>Builds the rules.</summary>
    public AssessAssetConditionRequestValidator()
    {
        RuleFor(request => request.Condition).IsInEnum();
        RuleFor(request => request.Note!).MaximumLength(Asset.ReasonLength);
    }
}
