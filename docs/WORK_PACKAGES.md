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
~34 packages. Phases 0-2 are the critical path (foundation + first demonstration workflow). Money/finance WPs are [SENSITIVE] — heavier review, but still FAST unit tests. Keep the per-package loop under ~60s; if it creeps, move a test to the integration tier.
