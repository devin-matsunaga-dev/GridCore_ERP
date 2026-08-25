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
~42 packages. Phases 0-2 are the critical path (foundation + first demonstration workflow). Money/finance WPs are [SENSITIVE] — heavier review, but still FAST unit tests. Keep the per-package loop under ~60s; if it creeps, move a test to the integration tier.
