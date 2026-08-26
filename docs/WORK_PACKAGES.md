# WORK_PACKAGES.md — Utility ERP

Each WP = one Claude Code session, ends with a Completion Report + fast-test verification + git checkpoint. Protocol in SESSION.md. Organized so the two demonstration workflows come together as early as possible.

## Session model
Branch → `claude` → "Read docs/SESSION.md and proceed" → scope summary → "go" → build → report (with FAST regression) → you verify → squash-merge. One WP per session; continuity via STATUS.md + DECISIONS.md. Steering docs: ARCHITECTURE, CONVENTIONS (⚡ testing rules), DESIGN.

---

# PHASE 0 — Foundation

### WP-0.1 — Repo, solution skeleton, docs
Solution: AppHost, Web.Host, module libs (Customers, Metering, Billing, Payments, Assets, WorkOrders, Inventory, Finance), Platform, Contracts, test projects (`*.UnitTests` per module + one `IntegrationTests`). `.gitignore`, `.editorconfig`. Docs already present — verify layout matches.
**Verify:** solution builds; `dotnet test tests/*UnitTests` runs (even if empty) in seconds.

### WP-0.2 — Aspire AppHost + infra
AppHost composes Postgres, Redis, RabbitMQ, Keycloak, MinIO; `/health` aggregates. Web dev server via AddNpmApp.
**Verify:** `aspire run` → dashboard all green; `/health` healthy.

### WP-0.3 — Auth + RBAC **[SENSITIVE]**
Keycloak realm w/ the 8 roles (Administrator, CustomerService, Billing, Finance, Warehouse, Technician, Supervisor, Manager) + test users; OIDC; policy-based authorization; `/api/me`. Swappable to other OIDC by config.
**Verify:** login per role; permission-gated test endpoint 403s for wrong role.

### WP-0.4 — Audit + approvals + platform
Append-only audit (user/action/entity/before-after) via one-line helper; lightweight approval workflow primitive (request→approve/reject, reusable); notification stub; scheduler.
**Verify (unit):** audit captured on a write; approval transitions enforced.

### WP-0.5 — Bus + outbox + Finance event seam **[SENSITIVE]**
MassTransit + RabbitMQ + EF outbox; idempotency helper; first `Contracts` events; a no-op Finance consumer proving the event→journal seam.
**Verify:** unit test for idempotency/dedupe; one integration test (gate tier) for outbox delivery.

### WP-0.6 — React shell (per DESIGN.md)
Vite+TS, Tailwind + shadcn + lucide, dark-green sidebar shell w/ grouped nav, topbar, OIDC login, protected routes, dark mode, TanStack Query, toast pattern. **Vitest** configured.
**Verify:** login; nav matches reference screenshot; `npm run test` runs fast.

### WP-0.7 — CI + FAST test harness
CI: build once, run `*.UnitTests` in parallel (NO `--maxcpucount:1`), then integration on a separate job/stage; Vitest; image build+push on tag; Dependabot. Shared Testcontainers **collection fixture + Respawn** scaffolding for the integration project (one container, reset per test).
**Verify:** push → CI green in minutes; break a test → red; confirm unit job finishes in seconds.

### WP-0.8 — Reference data + demo seeder (split)
**Migrations/bootstrap:** chart of accounts, default rate plan, roles, warehouses — the data the app NEEDS. **Demo seeder (Development-guarded):** small utility dataset (customers, locations, meters, assets, bills, inventory, work orders).
**Verify:** never-seeded DB is functional (can create a customer, post a journal); seeder fills a demo world; guard blocks it in Production.

**🏁 Phase 0 gate:** fresh clone → `aspire run` → login → outbox round-trip. Full gate suite green. Tag `v0.1-phase0`.

---

# PHASE 1 — Customers, Assets, Inventory (the registries)

### WP-1.1 — Customers & service locations
Customer records (class, status, deposit); service locations (separate address). CRUD + validation + audit + events.
**Verify:** unit (validation, state); create via API.

### WP-1.2 — Service accounts + lifecycle
Connect customer↔location; states Active/Pending/Disconnected/Closed; start/stop service workflows; account history.
**Verify (unit):** illegal transition blocked; start-service opens account; history recorded.

### WP-1.3 — Asset registry
Utility asset classes + fields (tag, serial, model, install date, status, condition, lat/long). CRUD; maintenance-history read model (filled later by work orders).
**Verify:** create each asset class; lat/long stored.

### WP-1.4 — Inventory & warehouses
Items, warehouses, qty on hand, min stock, adjustments (permission-gated + audited), receipts, issuance primitive.
**Verify (unit):** adjustment math; low-stock flag; unauthorized adjust → 403.

### WP-1.5 — Registry UIs + 360° customer page
Tables (filter/sort) + detail pages for customers/assets/inventory; customer 360 (service accounts → locations). Per DESIGN.md.
**Verify:** side-by-side with reference screenshot; drawers/pages work.

**🏁 Tag `v0.2-phase1`.**

---

# PHASE 2 — Metering → Billing → Payments → Finance (the REVENUE CYCLE)

### WP-2.1 — Meter registry + assignment
Meters (number, serial, type, status) assigned to service locations.
**Verify:** assign meter; one meter per active location rule.

### WP-2.2 — Readings + consumption + meter simulator
Manual readings + history; consumption = current−previous with rollover handling; `IMeterReadingProvider` simulator generating cycle batches incl. high/zero/missing exceptions.
**Verify (unit, heavy):** consumption math incl. edge cases; simulator produces exceptions deterministically with a seed.

### WP-2.3 — Rate engine + bill generation **[SENSITIVE — money]**
Base charges + tiered rates with effective dates; generate bill from consumption; bill states Draft/Issued/PartiallyPaid/Paid/Overdue/Cancelled.
**Verify (unit, heavy):** tiered calc across boundaries; effective-dating picks right rate; `decimal` precision exact.

### WP-2.4 — Bill adjustments + audit **[SENSITIVE]**
Authorized credits/corrections as immutable entries against the bill; permission-gated; full audit trail. **No `Adjusted` state** — a correction is money, not a lifecycle move, so the bill keeps its status and `AdjustmentTotal` carries the change.
**Verify:** unauthorized adjust → 403; adjustment audited with before/after.

### WP-2.5 — Payments simulator **[SENSITIVE]**
`IPaymentProvider` sandbox: Approved/Declined/InsufficientFunds/Timeout/Refunded. Approved → reduce balance, update invoice, record payment, raise `PaymentApproved`.
**Verify (unit):** each outcome; approved path produces exactly one balance change + event (idempotent on retry).

### WP-2.6 — Finance: GL + journal posting **[SENSITIVE — double-entry]**
Chart of accounts, journal entries; consume `BillIssued` (Dr AR/Cr Revenue) and `PaymentApproved` (Dr Cash/Cr AR); balanced-posting assertion; AR view + trial balance.
**Verify (unit):** every posting balances; trial balance nets to zero; (integration) event→journal end to end.

### WP-2.7 — Revenue Cycle E2E + demo screen
Wizard screen walking Create Customer→…→Accounting Entries; one **E2E test** asserting each downstream effect.
**Verify:** E2E green; demo screen runs the whole cycle visibly; numbers reconcile.

**🏁 REVENUE CYCLE COMPLETE. Tag `v0.3-revenue`.** (First half of MVP success criterion.)

---

# PHASE 2.5 — Customers Deepening (the CSR EXPERIENCE)

The Revenue Cycle proved the pipes; Customers is still the MVP spine from Phase 1 (records, service locations, service accounts, states). This phase builds the module out into a complete customer-service-rep desk **before** Billing is deepened, so the billing pass lands on a real customer surface instead of inventing one as it goes. Every package here is **internal / CSR-side**: no customer self-service portal, no external customer login — deferred, see DECISIONS.md. Each WP notes whether it touches Billing, which is the map for the later billing-deepening pass.

### WP-2.8 — Customer registration / intake wizard **[SENSITIVE — deposit]**
One guided onboarding flow, not eight screens: identity + contacts + customer class → create (or pick an existing) service location → open the service account → assess and collect the deposit → start service. Per-step validation (react-hook-form + zod), back/forward without losing entered data, a review step before commit, and one transactional commit at the end — an abandoned wizard leaves nothing behind. Deposit assessment reads the class-based deposit rule; collection is permission-gated and audited, and hands off to the WP-2.12 lifecycle rather than duplicating it. Emits the existing `CustomerCreated` / service-account events.
**Touches Billing:** sets up the billable account (class drives later rate selection) but generates **no bills**.
**Verify (unit):** each step's validation rules; class → deposit amount; a wizard abandoned mid-flow writes no customer, location or account; deposit collection without permission → 403. One gate-tier integration test for the single-transaction commit across the module's tables.

### WP-2.9 — CSR customer search
The lookup a rep uses fifty times a day: one box, one query, matching on **account number, customer name, phone, service address, or meter number**. Input classified before it is dispatched (digits-and-dashes → account/meter, digits → phone, else name/address), results ranked with exact matches first and each row labelled with what it matched on. Partial and case-insensitive name matching, normalised phone comparison (punctuation stripped), address matching that survives "St" vs "Street". Paged, keyboard-first (arrow + enter to the 360 page), and fast on the seeded demo dataset.
**Touches Billing:** no — read-only across Customers and Metering.
**Verify (unit):** input classification incl. ambiguous strings; phone and address normalisation; ranking puts an exact account-number hit first; a meter number resolves through to its customer; empty and no-match results render as such, not as an error. Gate-tier integration test for the search query itself (index behaviour is a DB fact).

### WP-2.10 — Customer 360 page
Deepens WP-1.5's 360 into the single pane a rep works from: contacts, **all** service accounts with their states, meters on each location, current balance, recent bills, recent payments, open work orders, and an **account timeline** merging every one of those into one reverse-chronological feed (account opened, bill issued, payment taken, adjustment made, note logged, work order raised). Composed from module read models over the event/query seam — the page never reaches into another module's tables. Per DESIGN.md; sections lazy-load independently so one slow panel cannot block the page.
**Touches Billing:** **yes** — reads bills, current balance and payment history; the bill/balance panels are the surface the billing-deepening pass will extend.
**Verify (unit):** timeline merge orders mixed-source entries correctly and is stable for equal timestamps; balance shown equals bills-issued minus payments minus credits; a customer with no bills, no meters and no work orders renders empty states rather than throwing. Vitest for the panels.

### WP-2.11 — Contact & profile management
Edit the customer profile a rep actually maintains: multiple **contacts** per customer (name, relationship, authorised-to-discuss flag) each with multiple **contact methods** (phone, mobile, email) with one primary per type; **mailing address distinct from the service address**, defaulting to it until explicitly separated; and **communication preferences** (bill delivery channel, outage and dunning notices, preferred language). All changes audited with before/after.
**Touches Billing:** **yes** — the mailing address is the bill-to address, and the bill-delivery preference is the hook the billing pass reads when bill delivery is built.
**Verify (unit):** exactly one primary method per type is enforced (promoting a second demotes the first); clearing the mailing-address override falls back to the service address; an unauthorised contact cannot be marked authorised-to-discuss; profile edit writes an audit entry with before/after.

### WP-2.12 — Deposit lifecycle **[SENSITIVE — money]**
The deposit as a tracked balance with a full lifecycle: **collect** (at intake or later), **hold** (on account, interest-bearing flag stored but not accrued in MVP), **apply to a bill** (reduces what is owed, as a payment-side effect not a bill mutation), and **refund on close** (net of any final balance, remainder written off or carried). Every transition is an immutable entry — never an edited amount — permission-gated, audited, and raises events so Finance posts it: collection Dr Cash / Cr Customer Deposits (a liability), refund the reverse, application Dr Customer Deposits / Cr AR.
**Touches Billing:** **yes** — apply-to-bill settles against an issued bill and must leave the bill's own adjustment trail alone (WP-2.4's rule holds: money moving is not a lifecycle state).
**Verify (unit):** every deposit posting balances (debits = credits); collect → hold → apply → refund arithmetic is exact in `decimal`; a refund cannot exceed the held balance; applying more than the bill's outstanding amount is refused; deposit action without permission → 403; each transition audited. Gate-tier integration test for deposit event → journal entry.

### WP-2.13 — Account notes / interaction log
Free-text **notes** and structured **logged interactions** (inbound call, outbound call, counter visit, field visit, complaint, billing dispute) on the customer or a specific service account. Each carries type, timestamp, the rep who logged it, an optional follow-up date, and an optional link to the bill, payment or work order it concerns. Notes are append-only — a correction is a new note referencing the old, never an overwrite — and pinned notes surface at the top of the 360. Feeds the WP-2.10 timeline.
**Touches Billing:** indirectly — a billing dispute is logged here and links to the bill, which is what makes the later billing pass able to show "why was this adjusted".
**Verify (unit):** append-only enforced (edit attempt → 409, correction creates a linked new note); interaction requires a valid type; follow-up date cannot be in the past; a note linked to a nonexistent bill is refused; pinned notes sort ahead of unpinned regardless of date.

### WP-2.14 — Customer documents
What a rep hands or sends a customer, all read-side: **view and reprint a bill** (the issued document reproduced exactly as issued, from stored figures — never recalculated, or a reprint could disagree with the original); **account statement** over a caller-chosen date range (opening balance, bills, payments, adjustments, deposit movements, closing balance, proving out); and a **payment history export** (CSV). Generation is audited — who reprinted what, for whom, when — because these leave the building.
**Touches Billing:** **yes** — the heaviest read across Billing and Payments in this phase; the statement's opening/closing balance arithmetic is the reconciliation the billing pass will lean on.
**Verify (unit):** a reprint of an adjusted bill reproduces the figures **as issued**, with adjustments shown separately rather than folded into the original lines; statement opening + activity = closing for a seeded range; a range with no activity yields a valid zero-activity statement; export escapes CSV-hostile characters in names and addresses; reprint is audited.

### WP-2.15 — Account transitions **[SENSITIVE]**
The two changes that alter what a customer is billed. **Class / status change:** residential↔commercial and status moves, each requiring a **reason code** from a fixed list plus free text, effective-dated, audited before/after. **Move-in / move-out:** close service at one location and open it at another for the *same* customer as one linked transfer (final read at the old location, initial read at the new, service history preserved on both accounts, deposit carried rather than refunded-and-recollected), and the standalone close/open cases. Guarded by WP-1.2's state machine — this phase adds reasons and linkage, it does not loosen the transitions.
**Touches Billing:** **yes** — a move-out triggers a final bill and a class change picks a different rate from the effective date forward. Both are stubs pointing at the billing-deepening pass; this WP records the effective dates and reasons that pass will consume.
**Verify (unit):** a transition without a reason code → 400; class change is effective-dated and does not retro-date past an issued bill; move-out then move-in links both accounts and carries the deposit exactly (no net money created); a transfer to an occupied location is refused; illegal state moves still blocked by the WP-1.2 machine; each transition audited with before/after.

**🏁 CSR EXPERIENCE COMPLETE. Tag `v0.3.5-customers`.** Customers is now a full service desk; the billing-deepening pass has a surface to land on.

---

# PHASE 2.6 — CUC Process Realism (the REAL UTILITY'S PROCESSES)

Phase 2.5 built the desk; this phase makes the desk work the way the real one does. The reference is CUC Saipan's published customer-service processes — what a customer applies for, what they are charged for it, what happens when they do not pay, and what the utility does with the deposit it is holding. Two rules run through every package here.

**Every figure is effective-dated reference data seeded by migration, never a constant.** CUC's own publications disagree with each other on amounts and change without notice, so a fee schedule is a table the application reads and a charge **stamps the row that priced it** — the `DepositRule` / `RatePlan` pattern one step on. Changing $185 to $200 is a migration, not a redeploy, and a reprint never re-reads a catalogue that has since moved.

**The security deposit is money held against a debt, not a fee.** CNMI Public Law 16-17 and CUC's published regulations oblige the utility to set the deposit against qualifying past-due amounts **before** disconnection, and to reconcile it against unpaid charges when the account closes. That makes the offset a *step in the disconnection path*, not a button a clerk remembers — WP-2.12's ledger is right, but nothing today is obliged to call it.

Still internal / CSR-side: no customer self-service portal and no external customer login (deferred, see DECISIONS.md), and **no new external integrations** — a fee, an inspection and a dunning notice are internal domain logic, not provider seams.

**WP-2.16 goes first and everything else waits on it.** There is no fee line on a bill today — `ChargeKind` is service charge and consumption only — so the late charge, the reconnection fee, the returned-payment fee and four Phase 3 packages have nowhere to land until there is one.

### WP-2.16 — Fee schedule & account charges **[SENSITIVE — money]**
The non-consumption half of what a utility charges for, which GridCore has no shape for at all. An effective-dated **fee schedule** as reference data beside `RatePlan` in Billing — code, description, amount, currency, service type, effective-from — seeded by migration exactly as `DefaultRatePlans` and `DepositRules` are, with a completeness check at startup so a declared code with no row fails loudly rather than at the counter. A new `ChargeKind.Fee` and an **account charge** raised against a service account: it lands on the next bill, or on a bill of its own where the customer is paying at the counter now. Assessment **stamps the schedule row id and the amount charged** onto the charge, the shape `DepositAssessment.RuleId` already gives a deposit, so a document reprinted after a schedule change still shows the figure the customer holds a copy of. The catalogue lives in **Billing, not Customers** — a fee is a published charge that becomes a receivable, and Customers must not own money that appears on a bill; the desk reads it over a directory seam the way it already reads `IBillDirectory`. Ships the published CUC figures as the demo schedule with their provenance in the description, so nobody reads $135 as authoritative.
**Touches Billing:** **yes** — a new line kind and a second source of receivables besides the rate engine.
**Verify (unit):** a fee charged today prices off today's schedule and a reprint after a change still shows the old figure; an unknown fee code → 400; charging without permission → 403; the Finance posting balances (Dr AR / Cr fee revenue); a fee line carries no tier or per-unit fields, which is what distinguishes it from a consumption line; the completeness check fails when a declared code has no row; a schedule row is never edited in place — a new figure is a new effective-dated row. One gate-tier integration test for the charge → journal entry.

### WP-2.17 — Service types & per-service deposit schedule **[SENSITIVE — deposit]**
The reference asks for three deposits — electric, water, wastewater — and GridCore cannot express any distinction between them, because a service account is a customer↔location link with no notion of *what service*. `ServiceType` moves out of `RatePlan` into `Contracts` and gains **Wastewater**; a service account **declares its type**, so one premise may hold an electric, a water and a wastewater account at once. `DepositRule` is re-keyed to **(customer class × service type)**, each rule carrying a **minimum** and an optional usage basis — `max(minimum, average monthly usage × n months)`, which is exactly how the reference describes the electric deposit. **Re-assessment** is the piece the desk is missing: what is held against what is now required, asked on demand and on any class or service change, answering with a shortfall a rep can read out. Wastewater arrives as the first **unmetered** account — a flat charge, no meter, no reading — which is a shape the module has to refuse to attach a meter to rather than one it merely never does.
**Touches Billing:** **yes** — rate-plan selection becomes per service type, and unmetered billing is a stub pointing at the billing-deepening pass.
**Verify (unit):** each (class × type) resolves to exactly one rule and a missing pair fails the completeness check; a usage-based assessment below the minimum returns the minimum; a customer with no reading history falls back to the minimum rather than assessing zero; re-assessment on an account holding more than required returns a shortfall of zero, never a negative; an unmetered account refuses a meter assignment; the existing class-keyed rules migrate to electric without changing what any current customer was assessed.

### WP-2.18 — Service application & document intake
CUC reviews an application before it establishes an account; WP-2.8's wizard creates one immediately. An **application** becomes a reviewable entity — submitted → under review → approved / rejected / withdrawn, with a reason code on every terminal move — and **approval is what opens the account**, which turns the intake wizard into the approve-and-open path rather than the only path. A required-document checklist per application type (photo ID, lease or deed, business licence for a commercial connection), with the documents themselves stored in **MinIO behind an `IDocumentStore` seam**: the object store has been composed in the AppHost since WP-0.2 and this is its first user. Type, uploader, size and checksum are recorded against the application so what was reviewed is provable years later.
**Touches Billing:** no — nothing here is billable; the deposit and the connection fee are assessed at approval by WP-2.16 and WP-2.17.
**Verify (unit):** approval is blocked while a required document is missing; a rejected application cannot be approved without a fresh submission; an upload of a disallowed content type → 400; approval without permission → 403; the store seam is **faked in the fast tier** — CONVENTIONS.md rule C, no container to test a checklist. One gate-tier integration test for a real MinIO round-trip.

### WP-2.19 — Delinquency, late charges & the statutory deposit offset **[SENSITIVE — money, PL 16-17]**
The package Public Law 16-17 is about. **Arrears** per service account, aged; a **late-charge run** applying the configured 1% per month of the *past-due* balance as a WP-2.16 fee, idempotent per bill per period. A **dunning sequence** as reference data — notice type, days past due, what it says — running reminder → delinquency → disconnection notice, where each notice served is a **record with a served-on date**: that record is the whole of what makes "the customer had an opportunity to pay before disconnection" provable rather than asserted. **Disconnection eligibility is computed, never typed**: arrears over the threshold, *and* the disconnection notice served, *and* the statutory waiting period elapsed, *and* no kept payment arrangement. And the offset the law requires: evaluating eligibility **applies the held deposit to qualifying past-due amounts first**, so an account whose deposit clears its arrears is **not eligible for disconnection at all**. The `DepositEntry` that results carries the statutory basis in its reason, because a legally obliged movement should defend itself from the trail without anyone remembering why it happened.
**Touches Billing:** **yes** — the late charge is a fee on a bill and arrears is a read across the whole bill history.
**Verify (unit):** the 1% is taken on the **past-due** balance and not on the bill total; running the late-charge job twice charges once; a $300 deposit against $200 arrears offsets exactly $200 and leaves the account **ineligible**; a $100 deposit against $200 arrears offsets $100 and leaves it eligible; eligibility is false when the notice was never served and false again inside the waiting period; every offset entry balances, is audited, and names the statutory basis; an offset attempted without permission → 403. One gate-tier integration test for the offset event → journal entry.

### WP-2.20 — Payment arrangements **[SENSITIVE — money]**
What Customer Service does instead of disconnecting. An arrangement against a stated arrears balance: a down payment, an instalment count, a schedule of due dates, each instalment settled by a real payment through WP-2.5. States proposed → active → kept, or **broken** on a missed instalment — broken restores disconnection eligibility, active suppresses it, which is the only reason the state matters to anything outside this feature. Permission-gated with a limit: a rep may arrange within it, and beyond it the arrangement uses WP-0.4's approval primitive rather than a second bespoke workflow.
**Touches Billing:** **yes** — an arrangement is a promise about receivables that already exist. It **creates no money and never mutates a bill**: the customer still owes what the bills say, and this records how and when it will arrive.
**Verify (unit):** instalments sum exactly to the arrangement balance in `decimal`, with the remainder landing on the last instalment rather than being spread; a payment applies to the earliest unpaid instalment; one missed due date breaks it; a broken arrangement cannot be resumed, only replaced; an arrangement for more than the arrears is refused; an arrangement beyond the rep's limit requires approval before it becomes active; the arrangement leaves every bill's status untouched.

### WP-2.21 — Disconnection for non-payment & reconnection **[SENSITIVE — money]**
The reference's "Customer Service determines the outstanding amount, deposit requirements and applicable reconnection charges before service is restored" — one computed, itemised **amount to restore**: remaining arrears + the reconnection fee (WP-2.16) + any deposit shortfall (WP-2.17's re-assessment). That figure is what a rep reads down the telephone, so it itemises to its own total or it is worth nothing. Disconnection for non-payment becomes a **process consuming WP-2.19's eligibility** rather than WP-1.2's free-text `/stop`, and reconnection is authorised once the restore amount is settled. Both raise a **service request** carrying the field act — a stub with a seam until WP-3.9 dispatches it, because a transition register does not put a technician at a premise (WP-2.15's rule) and neither does a reconnection authorisation.
**Touches Billing:** **yes** — the restore amount is the heaviest read in the phase, across bills, payments, fees and the deposit ledger.
**Verify (unit):** the itemisation adds up to the total it prints; a reconnection authorised against a partly-paid restore amount → 409; disconnection for non-payment on an ineligible account → 409; the reconnection fee is charged once per disconnection and not once per attempt; the deposit shortfall inside the restore amount is the **re-assessed** figure, not the original rule amount; authorising a reconnection does not energise the account; each act audited with before/after.

### WP-2.22 — Returned payments & the NSF fee **[SENSITIVE — money]**
A payment that settles and then bounces is not a shape GridCore can hold — `PaymentStatus` stops at Refunded. Adds **`Returned`** and a **reversal**: the bill's balance is restored, the bill returns to whichever status its remaining balance now dictates, and the reversal is a **new entry, never an edited payment** — WP-2.4's rule, one module over. The NSF fee is assessed through WP-2.16 on the reversal, and the Finance posting is the exact reverse of the original plus the fee. Also records a **payment channel** on every payment — counter, online, telephone, prepaid, mail — which the reference lists as distinct routes and which is what lets a cashier tell the desk's own takings from an online payment.
**Touches Billing:** **yes** — a reversal moves a bill *backwards* out of Paid, and it is the one legitimate way that happens.
**Verify (unit):** reversing an approved payment restores the exact balance in `decimal`; a bill that had reached Paid returns to Issued or PartiallyPaid as its remaining balance dictates; a payment cannot be returned twice; a declined payment cannot be returned at all; the reversal postings net to zero against the original; the **NSF fee survives the reversal** and is not itself reversed; the channel is required at record time and immutable afterwards.

### WP-2.23 — Billing disputes & leak adjustments **[SENSITIVE]**
WP-2.13 logs a dispute as an interaction, and a note is not a case. A **dispute** raised against a bill, routed to a queue, moving received → investigating → resolved (upheld / adjusted / rejected / withdrawn) with a required resolution note — and resolving it as *adjusted* is what **raises the WP-2.4 adjustment**, which is what finally makes "why was this bill credited" answerable from the credit itself. Alongside it, the reference's high-bill and leak path: a reported usage anomaly, an investigation record, a **qualification rule as reference data** (excess over the historical average, once per n months, capped), and a **calculated** leak credit at the configured rate. Both feed the WP-2.10 timeline; the WP-2.13 dispute interaction stays as the record of the telephone call that started it.
**Touches Billing:** **yes** — the adjustment is the outcome, and WP-2.4's immutable-correction rule holds unchanged: a credit is a new entry against the bill, never a rewrite of it.
**Verify (unit):** a dispute resolved as *adjusted* with no adjustment raised → 409; the leak credit equals excess-over-average at the configured rate, exact in `decimal`; a customer already granted an adjustment inside the window does not qualify; the credit is capped where the rule caps it; a dispute against a draft bill is refused (nothing has been priced at the customer yet); resolving without permission → 403; the resolution note is required on every terminal move.

### WP-2.24 — Change of account holder
The reference's "change account holder — generally processed similarly to establishing a new account; new deposit may be required", which is a different act from WP-2.15's transfer and must not reuse its machinery. A **handover at one premise between two customers**, as one linked act: a final read and closure for the outgoing account, a new account for the incoming customer at the same location, and the deposit **refunded to the outgoing customer and separately assessed on the incoming one**. Never carried — WP-2.15's zero-direction `Transferred` entry exists precisely because both accounts on a transfer belong to *one* customer, and a handover is two, so money genuinely does leave and genuinely does arrive. Recorded in WP-2.15's transition register with a new kind and its own reason codes, guarded so the premise is never occupied by both accounts on the same day.
**Touches Billing:** **yes** — a final bill for the outgoing customer, a stub pointing at the billing-deepening pass, and the incoming account starts a fresh service period.
**Verify (unit):** the outgoing deposit is a `Refunded` entry and the incoming a `Collected` one — a `Transferred` entry on a handover fails the test that says so; the two accounts do not overlap by a day; a handover to a customer already holding an account at that premise → 409; a handover that would leave the outgoing account open is unreachable rather than merely unwritten; the register row names both accounts **and** both customers; audited on both sides with before/after.

**🏁 CUC PROCESS REALISM COMPLETE. Tag `v0.3.6-cuc-process`.** The desk now applies, charges, chases, arranges, disconnects, restores and reconciles the way the real one does — everything that does not need a crew at a premise.

---

# PHASE 3 — Work Orders → Inventory Issuance → Assets → Finance (OPS & MAINTENANCE CYCLE)

### WP-3.1 — Work order core + state machine
Types (inspection/PM/repair/meter-replacement/connect/disconnect); Open→Assigned→InProgress→Completed/Cancelled; links to asset/location/customer; priority, dates, notes.
**Verify (unit):** transitions guarded; links resolve.

### WP-3.2 — Crew simulator + assignment
`ICrewProvider` technicians/crews; assign to work order; labor records.
**Verify:** assignment; labor captured.

### WP-3.3 — Parts issuance ↔ inventory
Issue/reserve parts to a work order → reduces inventory, records materials + cost against the job; raises events for Finance.
**Verify (unit):** issuance decrements stock exactly; reservation vs issue; over-issue blocked.

### WP-3.4 — Completion → asset history + costs → Finance **[SENSITIVE]**
Completing a work order writes to asset maintenance history and posts costs (Dr expense/inventory as appropriate) via events.
**Verify (unit):** completion updates asset history; cost postings balance; (integration) event→journal.

### WP-3.5 — Ops Cycle E2E + demo screen + work-order UI
Work-order feed/board + detail per DESIGN.md; wizard for the Ops cycle; **E2E test** asserting inventory reduction, asset-history update, cost recording.
**Verify:** E2E green; board matches reference; demo runs top-to-bottom.

**🏁 OPS CYCLE COMPLETE. Tag `v0.4-operations`.** (Second half of MVP success criterion — both workflows now demonstrable.)

## Phase 3 (continued) — CUC process packages deferred from Phase 2.6

Six packages the CUC reference asks for that **cannot be built in Phase 2.6**, because each one needs a crew, a part or a completed job — the work-order core, the crew simulator, parts issuance and job costing that WP-3.1 through WP-3.4 introduce. They are placed here rather than forced early, and each names its dependency. They run **after** the `v0.4-operations` gate, so the second demonstration workflow is proved before the field-side process work lands on top of it.

### WP-3.6 — Connection & inspection work orders
*Depends on: WP-3.1 (work-order core, which already declares connect / disconnect / inspection types) and WP-2.16 (fee schedule).*
The middle of the reference's installation flow, which Phase 2.6 deliberately left as a seam: an approved WP-2.18 application raises a **connection order**, an **inspection order** carries a pass/fail result, and the account is energised only by a completed connection order — never by an API call, which is what `/start` is today. **The first inspection is free and each subsequent one is charged**, the published rule and the reason the count lives on the connection rather than on the order. Retires WP-2.21's service-request stub.
**Verify (unit):** an account cannot reach Active without a completed connection order; the second inspection on one connection charges and the first does not; a failed inspection blocks energising; a cancelled connection order leaves the account Pending.

### WP-3.7 — Installation cost assessment **[SENSITIVE — money]**
*Depends on: WP-3.2 (labour), WP-3.3 (parts issuance), WP-3.4 (job costing), WP-2.16, WP-3.6.*
The reference's central warning made real — "a $135 meter/service fee does not necessarily mean the entire installation costs $135". Turns the actual labour, materials, equipment hours and a configured administrative rate on a connection order into a customer charge, itemised so the customer can be told what they are paying for.
**Verify (unit):** the assessed cost equals labour + materials + equipment + admin exactly, in `decimal`; issuing further parts after assessment requires a **re-assessment** rather than silently moving the charge; a charge is raised once per order; the admin rate comes from the schedule, not a constant.

### WP-3.8 — Meter testing requests & fee **[SENSITIVE]**
*Depends on: WP-3.1, WP-2.16, WP-2.23.*
A customer request to have a meter tested → a test order → a result. The fee follows the meter type from the catalogue ($75 single-phase, $110 three-phase as published) and is **waived when the meter is found faulty** — the rule that makes the test worth requesting. A faulty result feeds a WP-2.23 dispute, because a meter that was over-reading has bills behind it.
**Verify (unit):** the fee follows meter type; a faulty result charges nothing; a test against an unmetered (wastewater) account is refused; a faulty result opens a dispute rather than adjusting a bill directly.

### WP-3.9 — Field execution of disconnection & reconnection
*Depends on: WP-3.1, WP-3.2, WP-2.21.*
Dispatches the orders WP-2.21 raises and makes **completion the thing that moves the account's state**, closing the gap where a reconnection is authorised in the office and the supply is assumed to follow.
**Verify (unit):** a completed reconnection order energises the account; an order cancelled in the field leaves the account Disconnected with the restore amount still standing; a disconnection order cannot complete against an account that has since paid.

### WP-3.10 — Unauthorized connection findings & penalty **[SENSITIVE — money]**
*Depends on: WP-3.1, WP-2.16.*
A field finding of unauthorised connection or use becomes an evidence record, and the record is what permits the penalty (~$550 as published) plus an estimate of the unbilled usage. The fee itself ships in WP-2.16's catalogue; what waits for Phase 3 is the finding that justifies it.
**Verify (unit):** a penalty requires a finding; one incident cannot be found twice; the penalty is an ordinary fee charge and posts like one; the unbilled estimate is a separate line from the penalty.

### WP-3.11 — PayGo / prepaid conversion
*Depends on: WP-3.1 (meter-replacement orders), WP-2.16, **and the billing-deepening pass** (see below).*
The largest deferred item and the one that is not only a fee. An existing customer applies to convert, pays the published meter-change fee (~$95), a meter-exchange order swaps the meter, and the account moves to a **prepaid billing mode** with a credit balance and top-ups. The conversion half is Phase 3 work; the **prepaid billing half is not a Phase 3 shape** and belongs with the billing-deepening pass — a prepaid account is not billed on a cycle, which is an assumption the whole billing module currently holds.
**Verify (unit):** an account cannot enter prepaid mode without a completed meter exchange; a prepaid account is skipped by the billing run; a top-up increases the credit balance and posts to Finance; converting an account with arrears is refused until they are settled or arranged.

**🏁 Tag `v0.4.5-cuc-field`.** Every process in the CUC reference is now modelled except those explicitly held for the billing-deepening pass.

---

> **Unscheduled: the billing-deepening pass.** Referenced throughout Phase 2.5 and by four packages above — the final bill on a move-out or handover, bill delivery against the WP-2.11 preference, the final and initial meter reads a transfer implies, unmetered (wastewater) billing, and prepaid billing. It is **not yet a phase in this document**, and stubs are now pointing at it from three phases. It should be named and scheduled before Phase 2.6 ships more of them.

---

# PHASE 4 — Purchasing, Finance views, Dashboards, Admin polish

### WP-4.1 — Procurement lifecycle
Purchase Request→Approval→PO→Receive Goods; `IVendorProvider` simulated vendors/prices/lead times; receiving increments inventory + raises `GoodsReceived` (Dr Inventory/Cr AP).
**Verify (unit):** lifecycle transitions; receiving updates stock + posts AP; approval gate.

### WP-4.2 — Finance views (AR/AP/journal/trial balance)
Read screens: AR aging summary, AP summary, journal list, trial balance. Numeric tables per DESIGN.md.
**Verify:** figures reconcile with seeded transactions; trial balance balances.

### WP-4.3 — Dashboards
Home dashboard (KPIs, work-order donut, alerts, feed, quick actions) + module dashboards, per reference screenshot; SignalR for live-ish counts.
**Verify:** matches reference; KPIs pull real seeded numbers.

### WP-4.4 — Approvals & audit surfaces
Approvals inbox (PRs, adjustments); audit log viewer with before/after diff.
**Verify:** approve a PR from inbox advances it; audit search works; permissions enforced.

### WP-4.5 — Admin: users/roles/permissions UI
Manage users, role assignment, permission matrix.
**Verify:** role change reflects in access; sensitive toggles audited.

**🏁 Tag `v0.5-phase4`.**

---

# PHASE 5 — Hardening & Demo Deployment

### WP-5.1 — Prod/demo compose + env guards
`aspire publish` → hardened compose; `ASPNETCORE_ENVIRONMENT=Production`; simulators present, demo-seeder disabled; pinned images, volumes, limits.
**Verify:** clean box `docker compose up` healthy from images; seeder refuses; reboot recovers.

### WP-5.2 — Reverse proxy + TLS
Caddy/Nginx, cert, security headers, SignalR passthrough.
**Verify:** padlock; internal ports closed.

### WP-5.3 — Backups + restore proof
pg_dump + volume snapshot; documented restore.
**Verify:** restore onto scratch box; log in; data intact.

### WP-5.4 — Observability + full-suite gate
OTel dashboards; ensure full gate suite (unit+integration+E2E) green and timed; document runtimes.
**Verify:** both demonstration workflows pass E2E on the deployed build; suite runtime recorded.

**🏁 MVP COMPLETE. Tag `v1.0`.**

---

## Deferred (Phase 6+ backlog)
SCADA, real AMI, real payment gateway, payroll, GIS, outage prediction, mobile field app, bank reconciliation, regulatory reporting, forecasting, analytics/AI. Each becomes a WP when scheduled.

## Sizing
~57 packages. Phases 0-2 are the critical path (foundation + first demonstration workflow). Money/finance WPs are [SENSITIVE] — heavier review, but still FAST unit tests. Keep the per-package loop under ~60s; if it creeps, move a test to the integration tier.
