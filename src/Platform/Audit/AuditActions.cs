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

    /// <summary>
    /// A customer moved to another status (WP-2.15 gave it a reason code and an effective date).
    /// </summary>
    /// <remarks>
    /// Still <c>customer.status</c> rather than a new action. What happened is what always happened
    /// — a customer's status moved — and re-cutting the verb would leave every entry written before
    /// WP-2.15 unfindable by the filter that finds the ones written after it. The reason code and the
    /// effective date arrive in the snapshot, which is where facts about the change belong.
    /// </remarks>
    public const string CustomerStatusChanged = "customer.status";

    /// <summary>
    /// A customer moved between classes — residential to commercial or back (WP-2.15). Sensitive: it
    /// picks a different tariff from the effective date forward, so it is permission-gated on
    /// <c>customers.transition</c> and audited (invariant 5).
    /// </summary>
    public const string CustomerClassChanged = "customer.class";

    /// <summary>
    /// A customer took service at a premise they were not being served at (WP-2.15) — the standalone
    /// move-in. The account's own opening is audited separately as
    /// <see cref="ServiceAccountOpened"/>; this is the entry that carries the reason code, the
    /// effective date and the before/after of the customer it was done for.
    /// </summary>
    public const string ServiceMovedIn = "service_account.move_in";

    /// <summary>
    /// Service ended at a premise and the account was closed (WP-2.15) — the standalone move-out,
    /// which is also what triggers the final bill the billing pass will raise.
    /// </summary>
    public const string ServiceMovedOut = "service_account.move_out";

    /// <summary>
    /// Service moved from one premise to another for the same customer, as one linked act (WP-2.15).
    /// Its own action rather than a move-out beside a move-in, because a transfer is the thing that
    /// carries the deposit — reading it as two entries would lose the fact that no money changed
    /// hands between them.
    /// </summary>
    public const string ServiceTransferred = "service_account.transfer";

    /// <summary>
    /// A security deposit was assessed and collected from a customer. Sensitive: money changing
    /// hands, so it is permission-gated on <c>customers.deposit</c> and audited (invariant 5).
    /// </summary>
    public const string CustomerDepositCollected = "customer.deposit";

    /// <summary>
    /// A held deposit was put against a bill the customer owes (WP-2.12). Its own action rather than
    /// a second <c>customer.deposit</c> entry, because "what has this customer's deposit been spent
    /// on" is a question asked by filtering rather than by reading every deposit entry to see which
    /// way the money went.
    /// </summary>
    public const string CustomerDepositApplied = "customer.deposit_applied";

    /// <summary>
    /// A deposit was given back to the customer (WP-2.12). The one deposit movement that takes money
    /// out of the building, which is why it is filterable on its own.
    /// </summary>
    public const string CustomerDepositRefunded = "customer.deposit_refunded";

    /// <summary>A contact was added to a customer.</summary>
    public const string CustomerContactCreated = "customer_contact.create";

    /// <summary>A contact's details or contact methods were changed.</summary>
    public const string CustomerContactUpdated = "customer_contact.update";

    /// <summary>A contact was removed from a customer.</summary>
    public const string CustomerContactRemoved = "customer_contact.remove";

    /// <summary>
    /// A contact was granted or refused the right to discuss the account. Sensitive: it decides who
    /// the utility will disclose a customer's affairs to, so it is permission-gated and gets an
    /// entry of its own beside the contact's — "who authorised this person" is a question asked of
    /// the trail by filtering an action, not by reading every update.
    /// </summary>
    public const string CustomerContactAuthorised = "customer_contact.authorise";

    /// <summary>A customer's mailing address or communication preferences were saved.</summary>
    public const string CustomerProfileUpdated = "customer_profile.update";

    /// <summary>A note or an interaction was logged against a customer (WP-2.13).</summary>
    public const string CustomerNoteLogged = "customer_note.create";

    /// <summary>
    /// A new note was written correcting an earlier one (WP-2.13). Its own action rather than a
    /// second <c>customer_note.create</c>, because the note log is append-only and "what has been
    /// corrected on this account" is therefore a question asked by filtering the trail rather than by
    /// reading a diff — there are no diffs, only newer rows. The entry is recorded against the
    /// correction with the note it supersedes as its <c>before</c>.
    /// </summary>
    public const string CustomerNoteCorrected = "customer_note.correct";

    /// <summary>
    /// A note was put at the top of a customer's log, or taken back down (WP-2.13). The one thing
    /// about a note that moves, so it is the one thing about a note with a before/after worth
    /// reading — and it is audited because every write endpoint is (invariant 1), not because
    /// pinning is sensitive.
    /// </summary>
    public const string CustomerNotePinned = "customer_note.pin";

    /// <summary>
    /// A copy of an issued bill was produced for a customer (WP-2.14).
    /// </summary>
    /// <remarks>
    /// <b>A read that is audited, which is the exception and not the rule.</b> Invariant 1 is about
    /// writes, and WP-2.9 turned a search log down for exactly that reason — a record of every screen
    /// somebody opened is surveillance rather than an audit trail. A reprint is different in kind: a
    /// document with a customer's consumption and address on it leaves the building, and "who sent
    /// this out, for whom, and when" is the question asked of it afterwards. Recorded against the
    /// bill, with no <c>before</c> and no <c>after</c> — nothing about the bill changed, which is the
    /// whole point of a reprint.
    /// </remarks>
    public const string BillReprinted = "bill.reprint";

    /// <summary>
    /// An account statement was produced over a date range (WP-2.14). Audited for the reason a
    /// reprint is: it leaves the building. Its own action rather than a second <c>bill.reprint</c>,
    /// because a statement is about an account over a period rather than about one document, and
    /// "what statements has this customer been sent" is a question asked by filtering.
    /// </summary>
    public const string CustomerStatementProduced = "customer_statement.produce";

    /// <summary>
    /// A customer's payment history was exported as a file (WP-2.14). Its own action again, and the
    /// one of the three that produces something a recipient can keep, forward and open in a
    /// spreadsheet — which is precisely why the trail names it separately.
    /// </summary>
    public const string CustomerPaymentHistoryExported = "customer_payment_history.export";

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
    /// A fee from the published schedule was raised against a service account (WP-2.16). Sensitive:
    /// it is money the customer will be asked for, so it is permission-gated and audited.
    /// </summary>
    /// <remarks>
    /// The snapshot carries the schedule row that priced it and the amount charged, which together
    /// are what lets somebody answer "why is this $135" years after the schedule has moved on.
    /// </remarks>
    public const string AccountChargeRaised = "account_charge.raise";

    /// <summary>
    /// A raised charge was withdrawn before it reached a bill (WP-2.16). Sensitive for the reason a
    /// bill cancellation is: it removes money the utility was going to be owed.
    /// </summary>
    public const string AccountChargeCancelled = "account_charge.cancel";

    /// <summary>
    /// A charge was put on a bill of its own so the customer could pay it at the counter (WP-2.16).
    /// Recorded against the charge — the bill it produced is named in the snapshot and carries its
    /// own <see cref="BillIssued"/> entry.
    /// </summary>
    public const string AccountChargeBilled = "account_charge.bill";

    /// <summary>
    /// An approved payment was applied to a bill, reducing what is owed. Recorded against
    /// <c>system</c>: this happens in a consumer rather than at somebody's keyboard, and the clerk
    /// who took the money is named on the payment's own entry.
    /// </summary>
    public const string BillPaymentRecorded = "bill.payment";

    /// <summary>
    /// A customer's security deposit was applied to a bill, reducing what is owed (WP-2.12).
    /// Recorded against <c>system</c> for the same reason a payment is: this happens in a consumer,
    /// and the rep who decided to spend the deposit is named on the deposit ledger's own entry.
    /// Distinct from <see cref="BillPaymentRecorded"/> because "was this bill settled with cash or
    /// out of the deposit" is exactly what somebody asks of a closed account.
    /// </summary>
    public const string BillDepositApplied = "bill.deposit";

    /// <summary>
    /// A payment was taken from a customer and put to the payment provider. Recorded whatever the
    /// provider answered — a run of declines on one account is exactly what somebody comes looking
    /// for, and a trail holding only the successes could not answer them.
    /// </summary>
    public const string PaymentTaken = "payment.take";

    /// <summary>
    /// A balanced journal entry was posted to the general ledger. Recorded against <c>system</c>:
    /// postings happen in a consumer reacting to a fact another module has already stated, and the
    /// person behind that fact is named on the bill's or the payment's own entry.
    /// </summary>
    public const string JournalPosted = "journal.post";

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

    /// <summary>
    /// A row of <c>customers.customer_contacts</c>. Contact methods are audited against the contact
    /// they belong to rather than as entities of their own — the same call the meter's history lines
    /// and the bill's adjustments make. "What happened to this contact" is the question, and a
    /// snapshot carrying the whole method set answers it in one entry, primary flags included.
    /// </summary>
    public const string CustomerContact = "customers.customer_contact";

    /// <summary>
    /// A row of <c>customers.customer_profiles</c>, identified by the <b>customer</b> — which is
    /// also its primary key, so there is no second id to quote.
    /// </summary>
    public const string CustomerProfile = "customers.customer_profile";

    /// <summary>
    /// A row of <c>customers.customer_notes</c>. A correction is audited against the <b>new</b> row
    /// with the row it supersedes as the <c>before</c> — the register is append-only, so there is no
    /// entity that changed and claiming otherwise would make the trail disagree with the table.
    /// </summary>
    public const string CustomerNote = "customers.customer_note";

    /// <summary>
    /// A document produced for a customer and identified by that <b>customer</b> (WP-2.14) — a
    /// statement, a payment-history export.
    /// </summary>
    /// <remarks>
    /// Not a table, and deliberately not one: nothing is stored, because a statement is a view of
    /// records that already exist and storing a second copy of them would create a document that can
    /// disagree with the ledgers it was drawn from. What is stored is the entry saying it was
    /// produced, whose snapshot carries the range and the figures — enough to reproduce it exactly,
    /// which is what makes storing the file unnecessary. A bill reprint is audited against
    /// <see cref="Bill"/> instead, because there a row genuinely is the document.
    /// </remarks>
    public const string CustomerDocument = "customers.customer_document";

    /// <summary>
    /// A row of <c>customers.account_transitions</c> (WP-2.15) — a class change, a status change, a
    /// move-in, a move-out or a transfer.
    /// </summary>
    /// <remarks>
    /// Every transition is audited against <b>this</b> entity rather than against the customer or the
    /// account it moved, and uniformly across all five kinds. The alternative — a class change
    /// audited against the customer and a transfer against one of two accounts — would make "show me
    /// every transition this customer has been through" a query that has to know which kind it is
    /// looking for before it can find it. The row is what the trail points at; the snapshot on either
    /// side carries the customer's class, status, deposit and account, which is the before/after
    /// WORK_PACKAGES.md asks for and reads the same way whichever kind moved.
    /// </remarks>
    public const string AccountTransition = "customers.account_transition";

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
    /// A row of <c>billing.account_charges</c> (WP-2.16) — one fee raised against a service account.
    /// </summary>
    /// <remarks>
    /// Its own entity rather than a line of the bill's, unlike an adjustment: a charge exists before
    /// any bill carries it and may never reach one, so auditing it against a bill would mean naming
    /// a document that does not exist yet. Once it is billed, the bill's own entries take over.
    /// </remarks>
    public const string AccountCharge = "billing.account_charge";

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

    /// <summary>
    /// A row of <c>finance.journal_entries</c>. Lines are audited against the entry they belong to
    /// rather than as entities of their own — an entry without its lines is not an entry, and both
    /// are already append-only. The snapshot carries the lines for the same reason: the question
    /// asked of a ledger is always which accounts moved.
    /// </summary>
    public const string JournalEntry = "finance.journal_entry";

    /// <summary>A row of <c>assets.assets</c>.</summary>
    public const string Asset = "assets.asset";

    /// <summary>
    /// A row of <c>inventory.stock_items</c>. Movements and levels are audited against the item they
    /// belong to rather than as entities of their own: the ledger line is already immutable, and the
    /// question an auditor asks is "what happened to this item", not "what happened to this row".
    /// </summary>
    public const string StockItem = "inventory.stock_item";
}
