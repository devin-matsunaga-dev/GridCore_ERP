# ARCHITECTURE.md — Utility ERP

> Read fully before coding. Binding. If a WP forces a change here, updating this file is part of that WP.

## What this is

A utility-company ERP MVP: seven integrated modules (Customers, Meters, Billing, Payments, Assets, Work Orders, Inventory/Purchasing, Finance, Admin) that prove business processes *connect* — external systems simulated behind provider interfaces. Single deployment, multi-user, RBAC. The two demonstration workflows (Revenue Cycle, Ops & Maintenance Cycle) are the acceptance target.

## Topology

**Modular monolith**, one ASP.NET Core host, orchestrated by .NET Aspire. Modules are class libraries with their own schema; they talk through public service interfaces (in-process) and domain events for cross-module effects (esp. anything that touches Finance).

| Component | Tech | Role |
|---|---|---|
| `Web.Host` | ASP.NET Core | Hosts all module APIs |
| `Modules.Customers` | lib | Customers, service locations, service accounts |
| `Modules.Metering` | lib | Meters, readings, consumption, meter simulator |
| `Modules.Billing` | lib | Rate engine, bills, adjustments |
| `Modules.Payments` | lib | IPaymentProvider + sandbox simulator |
| `Modules.Assets` | lib | Asset registry, maintenance history |
| `Modules.WorkOrders` | lib | Work orders, crews (simulated) |
| `Modules.Inventory` | lib | Stock, warehouses, purchasing |
| `Modules.Finance` | lib | GL, journal entries, AR/AP, trial balance |
| `Platform` | lib | Auth, RBAC, audit, approvals, events/outbox, seed engine |
| `Contracts` | lib | Cross-module events + shared DTOs only |
| `Web` | React (Vite+TS) | SPA frontend |

## Stack versions (LTS/latest — Aug 2026; never scaffold EOL)

.NET 10 LTS · Aspire 13.x (latest; `aspire update` at gates) · React 19 + latest Vite · Node 24 LTS · PostgreSQL (newest Timescale-not-needed; plain PG latest) · Ubuntu LTS. No Python required for this project.

## Module boundaries (hard rules)

- A module NEVER queries another module's tables. Cross-module reads → that module's service interface; cross-module effects → **domain events**.
- `Contracts` holds events + shared value objects only (no entities/EF types).
- Each module owns its Postgres **schema** and its own EF migrations.
- **Finance is downstream of everyone.** Billing, Payments, and Inventory raise events (`BillIssued`, `PaymentApproved`, `GoodsReceived`); Finance consumes them and posts journal entries. Finance never calls back into other modules.

## Provider interfaces (the simulation seam — non-negotiable)

Define these in `Contracts` from day one; simulators live behind them:
- `IMeterReadingProvider` — simulator generates cycle batches + exceptions.
- `IPaymentProvider` — sandbox returns Approved/Declined/InsufficientFunds/Timeout/Refunded.
- `IVendorProvider` — test vendors, prices, lead times, receiving.
- `ICrewProvider` — technicians/crews for work-order assignment.
Production swaps an implementation via DI config only — no domain code changes.

## Communication & data

- In-process: direct service interfaces.
- Cross-module async: MassTransit + RabbitMQ via **transactional outbox** (never publish directly). Consumers idempotent (Platform dedupe helper).
- PostgreSQL, schema-per-module. Money = `decimal` everywhere, never float. Ledger entries are append-only.
- Redis: cache + rate-limit + SignalR backplane (dashboards).
- MinIO: document/report storage.

## Invariants (never break)

1. Every write endpoint produces an **audit** entry (user, action, entity, before/after).
2. Every event publish goes through the **outbox**; every consumer is idempotent.
3. **Double-entry integrity:** every financial transaction posts balanced journal entries (debits = credits); the ledger is append-only; corrections are new entries, never edits.
4. **Money is `decimal`;** rounding is explicit and centralized.
5. Sensitive actions (bill adjustment, approvals, inventory adjustment) are permission-gated AND audited.
6. External effects only ever go through provider interfaces — no direct simulator calls from domain code.
7. Migrations are append-only; never edit an applied migration.
8. Seed/demo data is environment-guarded (Development only) and never required for the app to function (reference data — rate plans defaults, chart of accounts, roles — ships via migration/bootstrap, not the demo seeder).

## Environments

- **Dev:** WSL2 (Ubuntu LTS), everything via Aspire AppHost (`aspire run`).
- **Prod/demo:** Linux host, Docker Compose from CI-built images (`aspire publish`), `ASPNETCORE_ENVIRONMENT=Production`, simulators still present (they ARE the product's external boundary for the MVP) but seed/demo generation disabled.

## Deferred (don't add unprompted)
SCADA, real AMI, real payments, payroll, GIS, outage prediction, mobile apps, bank reconciliation, regulatory reporting, forecasting, analytics/AI, microservice extraction, event sourcing.
