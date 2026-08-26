using System.Reflection;

namespace GridCore.Platform.Security;

/// <summary>
/// Every permission an endpoint can be gated on, grouped by the module that owns it.
/// Endpoints require permissions; <see cref="RolePermissionMap"/> is the only place roles are
/// turned into permissions, so re-cutting a role never touches endpoint code.
/// Naming: <c>&lt;module&gt;.&lt;action&gt;</c>, lower case, dot separated.
/// </summary>
public static class Permissions
{
    /// <summary>Customers, service locations and service accounts.</summary>
    public static class Customers
    {
        /// <summary>View customers, locations and service accounts.</summary>
        public const string Read = "customers.read";

        /// <summary>Create and edit customers, locations and service accounts.</summary>
        public const string Write = "customers.write";

        /// <summary>
        /// Assess and collect a security deposit (WP-2.8). Deliberately narrower than
        /// <see cref="Write"/>: opening an account and taking money for it are two different jobs,
        /// and a clerk who may register a customer is not automatically a clerk who may take a
        /// deposit off them. WP-2.12's lifecycle — hold, apply, refund — gates on this too.
        /// </summary>
        public const string Deposit = "customers.deposit";

        /// <summary>
        /// Mark a contact authorised to discuss a customer's account, or withdraw that (WP-2.11).
        /// Narrower than <see cref="Write"/> on purpose: maintaining a contact's name and numbers is
        /// clerical work, and deciding that the utility will disclose a customer's balance to a
        /// third party is not. The gate is in <c>CustomerContactService</c> rather than on the
        /// route, because whether a request moves the flag depends on what is in the body.
        /// </summary>
        public const string Authorise = "customers.authorise";

        /// <summary>
        /// Produce a document for a customer — reprint a bill, run an account statement, export a
        /// payment history (WP-2.14).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Named for the capability, not for the schema.</b> The bill reprint lives in Billing,
        /// because Billing owns the figures a bill was issued with and nobody else may read them;
        /// but from the desk it is the same act as the statement beside it — a document leaves the
        /// building with a customer's affairs on it — so it is gated on the same permission. A
        /// <c>billing.reprint</c> that had to be granted alongside this one would be two grants for
        /// one job, and the first utility to cut a role would get them out of step.
        /// </para>
        /// <para>
        /// <b>Narrower than <see cref="Read"/>, deliberately.</b> Reading a balance on screen and
        /// handing somebody a statement of it are different acts with different consequences: the
        /// second is a record that outlives the call, travels, and is produced under the utility's
        /// name. That is the line WP-2.13 put notes on the other side of — logging a call is
        /// clerical work and earned no permission — and the reason these three did earn one.
        /// </para>
        /// </remarks>
        public const string Documents = "customers.documents";
    }

    /// <summary>Meters, readings and consumption.</summary>
    public static class Metering
    {
        /// <summary>View meters, readings and consumption.</summary>
        public const string Read = "metering.read";

        /// <summary>Register meters, record readings, resolve reading exceptions.</summary>
        public const string Write = "metering.write";
    }

    /// <summary>Rate plans, bills and adjustments.</summary>
    public static class Billing
    {
        /// <summary>View rate plans, bills and adjustments.</summary>
        public const string Read = "billing.read";

        /// <summary>Run a billing cycle and issue bills.</summary>
        public const string Generate = "billing.generate";

        /// <summary>Adjust an issued bill. Sensitive: permission-gated and audited.</summary>
        public const string Adjust = "billing.adjust";
    }

    /// <summary>Customer payments.</summary>
    public static class Payments
    {
        /// <summary>View payments and their provider outcomes.</summary>
        public const string Read = "payments.read";

        /// <summary>Take a payment against an account.</summary>
        public const string Record = "payments.record";

        /// <summary>Refund a settled payment. Sensitive: permission-gated and audited.</summary>
        public const string Refund = "payments.refund";
    }

    /// <summary>General ledger, AR/AP and trial balance.</summary>
    public static class Finance
    {
        /// <summary>View journals, AR/AP and the trial balance.</summary>
        public const string Read = "finance.read";

        /// <summary>Post a manual journal entry. Sensitive: permission-gated and audited.</summary>
        public const string Post = "finance.post";
    }

    /// <summary>Utility asset registry and maintenance history.</summary>
    public static class Assets
    {
        /// <summary>View assets and their maintenance history.</summary>
        public const string Read = "assets.read";

        /// <summary>Register assets and update condition or status.</summary>
        public const string Write = "assets.write";
    }

    /// <summary>Work orders and crew assignment.</summary>
    public static class WorkOrders
    {
        /// <summary>View work orders and crews.</summary>
        public const string Read = "workorders.read";

        /// <summary>Raise a work order.</summary>
        public const string Create = "workorders.create";

        /// <summary>Assign a work order to a crew or technician.</summary>
        public const string Assign = "workorders.assign";

        /// <summary>Complete a work order, booking parts and labour.</summary>
        public const string Complete = "workorders.complete";
    }

    /// <summary>Stock, warehouses and purchasing.</summary>
    public static class Inventory
    {
        /// <summary>View stock levels, warehouses and movements.</summary>
        public const string Read = "inventory.read";

        /// <summary>Issue and receive stock.</summary>
        public const string Write = "inventory.write";

        /// <summary>Correct stock on hand outside normal movements. Sensitive: permission-gated and audited.</summary>
        public const string Adjust = "inventory.adjust";
    }

    /// <summary>Vendors, requisitions and purchase orders.</summary>
    public static class Purchasing
    {
        /// <summary>View vendors, requisitions and purchase orders.</summary>
        public const string Read = "purchasing.read";

        /// <summary>Raise a requisition or purchase order.</summary>
        public const string Create = "purchasing.create";

        /// <summary>Approve a purchase order. Sensitive: permission-gated and audited.</summary>
        public const string Approve = "purchasing.approve";
    }

    /// <summary>Cross-cutting platform surfaces.</summary>
    public static class Platform
    {
        /// <summary>Decide a pending approval request (WP-0.4).</summary>
        public const string Approve = "platform.approve";

        /// <summary>Read the audit trail.</summary>
        public const string AuditRead = "platform.audit.read";

        /// <summary>Administer users, roles and system settings.</summary>
        public const string Admin = "platform.admin";
    }

    /// <summary>
    /// Every declared permission. Discovered from the constants above so a new permission cannot be
    /// forgotten in <see cref="RolePermissionMap"/>'s Administrator grant.
    /// </summary>
    public static IReadOnlySet<string> All { get; } = Discover();

    private static IReadOnlySet<string> Discover()
    {
        var values = typeof(Permissions)
            .GetNestedTypes(BindingFlags.Public)
            .SelectMany(group => group.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

        return values.ToHashSet(StringComparer.Ordinal);
    }
}
