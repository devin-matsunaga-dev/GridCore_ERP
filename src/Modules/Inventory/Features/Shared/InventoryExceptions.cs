namespace GridCore.Modules.Inventory.Features.Shared;

/// <summary>
/// Base of the failures the inventory endpoints translate into ProblemDetails responses. The service
/// throws these rather than returning result objects, so a rule can be enforced in the one place
/// that knows it and still reach the caller as the right status code.
/// </summary>
/// <remarks>
/// Copied from the Assets module's hierarchy rather than shared with it, deliberately: every type
/// here names an inventory entity ("Stock item not found"), and a shared 404 would have to be told
/// what it was looking for. The pattern travels between modules; the code does not.
/// </remarks>
public abstract class InventoryException(string message) : Exception(message);

/// <summary>No stock item with that id. Surfaces as 404.</summary>
public sealed class StockItemNotFoundException(Guid id)
    : InventoryException($"Stock item '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid StockItemId { get; } = id;
}

/// <summary>
/// No warehouse with that id. Surfaces as 404. Warehouses are reference data shipped by migration,
/// so this is a caller quoting an id that was never issued rather than one that has gone away.
/// </summary>
public sealed class WarehouseNotFoundException(Guid id)
    : InventoryException($"Warehouse '{id}' was not found. Warehouses are reference data; adding one is a migration.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid WarehouseId { get; } = id;
}

/// <summary>
/// The stock is not in a state that allows what was asked — issuing more than is on hand, receiving
/// into a closed warehouse, or an item code or part number already taken. Surfaces as 409.
/// </summary>
public sealed class InventoryWorkflowException(string message) : InventoryException(message);

/// <summary>
/// The movement or the item as described could not be accepted. Surfaces as 400. Edge validation
/// catches most of these first; this is the aggregate's own guard, which also protects a seeder or a
/// later module calling the service directly.
/// </summary>
public sealed class InventoryValidationException(string message) : InventoryException(message);
