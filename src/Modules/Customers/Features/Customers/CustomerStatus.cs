namespace GridCore.Modules.Customers.Features.Customers;

/// <summary>
/// Where a customer stands with the utility. This is the <i>customer record's</i> status, not a
/// service account's: whether a particular premise is connected is WP-1.2's state machine, and a
/// customer may be active while one of their accounts is disconnected.
/// </summary>
public enum CustomerStatus
{
    /// <summary>Registered but not yet taking service. Where a new registration starts.</summary>
    Prospect = 1,

    /// <summary>Taking service, or entitled to.</summary>
    Active = 2,

    /// <summary>Barred from new service — unpaid balance, disputed identity — but still on the books.</summary>
    Suspended = 3,

    /// <summary>Left the utility. Terminal: their history stays readable, but nothing new attaches to them.</summary>
    Closed = 4,
}

/// <summary>
/// The customer state machine, in one place. Kept out of <see cref="Customer"/> itself so a UI can
/// ask what is legal without holding an entity — DESIGN.md renders allowed transitions as buttons
/// and disables the rest.
/// </summary>
public static class CustomerTransitions
{
    private static readonly Dictionary<CustomerStatus, CustomerStatus[]> Allowed = new()
    {
        [CustomerStatus.Prospect] = [CustomerStatus.Active, CustomerStatus.Closed],
        [CustomerStatus.Active] = [CustomerStatus.Suspended, CustomerStatus.Closed],
        [CustomerStatus.Suspended] = [CustomerStatus.Active, CustomerStatus.Closed],

        // Terminal. Reopening is a new registration, so that the closed record keeps meaning what
        // it said — the same reason a ledger correction is a new entry rather than an edit.
        [CustomerStatus.Closed] = [],
    };

    /// <summary>The statuses a customer in <paramref name="status"/> may move to.</summary>
    public static IReadOnlyList<CustomerStatus> AllowedFrom(CustomerStatus status) =>
        Allowed.TryGetValue(status, out var next) ? next : [];

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> is a legal move.</summary>
    public static bool IsAllowed(CustomerStatus from, CustomerStatus to) =>
        AllowedFrom(from).Contains(to);
}
