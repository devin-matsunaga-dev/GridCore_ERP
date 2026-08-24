using FluentValidation;
using GridCore.Modules.Inventory.Features.Items;

namespace GridCore.Modules.Inventory.UnitTests.Items;

/// <summary>
/// Edge validation. These are the answers a caller gets as a 400 naming the field, before any rule
/// that depends on where the stock currently is.
/// </summary>
public class StockItemValidatorTests
{
    private static RegisterStockItemRequest Conductor(string name = "ACSR Raven 1/0 conductor") =>
        new(StockItemCategory.Conductor, name, UnitOfMeasure.Metre, UnitCost: 4.85m);

    private static AdjustStockRequest Adjustment(string reason, decimal counted = 32m) =>
        new(Guid.CreateVersion7(DateTimeOffset.UnixEpoch), counted, reason);

    private static IReadOnlyList<string> Failures<TRequest>(AbstractValidator<TRequest> validator, TRequest request) =>
        [.. validator.Validate(request).Errors.Select(failure => failure.PropertyName)];

    [Fact]
    public void A_well_formed_registration_passes() =>
        Assert.Empty(Failures(new RegisterStockItemRequestValidator(), Conductor()));

    [Fact]
    public void An_item_needs_a_name() =>
        Assert.Contains("name", Failures(new RegisterStockItemRequestValidator(), Conductor("  ")), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void An_undeclared_category_or_unit_is_a_400()
    {
        var validator = new RegisterStockItemRequestValidator();

        Assert.NotEmpty(validator.Validate(Conductor() with { Category = (StockItemCategory)99 }).Errors);
        Assert.NotEmpty(validator.Validate(Conductor() with { Unit = (UnitOfMeasure)99 }).Errors);
    }

    [Fact]
    public void A_negative_cost_is_a_400() =>
        Assert.NotEmpty(new RegisterStockItemRequestValidator().Validate(Conductor() with { UnitCost = -1m }).Errors);

    [Fact]
    public void A_movement_needs_a_warehouse()
    {
        // Stock is always somewhere. An empty warehouse id would otherwise reach the service and come
        // back as a 404 for a warehouse nobody named.
        var failures = Failures(new ReceiveStockRequestValidator(), new ReceiveStockRequest(Guid.Empty, 10m));

        Assert.Contains("WarehouseId", failures, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_receipt_or_an_issue_of_nothing_is_a_400(decimal quantity)
    {
        var warehouse = Guid.CreateVersion7(DateTimeOffset.UnixEpoch);

        Assert.NotEmpty(new ReceiveStockRequestValidator().Validate(new ReceiveStockRequest(warehouse, quantity)).Errors);
        Assert.NotEmpty(new IssueStockRequestValidator().Validate(new IssueStockRequest(warehouse, quantity)).Errors);
    }

    [Fact]
    public void An_adjustment_without_a_reason_is_a_400() =>
        // Invariant 5, refused from the outside as well as from the inside: this is the one endpoint
        // where an unexplained write is the whole risk.
        Assert.NotEmpty(new AdjustStockRequestValidator().Validate(Adjustment("   ")).Errors);

    [Fact]
    public void An_adjustment_to_a_negative_count_is_a_400() =>
        Assert.NotEmpty(new AdjustStockRequestValidator().Validate(Adjustment("Stock take", -1m)).Errors);

    [Fact]
    public void An_adjustment_to_zero_is_allowed_here() =>
        // Counting an empty shelf is a real finding; whether it is a *change* is the aggregate's
        // question, and it answers 409 when it is not.
        Assert.Empty(new AdjustStockRequestValidator().Validate(Adjustment("Shelf found empty", 0m)).Errors);

    [Fact]
    public void A_reorder_level_of_zero_is_allowed_and_a_negative_one_is_not()
    {
        var validator = new SetMinimumQuantityRequestValidator();
        var warehouse = Guid.CreateVersion7(DateTimeOffset.UnixEpoch);

        // Zero is how a reorder level is cleared.
        Assert.Empty(validator.Validate(new SetMinimumQuantityRequest(warehouse, 0m)).Errors);
        Assert.NotEmpty(validator.Validate(new SetMinimumQuantityRequest(warehouse, -1m)).Errors);
    }

    [Fact]
    public void Over_long_text_is_refused_before_it_reaches_a_column()
    {
        var validator = new RegisterStockItemRequestValidator();

        Assert.NotEmpty(validator.Validate(Conductor(new string('x', StockItem.NameLength + 1))).Errors);
        Assert.NotEmpty(validator.Validate(Conductor() with { Description = new string('x', StockItem.DescriptionLength + 1) }).Errors);
        Assert.NotEmpty(validator.Validate(Conductor() with
        {
            ManufacturerPartNumber = new string('x', StockItem.PartNumberLength + 1),
        }).Errors);
    }

    [Fact]
    public void Nothing_in_the_update_validator_judges_the_unit_of_measure() =>
        // It cannot: whether the unit may change depends on whether stock has already moved, which
        // only the aggregate can see — so that answer is a 409, not a 400.
        Assert.Empty(new UpdateStockItemRequestValidator().Validate(new UpdateStockItemRequest(
            StockItemCategory.Conductor,
            "ACSR Raven 1/0 conductor",
            UnitOfMeasure.Each,
            4.85m)).Errors);
}
