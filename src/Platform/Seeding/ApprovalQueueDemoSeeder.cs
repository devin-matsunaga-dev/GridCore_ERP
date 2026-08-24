using GridCore.Platform.Approvals;
using GridCore.Platform.Data;
using GridCore.Platform.Security;

namespace GridCore.Platform.Seeding;

/// <summary>
/// Puts a couple of pending decisions in the approval queue so a demo has something to approve.
/// </summary>
/// <remarks>
/// <para>
/// The approval primitive (WP-0.4) is reusable and module-agnostic, which is exactly why it can be
/// demonstrated before the modules that will raise real requests exist: a purchase order and a bill
/// adjustment differ only in their request type, subject and required permission. Later WPs raise
/// these for real; this seeder is what makes the queue non-empty in the meantime.
/// </para>
/// <para>
/// Both are raised by <see cref="DemoActor"/>s rather than by the system, so a signed-in Manager can
/// actually decide them — the aggregate refuses a decision by whoever raised the request, and a
/// queue full of requests the only signed-in user may not touch would demonstrate nothing.
/// </para>
/// </remarks>
public sealed class ApprovalQueueDemoSeeder(PlatformDbContext database, TimeProvider clock) : IDemoSeeder
{
    /// <summary>Subject type of the demo purchase order awaiting approval.</summary>
    public const string PurchaseOrderSubjectType = "demo.purchase_order";

    /// <summary>Subject type of the demo bill awaiting an adjustment decision.</summary>
    public const string BillSubjectType = "demo.bill";

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second copy of this queue.</remarks>
    public string Name => "platform.approval-queue";

    /// <inheritdoc />
    /// <remarks>First: it depends on nothing a module seeds.</remarks>
    public int Order => 100;

    /// <inheritdoc />
    public Task SeedAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var warehouse = new DemoActor("warehouse", "Wes Store (demo)");
        var billing = new DemoActor("billing", "Ben Biller (demo)");

        database.ApprovalRequests.AddRange(
            ApprovalRequest.Raise(
                "purchasing.purchase_order",
                PurchaseOrderSubjectType,
                "PO-1042",
                Permissions.Purchasing.Approve,
                new { PurchaseOrder = "PO-1042", Vendor = "Northgate Electrical Supply", Total = 14_820.00m, Currency = "USD" },
                "Replacement pole-top transformers for the Q4 inspection programme.",
                warehouse,
                now),
            ApprovalRequest.Raise(
                "billing.adjustment",
                BillSubjectType,
                "BILL-2026-08-0117",
                Permissions.Billing.Adjust,
                new { Bill = "BILL-2026-08-0117", Credit = 148.35m, Currency = "USD", Reason = "Estimated read corrected" },
                "Customer disputed an estimated read; actual read is lower.",
                billing,
                now));

        // No SaveChanges: the runner's unit of work saves this and the seed record in one
        // transaction, which is what makes a half-seeded demo world impossible.
        return Task.CompletedTask;
    }
}
