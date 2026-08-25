namespace GridCore.Platform.Security;

/// <summary>
/// The single place roles become permissions. Endpoints are gated on permissions, so re-cutting a
/// role is a change here and nowhere else. Pure and static — no configuration, no database — which
/// keeps authorization decisions cheap and unit-testable.
/// </summary>
public static class RolePermissionMap
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Map =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            // Administrator is granted every declared permission by construction, so a new
            // permission can never be invisible to the one role that must always see it.
            [GridCoreRoles.Administrator] = Permissions.All,

            [GridCoreRoles.CustomerService] = Set(
                Permissions.Customers.Read,
                Permissions.Customers.Write,

                // The front office is where a deposit is taken — at intake, over the counter. It is
                // a separate grant from customers.write on purpose (WP-2.8): the two travel together
                // for this role and for no other, which is the only way the distinction can be seen.
                Permissions.Customers.Deposit,

                Permissions.Metering.Read,
                Permissions.Billing.Read,
                Permissions.Payments.Read,
                Permissions.Payments.Record,
                Permissions.Assets.Read,
                Permissions.WorkOrders.Read,
                Permissions.WorkOrders.Create),

            [GridCoreRoles.Billing] = Set(
                Permissions.Customers.Read,
                Permissions.Metering.Read,
                Permissions.Metering.Write,
                Permissions.Billing.Read,
                Permissions.Billing.Generate,
                Permissions.Billing.Adjust,
                Permissions.Payments.Read,
                Permissions.Finance.Read),

            [GridCoreRoles.Finance] = Set(
                Permissions.Customers.Read,

                // Finance holds it without customers.write: a deposit is money, and the refunds and
                // applications WP-2.12 builds are Finance's work, not an edit to a customer record.
                Permissions.Customers.Deposit,

                Permissions.Billing.Read,
                Permissions.Payments.Read,
                Permissions.Payments.Refund,
                Permissions.Finance.Read,
                Permissions.Finance.Post,
                Permissions.Purchasing.Read,
                Permissions.Platform.AuditRead),

            [GridCoreRoles.Warehouse] = Set(
                Permissions.Assets.Read,
                Permissions.Inventory.Read,
                Permissions.Inventory.Write,
                Permissions.Inventory.Adjust,
                Permissions.Purchasing.Read,
                Permissions.Purchasing.Create,
                Permissions.WorkOrders.Read),

            [GridCoreRoles.Technician] = Set(
                Permissions.Assets.Read,
                Permissions.Assets.Write,
                Permissions.Metering.Read,
                Permissions.Metering.Write,
                Permissions.Inventory.Read,
                Permissions.WorkOrders.Read,
                Permissions.WorkOrders.Complete),

            [GridCoreRoles.Supervisor] = Set(
                Permissions.Customers.Read,
                Permissions.Assets.Read,
                Permissions.Assets.Write,
                Permissions.Metering.Read,
                Permissions.Inventory.Read,
                Permissions.WorkOrders.Read,
                Permissions.WorkOrders.Create,
                Permissions.WorkOrders.Assign,
                Permissions.WorkOrders.Complete,
                Permissions.Platform.Approve),

            [GridCoreRoles.Manager] = Set(
                Permissions.Customers.Read,
                Permissions.Metering.Read,
                Permissions.Billing.Read,
                Permissions.Billing.Adjust,
                Permissions.Payments.Read,
                Permissions.Finance.Read,
                Permissions.Assets.Read,
                Permissions.Inventory.Read,
                Permissions.WorkOrders.Read,
                Permissions.WorkOrders.Assign,
                Permissions.Purchasing.Read,
                Permissions.Purchasing.Approve,
                Permissions.Platform.Approve,
                Permissions.Platform.AuditRead),
        };

    /// <summary>The permissions granted by a single role; empty for a role GridCore does not know.</summary>
    public static IReadOnlySet<string> PermissionsForRole(string role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return Map.TryGetValue(role, out var permissions) ? permissions : EmptySet;
    }

    /// <summary>
    /// The union of the permissions granted by <paramref name="roles"/>. Roles GridCore does not
    /// know are ignored rather than rejected: the identity provider may carry roles for other
    /// systems, and an unrecognised role must never widen access.
    /// </summary>
    public static IReadOnlySet<string> PermissionsForRoles(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var granted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in roles)
        {
            granted.UnionWith(PermissionsForRole(role));
        }

        return granted;
    }

    /// <summary>Whether any of <paramref name="roles"/> grants <paramref name="permission"/>.</summary>
    public static bool HasPermission(IEnumerable<string> roles, string permission)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        return roles.Any(role => PermissionsForRole(role).Contains(permission));
    }

    private static IReadOnlySet<string> EmptySet { get; } = new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string> Set(params string[] permissions) =>
        permissions.ToHashSet(StringComparer.Ordinal);
}
