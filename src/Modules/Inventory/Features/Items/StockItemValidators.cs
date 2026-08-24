using FluentValidation;

namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>
/// The field rules shared by registering and correcting a catalogue line, over
/// <see cref="IStockItemDetails"/> so neither DTO has to stand in for the other.
/// </summary>
/// <typeparam name="TRequest">The request body being validated.</typeparam>
public abstract class StockItemDetailsValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : IStockItemDetails
{
    /// <summary>Builds the rules.</summary>
    protected StockItemDetailsValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(StockItem.NameLength);
        RuleFor(request => request.Category).IsInEnum();
        RuleFor(request => request.Unit).IsInEnum();
        RuleFor(request => request.Description!).MaximumLength(StockItem.DescriptionLength);
        RuleFor(request => request.ManufacturerPartNumber!).MaximumLength(StockItem.PartNumberLength);

        // Only that it is not negative. Whether it is finer than a cent is the aggregate's answer,
        // because that rule and the column it protects belong together — see StockCosts.
        RuleFor(request => request.UnitCost).GreaterThanOrEqualTo(0m);
    }
}

/// <summary>Rules for entering an item in the catalogue.</summary>
public sealed class RegisterStockItemRequestValidator : StockItemDetailsValidator<RegisterStockItemRequest>;

/// <summary>Rules for correcting a catalogue line.</summary>
/// <remarks>
/// Nothing here can say whether the unit of measure may change — that depends on whether stock has
/// already moved, which a validator cannot see and the aggregate can, so it answers 409.
/// </remarks>
public sealed class UpdateStockItemRequestValidator : StockItemDetailsValidator<UpdateStockItemRequest>
{
    /// <summary>Builds the rules.</summary>
    public UpdateStockItemRequestValidator()
    {
        RuleFor(request => request.StatusReason!).MaximumLength(StockItem.ReasonLength);
    }
}

/// <summary>The rules every movement body shares.</summary>
/// <typeparam name="TRequest">The request body being validated.</typeparam>
public abstract class StockMovementValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : IStockMovementRequest
{
    /// <summary>Builds the rules.</summary>
    protected StockMovementValidator()
    {
        RuleFor(request => request.WarehouseId)
            .NotEmpty()
            .WithMessage("'warehouseId' is required; stock is always somewhere.");
    }
}

/// <summary>Rules for booking stock in.</summary>
/// <remarks>
/// The quantity is checked for being positive here <i>and</i> in the aggregate. Not redundant: this
/// one makes a mistyped delivery read as a 400 naming the field, and the aggregate's protects the
/// seeder and WP-4.1's receiving, which never pass through a validator.
/// </remarks>
public sealed class ReceiveStockRequestValidator : StockMovementValidator<ReceiveStockRequest>
{
    /// <summary>Builds the rules.</summary>
    public ReceiveStockRequestValidator()
    {
        RuleFor(request => request.Quantity).GreaterThan(0m);
        RuleFor(request => request.UnitCost!.Value).GreaterThanOrEqualTo(0m).When(request => request.UnitCost.HasValue);
        RuleFor(request => request.Reference!).MaximumLength(StockMovement.ReferenceLength);
        RuleFor(request => request.Note!).MaximumLength(StockMovement.NoteLength);
    }
}

/// <summary>Rules for issuing stock to a job.</summary>
public sealed class IssueStockRequestValidator : StockMovementValidator<IssueStockRequest>
{
    /// <summary>Builds the rules.</summary>
    public IssueStockRequestValidator()
    {
        RuleFor(request => request.Quantity).GreaterThan(0m);
        RuleFor(request => request.Reference!).MaximumLength(StockMovement.ReferenceLength);
        RuleFor(request => request.Note!).MaximumLength(StockMovement.NoteLength);
    }
}

/// <summary>Rules for correcting a count.</summary>
/// <remarks>
/// The reason is required here and again in the movement itself. Invariant 5 is the whole point of
/// this endpoint: an adjustment moves stock with nothing physically moving, so an unexplained one is
/// the write an auditor comes looking for, and it must be impossible from either direction.
/// </remarks>
public sealed class AdjustStockRequestValidator : StockMovementValidator<AdjustStockRequest>
{
    /// <summary>Builds the rules.</summary>
    public AdjustStockRequestValidator()
    {
        RuleFor(request => request.CountedQuantity).GreaterThanOrEqualTo(0m);
        RuleFor(request => request.Reason).NotEmpty().MaximumLength(StockItem.ReasonLength);
    }
}

/// <summary>Rules for setting a reorder level.</summary>
public sealed class SetMinimumQuantityRequestValidator : StockMovementValidator<SetMinimumQuantityRequest>
{
    /// <summary>Builds the rules.</summary>
    public SetMinimumQuantityRequestValidator()
    {
        // Zero is allowed and means "no reorder level", which is how one is cleared.
        RuleFor(request => request.MinimumQuantity).GreaterThanOrEqualTo(0m);
    }
}
