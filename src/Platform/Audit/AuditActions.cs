namespace GridCore.Platform.Audit;

/// <summary>
/// Canonical audit action names. Modules add their own constants here as they land, so the trail
/// can be filtered on a known vocabulary rather than free text. Naming:
/// <c>&lt;entity&gt;.&lt;verb&gt;</c>, lower case, dot separated.
/// </summary>
public static class AuditActions
{
    /// <summary>An approval request was raised.</summary>
    public const string ApprovalRequested = "approval.request";

    /// <summary>An approval request was approved.</summary>
    public const string ApprovalApproved = "approval.approve";

    /// <summary>An approval request was rejected.</summary>
    public const string ApprovalRejected = "approval.reject";

    /// <summary>An approval request was withdrawn by the person who raised it.</summary>
    public const string ApprovalCancelled = "approval.cancel";

    /// <summary>A demo seeder wrote its dataset (Development only).</summary>
    public const string DemoSeeded = "demo.seed";

    /// <summary>A customer was registered.</summary>
    public const string CustomerCreated = "customer.create";

    /// <summary>A customer's details were changed.</summary>
    public const string CustomerUpdated = "customer.update";

    /// <summary>A customer moved to another status.</summary>
    public const string CustomerStatusChanged = "customer.status";

    /// <summary>A service location was registered.</summary>
    public const string ServiceLocationCreated = "service_location.create";

    /// <summary>A service location's details were changed.</summary>
    public const string ServiceLocationUpdated = "service_location.update";

    /// <summary>A service account was opened, joining a customer to a premise.</summary>
    public const string ServiceAccountOpened = "service_account.open";

    /// <summary>Service was energised on an account.</summary>
    public const string ServiceAccountStarted = "service_account.start";

    /// <summary>Service was cut on an account, leaving it open.</summary>
    public const string ServiceAccountStopped = "service_account.stop";

    /// <summary>A service account was closed for good.</summary>
    public const string ServiceAccountClosed = "service_account.close";

    /// <summary>A meter was entered in the meter register.</summary>
    public const string MeterRegistered = "meter.create";

    /// <summary>A meter's device details were corrected.</summary>
    public const string MeterUpdated = "meter.update";

    /// <summary>A meter was fitted at a service location.</summary>
    public const string MeterInstalled = "meter.install";

    /// <summary>A meter was taken off a service location.</summary>
    public const string MeterRemoved = "meter.remove";

    /// <summary>A meter moved through its lifecycle without changing where it is.</summary>
    public const string MeterStatusChanged = "meter.status";

    /// <summary>A reading was taken off a meter and recorded in the reading register.</summary>
    public const string MeterReadingRecorded = "meter_reading.create";

    /// <summary>A reading cycle was run against the reading provider, producing a batch of readings.</summary>
    public const string MeterReadingCycleRun = "meter_reading.cycle";

    /// <summary>A service account was put on a rate plan, or moved to another one.</summary>
    public const string AccountRatePlanAssigned = "account_rate_plan.assign";

    /// <summary>A billing run priced a reading cycle, producing draft bills.</summary>
    public const string BillingRunExecuted = "billing_run.execute";

    /// <summary>A bill was issued to the customer, and Finance posted the receivable.</summary>
    public const string BillIssued = "bill.issue";

    /// <summary>A bill was withdrawn. Sensitive: it removes money the utility was owed.</summary>
    public const string BillCancelled = "bill.cancel";

    /// <summary>
    /// An issued bill was corrected by a credit or a charge. Sensitive: it changes what a customer
    /// owes after they have been told what they owe.
    /// </summary>
    public const string BillAdjusted = "bill.adjust";

    /// <summary>Outstanding bills past their due date were reviewed and marked overdue.</summary>
    public const string BillOverdueReviewed = "bill.overdue_review";

    /// <summary>
    /// An approved payment was applied to a bill, reducing what is owed. Recorded against
    /// <c>system</c>: this happens in a consumer rather than at somebody's keyboard, and the clerk
    /// who took the money is named on the payment's own entry.
    /// </summary>
    public const string BillPaymentRecorded = "bill.payment";

    /// <summary>
    /// A payment was taken from a customer and put to the payment provider. Recorded whatever the
    /// provider answered — a run of declines on one account is exactly what somebody comes looking
    /// for, and a trail holding only the successes could not answer them.
    /// </summary>
    public const string PaymentTaken = "payment.take";

    /// <summary>A utility asset was entered in the register.</summary>
    public const string AssetRegistered = "asset.create";

    /// <summary>An asset's details were corrected.</summary>
    public const string AssetUpdated = "asset.update";

    /// <summary>An asset moved through its lifecycle — installed, withdrawn, retired.</summary>
    public const string AssetStatusChanged = "asset.status";

    /// <summary>An asset's condition was assessed.</summary>
    public const string AssetConditionAssessed = "asset.condition";

    /// <summary>A stock item was entered in the catalogue.</summary>
    public const string StockItemRegistered = "stock_item.create";

    /// <summary>A stock item's catalogue details were corrected, or the line was discontinued.</summary>
    public const string StockItemUpdated = "stock_item.update";

    /// <summary>Stock was booked in at a warehouse.</summary>
    public const string StockReceived = "stock.receive";

    /// <summary>Stock was issued out to a job.</summary>
    public const string StockIssued = "stock.issue";

    /// <summary>A stock count was corrected. Sensitive: permission-gated and audited (invariant 5).</summary>
    public const string StockAdjusted = "stock.adjust";

    /// <summary>A reorder level was set — the change that quietly silences a low-stock report.</summary>
    public const string StockMinimumSet = "stock.minimum";
}

/// <summary>Canonical audit entity-type names, prefixed with the owning module's schema.</summary>
public static class AuditEntityTypes
{
    /// <summary>A row of <c>platform.approval_requests</c>.</summary>
    public const string ApprovalRequest = "platform.approval_request";

    /// <summary>A row of <c>platform.demo_seed_records</c>.</summary>
    public const string DemoSeedRecord = "platform.demo_seed_record";

    /// <summary>A row of <c>customers.customers</c>.</summary>
    public const string Customer = "customers.customer";

    /// <summary>A row of <c>customers.service_locations</c>.</summary>
    public const string ServiceLocation = "customers.service_location";

    /// <summary>A row of <c>customers.service_accounts</c>.</summary>
    public const string ServiceAccount = "customers.service_account";

    /// <summary>
    /// A row of <c>metering.meters</c>. History lines are audited against the meter they belong to
    /// rather than as entities of their own: the line is already append-only, and the question an
    /// auditor asks is "what happened to this meter".
    /// </summary>
    public const string Meter = "metering.meter";

    /// <summary>
    /// A row of <c>metering.meter_readings</c>. The reading is its own entity rather than a line of
    /// the meter's: a bill is raised from one, a dispute is about one, and an auditor asked "where
    /// did this figure come from" is asking about the reading, not about the device.
    /// </summary>
    public const string MeterReading = "metering.meter_reading";

    /// <summary>
    /// A reading cycle, identified by its cycle code. Not a table: a cycle run writes many readings
    /// in one act, and one entry naming the cycle is what an auditor actually reads back — the
    /// readings themselves are already immutable and each stamped with who recorded it.
    /// </summary>
    public const string MeterReadingCycle = "metering.reading_cycle";

    /// <summary>
    /// A row of <c>billing.account_rate_plans</c>, identified by the <b>service account</b> it is
    /// about. That is what somebody asks after — "what is this customer billed on, and who put them
    /// there" — and the assignment row's own id is an implementation detail of holding the answer.
    /// </summary>
    public const string AccountRatePlan = "billing.account_rate_plan";

    /// <summary>
    /// A row of <c>billing.bills</c>. Lines and adjustments are audited against the bill they belong
    /// to rather than as entities of their own: a line is written once with the bill and never
    /// moves, and an adjustment is only meaningful as a change to what that bill is owed — the
    /// before/after an auditor reads is the bill's, not the entry's.
    /// </summary>
    public const string Bill = "billing.bill";

    /// <summary>
    /// A billing run, identified by the reading cycle it priced. Not a table: a run raises many
    /// bills in one act, and one entry naming the cycle is what an auditor reads back — each bill is
    /// already its own row stamped with who raised it.
    /// </summary>
    public const string BillingRun = "billing.billing_run";

    /// <summary>
    /// An overdue review, identified by the day it judged against. Not a table either, and one entry
    /// for the same reason a billing run gets one.
    /// </summary>
    public const string BillOverdueReview = "billing.overdue_review";

    /// <summary>
    /// A row of <c>payments.payments</c>. The attempt is its own entity rather than a line of the
    /// bill's: a receipt is issued for one, a chargeback is about one, and an auditor asking "where
    /// did this money come from" is asking about the payment, not about the document it settled.
    /// </summary>
    public const string Payment = "payments.payment";

    /// <summary>A row of <c>assets.assets</c>.</summary>
    public const string Asset = "assets.asset";

    /// <summary>
    /// A row of <c>inventory.stock_items</c>. Movements and levels are audited against the item they
    /// belong to rather than as entities of their own: the ledger line is already immutable, and the
    /// question an auditor asks is "what happened to this item", not "what happened to this row".
    /// </summary>
    public const string StockItem = "inventory.stock_item";
}
