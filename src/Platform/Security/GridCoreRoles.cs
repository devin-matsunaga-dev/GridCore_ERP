namespace GridCore.Platform.Security;

/// <summary>
/// The eight roles the utility runs on. These names are the source of truth: the Keycloak realm
/// declares realm roles with exactly these names, and <see cref="RolePermissionMap"/> maps them to
/// permissions. Endpoints are gated on permissions, never on these strings directly.
/// </summary>
public static class GridCoreRoles
{
    /// <summary>Full access, including user and permission administration.</summary>
    public const string Administrator = "Administrator";

    /// <summary>Front office: customers, service accounts, taking payments.</summary>
    public const string CustomerService = "CustomerService";

    /// <summary>Meter-to-cash: readings, rates, bill generation and adjustments.</summary>
    public const string Billing = "Billing";

    /// <summary>General ledger, AR/AP, journal posting.</summary>
    public const string Finance = "Finance";

    /// <summary>Stock, warehouses, goods receipt.</summary>
    public const string Warehouse = "Warehouse";

    /// <summary>Field crew: executes work orders, updates assets and meters.</summary>
    public const string Technician = "Technician";

    /// <summary>Schedules and assigns work, approves field work.</summary>
    public const string Supervisor = "Supervisor";

    /// <summary>Oversight: reads everything, approves adjustments and purchases.</summary>
    public const string Manager = "Manager";

    /// <summary>Every role, in the order they are presented in admin surfaces.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Administrator,
        CustomerService,
        Billing,
        Finance,
        Warehouse,
        Technician,
        Supervisor,
        Manager,
    ];

    private static readonly Dictionary<string, int> Order = All
        .Select((role, index) => (role, index))
        .ToDictionary(entry => entry.role, entry => entry.index, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="role"/> is one GridCore knows. Tokens may carry others.</summary>
    public static bool IsKnown(string role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return Order.ContainsKey(role);
    }

    /// <summary>Position of <paramref name="role"/> in <see cref="All"/>; unknown roles sort last.</summary>
    public static int OrderOf(string role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return Order.TryGetValue(role, out var index) ? index : int.MaxValue;
    }
}
