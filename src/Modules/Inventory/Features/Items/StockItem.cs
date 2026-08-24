using GridCore.Modules.Inventory.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>
/// A line in the store's catalogue, and the stock held against it. The thing a crew draws parts
/// from, a purchase order buys more of, and a low-stock report is a query over.
/// </summary>
/// <remarks>
/// <para>
/// An item is a <b>type</b>, not a device: one catalogue line, a quantity in each warehouse that
/// carries it, and a ledger of every movement. The individual transformer that came off the shelf
/// and went up a pole stops being a quantity and becomes an <c>Asset</c> with a tag and a
/// maintenance history, in another module — which is why nothing here points at one.
/// </para>
/// <para>
/// The aggregate owns the arithmetic. Nothing outside it can move a quantity, so a quantity cannot
/// move without the <see cref="StockMovement"/> line that explains it; that is the same guarantee
/// <c>Asset</c> gives its history, and it is what makes a count answerable rather than merely
/// current.
/// </para>
/// </remarks>
public sealed class StockItem
{
    /// <summary>Longest item name stored.</summary>
    public const int NameLength = 256;

    /// <summary>Longest description stored.</summary>
    public const int DescriptionLength = 512;

    /// <summary>Longest manufacturer's part number stored.</summary>
    public const int PartNumberLength = 128;

    /// <summary>Longest stored form of a category or unit name.</summary>
    public const int EnumNameLength = 32;

    /// <summary>Longest reason recorded against a movement or a discontinuation.</summary>
    public const int ReasonLength = StockMovement.NoteLength;

    private readonly List<StockLevel> _levels = [];
    private readonly List<StockMovement> _movements = [];

    private StockItem()
    {
        // EF materialisation.
        ItemCode = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Identifier of this item. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The catalogue code on the bin label, e.g. <c>ITM-000001</c>. Unique, and fixed at registration.</summary>
    public string ItemCode { get; private init; }

    /// <summary>What it is — "ACSR Raven 1/0 conductor".</summary>
    public string Name { get; private set; }

    /// <summary>What kind of thing it is.</summary>
    public StockItemCategory Category { get; private set; }

    /// <summary>What one of it is.</summary>
    public UnitOfMeasure Unit { get; private set; }

    /// <summary>Anything more a storeman needs to know before issuing it.</summary>
    public string? Description { get; private set; }

    /// <summary>
    /// The manufacturer's part number, where the item has one. Unique across the catalogue when
    /// present: holding one part twice under two codes is how half the stock goes missing.
    /// </summary>
    public string? ManufacturerPartNumber { get; private set; }

    /// <summary>
    /// What one unit is reckoned to cost — the standard cost a valuation and a requisition use.
    /// Money is <see langword="decimal"/> (invariant 4).
    /// </summary>
    /// <remarks>
    /// Not recalculated by a receipt. A receipt records what <i>that</i> delivery cost on its own
    /// ledger line; moving the standard cost with every delivery is a weighted-average valuation,
    /// which needs the central rounding helper CONVENTIONS.md still has no home for (WP-2.3 owns
    /// it) and is Finance's decision to make rather than the store's.
    /// </remarks>
    public decimal UnitCost { get; private set; }

    /// <summary>
    /// Whether the store still carries this line. A discontinued item is deactivated, never deleted:
    /// its movements are the history a job costing and a valuation read.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>Why it was last discontinued or brought back.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>When the item was entered in the catalogue.</summary>
    public DateTimeOffset RegisteredAt { get; private init; }

    /// <summary>Where this item is held and how much of it, one row per warehouse that carries it.</summary>
    public IReadOnlyList<StockLevel> Levels => _levels;

    /// <summary>
    /// The movements loaded on this instance, oldest first. Empty on a write path by design — see
    /// <see cref="Receive"/>.
    /// </summary>
    public IReadOnlyList<StockMovement> Movements => _movements;

    /// <summary>How much is held across every warehouse.</summary>
    public decimal TotalOnHand => _levels.Sum(level => level.QuantityOnHand);

    /// <summary>Whether any warehouse is at or below its reorder level — the low-stock flag a list row shows.</summary>
    public bool IsBelowMinimum => _levels.Any(level => level.IsBelowMinimum);

    /// <summary>
    /// Enters an item in the catalogue under a code the caller has already reserved — see
    /// <see cref="IStockItemNumberGenerator"/>.
    /// </summary>
    /// <remarks>
    /// No movement and no ledger line: registering a catalogue line is not stock arriving. An item
    /// registered today and never delivered is held nowhere, which is why it has no levels either.
    /// It is also why this takes no actor, where <c>Asset.Register</c> does — there is no service
    /// record to open, only an audit entry, and the platform stamps that.
    /// </remarks>
    /// <exception cref="InventoryValidationException">A required field is missing, an enum is undeclared, or the cost is not money.</exception>
    public static StockItem Register(
        string itemCode,
        StockItemCategory category,
        string name,
        UnitOfMeasure unit,
        DateTimeOffset now,
        string? description = null,
        string? manufacturerPartNumber = null,
        decimal unitCost = 0m,
        bool isActive = true)
    {
        Require(itemCode, nameof(itemCode));
        Require(name, nameof(name));
        RequireDeclared(category);
        RequireDeclared(unit);

        return new StockItem
        {
            Id = Guid.CreateVersion7(now),
            ItemCode = RegistryText.Clean(itemCode, RegistryNumbers.MaxLength)!,
            Name = RegistryText.Clean(name, NameLength)!,
            Category = category,
            Unit = unit,
            Description = RegistryText.Clean(description, DescriptionLength),
            ManufacturerPartNumber = RegistryText.Clean(manufacturerPartNumber, PartNumberLength),
            UnitCost = StockCosts.Require(unitCost, nameof(unitCost)),
            IsActive = isActive,
            RegisteredAt = now,
        };
    }

    /// <summary>
    /// Corrects the catalogue line. The code is not among them — it is on the bin label and quoted
    /// by every requisition — and neither are the quantities, which move only through
    /// <see cref="Receive"/>, <see cref="Issue"/> and <see cref="Adjust"/>.
    /// </summary>
    /// <exception cref="InventoryValidationException">A required field is missing, an enum is undeclared, or the cost is not money.</exception>
    /// <exception cref="InventoryWorkflowException">The unit of measure is being changed after stock has moved.</exception>
    public void UpdateDetails(
        StockItemCategory category,
        string name,
        UnitOfMeasure unit,
        decimal unitCost,
        bool isActive,
        string? description = null,
        string? manufacturerPartNumber = null,
        string? statusReason = null)
    {
        Require(name, nameof(name));
        RequireDeclared(category);
        RequireDeclared(unit);

        // Every guard runs before the first assignment, so a rejected correction leaves the item
        // exactly as it was rather than half-applied.
        var cost = StockCosts.Require(unitCost, nameof(unitCost));

        if (unit != Unit && _levels.Count > 0)
        {
            // A quantity means nothing without its unit. Re-denominating 240 metres as 240 each
            // would not convert the ledger behind it — it would silently reinterpret every line,
            // and the store would be short by whatever the two units differ by. A genuine change of
            // unit is a new catalogue line the old one is discontinued in favour of.
            throw new InventoryWorkflowException(
                $"Item {ItemCode} is held in {Unit} and stock has already moved; the unit of measure cannot be changed. Register a new item instead.");
        }

        // Only recorded when the flag actually moves, so a plain description fix cannot silently
        // overwrite why the line was discontinued (WP-1.1's rule for a premise, unchanged).
        if (isActive != IsActive)
        {
            StatusReason = RegistryText.Clean(statusReason, ReasonLength);
        }

        Category = category;
        Name = RegistryText.Clean(name, NameLength)!;
        Unit = unit;
        UnitCost = cost;
        Description = RegistryText.Clean(description, DescriptionLength);
        ManufacturerPartNumber = RegistryText.Clean(manufacturerPartNumber, PartNumberLength);
        IsActive = isActive;
    }

    /// <summary>
    /// Books stock in at a warehouse, appending the ledger line that says where it came from.
    /// </summary>
    /// <remarks>
    /// The movement is appended without the ledger being loaded, deliberately: a delivery of ten
    /// connectors must not have to read five years of movements to add a line to the end. That is
    /// why <see cref="Movements"/> is empty on a write path — the levels are what a write needs, and
    /// the ledger is read on the item being looked at.
    /// </remarks>
    /// <exception cref="InventoryValidationException">The quantity or the cost is not one the store can hold.</exception>
    /// <exception cref="InventoryWorkflowException">The item has been discontinued.</exception>
    public StockMovement Receive(
        Guid warehouseId,
        decimal quantity,
        RegistryActor actor,
        DateTimeOffset now,
        decimal? unitCost = null,
        string? reference = null,
        string? note = null)
    {
        StockMovement.RequireActor(actor);

        var received = StockQuantities.RequireMovement(quantity, Unit, nameof(quantity));
        var cost = unitCost is { } value ? StockCosts.Require(value, nameof(unitCost)) : (decimal?)null;

        if (!IsActive)
        {
            // Buying more of a line the store has stopped carrying is a purchase order raised
            // against the wrong item, and a 409 naming it is what sends the storeman back to the
            // catalogue rather than leaving stock nobody will ever issue.
            throw new InventoryWorkflowException($"Item {ItemCode} has been discontinued; stock cannot be received against it.");
        }

        var level = LevelFor(warehouseId, now);
        var onHand = level.Apply(received, now);

        var movement = StockMovement.Receipt(Id, warehouseId, received, onHand, cost, reference, note, actor, now);

        _movements.Add(movement);

        return movement;
    }

    /// <summary>
    /// Issues stock out of a warehouse to a job, appending the ledger line that says where it went.
    /// </summary>
    /// <remarks>
    /// Allowed on a discontinued item, unlike <see cref="Receive"/>: a line the store has stopped
    /// carrying still has to be cleared off the shelf, and the crew using up the last of it is
    /// exactly how that happens.
    /// </remarks>
    /// <exception cref="InventoryValidationException">The quantity is not one the store can hold.</exception>
    /// <exception cref="InventoryWorkflowException">There is not that much on hand.</exception>
    public StockMovement Issue(
        Guid warehouseId,
        decimal quantity,
        RegistryActor actor,
        DateTimeOffset now,
        Guid? workOrderId = null,
        string? reference = null,
        string? note = null)
    {
        StockMovement.RequireActor(actor);

        var issued = StockQuantities.RequireMovement(quantity, Unit, nameof(quantity));

        var level = _levels.SingleOrDefault(existing => existing.WarehouseId == warehouseId);

        if (level is null || level.QuantityOnHand < issued)
        {
            // Never negative stock. A store that can go below zero is a store whose count means
            // nothing, and the crew still standing there needs to be told to try another warehouse
            // rather than handed a number that says the shelf owes them parts.
            throw new InventoryWorkflowException(
                $"Cannot issue {issued} of item {ItemCode}: {level?.QuantityOnHand ?? 0m} on hand in that warehouse.");
        }

        var onHand = level.Apply(-issued, now);

        var movement = StockMovement.Issue(Id, warehouseId, issued, onHand, workOrderId, reference, note, actor, now);

        _movements.Add(movement);

        return movement;
    }

    /// <summary>
    /// Corrects the count in a warehouse to what was actually on the shelf, appending the ledger
    /// line that says who says so and why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller states the <b>counted quantity</b> and the difference is derived, rather than
    /// passing a signed delta. That is what a stock take produces — somebody counted forty-two — and
    /// making the store do the subtraction is what stops "we are eight short" from being entered as
    /// eight and going the wrong way.
    /// </para>
    /// <para>
    /// This is the sensitive movement of the three: it changes stock without anything physically
    /// moving, so it is gated on <c>inventory.adjust</c> rather than <c>inventory.write</c>, and the
    /// reason is required (invariant 5).
    /// </para>
    /// </remarks>
    /// <exception cref="InventoryValidationException">The count is not one the store can hold, or no reason was given.</exception>
    /// <exception cref="InventoryWorkflowException">The count already matches.</exception>
    public StockMovement Adjust(
        Guid warehouseId,
        decimal countedQuantity,
        string reason,
        RegistryActor actor,
        DateTimeOffset now)
    {
        StockMovement.RequireActor(actor);
        StockMovement.RequireReason(reason);

        // Every guard runs before the first mutation. Without this line the shelf would already have
        // been moved by the time an unexplained adjustment was refused — harmless to the database,
        // which rolls the transaction back, but the aggregate a caller is still holding would be
        // telling them a count that never happened.
        var counted = StockQuantities.RequireLevel(countedQuantity, Unit, nameof(countedQuantity));

        var level = LevelFor(warehouseId, now);
        var change = counted - level.QuantityOnHand;

        if (change == 0m)
        {
            // A stock take that agrees with the system is a real and welcome finding, but it is not
            // a movement — and a zero line in a ledger read to explain a quantity is noise. This is
            // the deliberate opposite of Asset.AssessCondition, which does record "inspected, still
            // Fair": there, the assessment is itself the record; here, the ledger is for movements.
            throw new InventoryWorkflowException(
                $"Item {ItemCode} already shows {counted} in that warehouse; there is nothing to correct.");
        }

        var onHand = level.Apply(change, now);

        var movement = StockMovement.Adjustment(Id, warehouseId, change, onHand, reason, actor, now);

        _movements.Add(movement);

        return movement;
    }

    /// <summary>
    /// Sets how low this item may fall in a warehouse before somebody should reorder.
    /// </summary>
    /// <remarks>
    /// Not a movement, so no ledger line: nothing came in and nothing went out. It still produces an
    /// audit entry, because moving a reorder level is how a low-stock report is quietly silenced.
    /// A level is opened if the item is not stocked there yet — setting the reorder point before the
    /// first delivery is how a store prepares for one.
    /// </remarks>
    /// <exception cref="InventoryValidationException">The quantity is not one the store can hold.</exception>
    public void SetMinimumQuantity(Guid warehouseId, decimal minimumQuantity, DateTimeOffset now) =>
        LevelFor(warehouseId, now)
            .SetMinimum(StockQuantities.RequireLevel(minimumQuantity, Unit, nameof(minimumQuantity)));

    /// <summary>How much of this item is held in one warehouse, and how low it may fall.</summary>
    public StockLevel? LevelIn(Guid warehouseId) =>
        _levels.SingleOrDefault(level => level.WarehouseId == warehouseId);

    /// <summary>How much is on hand in one warehouse — nothing, where the item is not stocked there.</summary>
    public decimal OnHandIn(Guid warehouseId) => LevelIn(warehouseId)?.QuantityOnHand ?? 0m;

    private StockLevel LevelFor(Guid warehouseId, DateTimeOffset now)
    {
        if (LevelIn(warehouseId) is { } existing)
        {
            return existing;
        }

        var level = StockLevel.For(Id, warehouseId, now);

        _levels.Add(level);

        return level;
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InventoryValidationException($"'{field}' is required to register a stock item.");
        }
    }

    private static void RequireDeclared<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        // A value cast from an unmapped integer would be stored by name as a number and read back
        // as nothing anyone can act on.
        if (!Enum.IsDefined(value))
        {
            throw new InventoryValidationException($"'{value}' is not a {typeof(TEnum).Name} GridCore declares.");
        }
    }
}
