# STATUS.md — Utility ERP State

> Updated at the end of every session (Claude's final task, with DECISIONS.md).

## Current position
- **Phase:** 0 — Foundation
- **Current WP:** WP-0.1 (repo, solution skeleton, docs — targets net10.0)
- **Current branch:** feat/wp-0.1-skeleton
- **Last tag:** —

## Platform versions (law — see ARCHITECTURE.md)
.NET 10 LTS · Aspire 13.x (`aspire update` at gates) · React 19 + latest Vite · Node 24 LTS · PostgreSQL latest · Ubuntu LTS · Tailwind + shadcn/ui + lucide · Vitest + xUnit

## Testing note (read every session)
FAST loop = unit tests only, parallel, `--no-build`, NO `--maxcpucount:1`. Integration (shared Testcontainer + Respawn) and E2E run at gates only. See CONVENTIONS.md ⚡ section.

## In flight / carry-over
- (empty)

## Work packages

### Phase 0 — Foundation
- [ ] WP-0.1 Skeleton + docs
- [ ] WP-0.2 Aspire + infra
- [ ] WP-0.3 Auth + RBAC (8 roles)
- [ ] WP-0.4 Audit + approvals
- [ ] WP-0.5 Bus + outbox + Finance seam
- [ ] WP-0.6 React shell
- [ ] WP-0.7 CI + FAST test harness
- [ ] WP-0.8 Reference data + demo seeder (split)   → gate v0.1-phase0

### Phase 1 — Registries
- [ ] WP-1.1 Customers & service locations
- [ ] WP-1.2 Service accounts + lifecycle
- [ ] WP-1.3 Asset registry
- [ ] WP-1.4 Inventory & warehouses
- [ ] WP-1.5 Registry UIs + customer 360        → gate v0.2-phase1

### Phase 2 — Revenue Cycle
- [ ] WP-2.1 Meter registry + assignment
- [ ] WP-2.2 Readings + consumption + meter simulator
- [ ] WP-2.3 Rate engine + bill generation
- [ ] WP-2.4 Bill adjustments + audit
- [ ] WP-2.5 Payments simulator
- [ ] WP-2.6 Finance GL + journal posting
- [ ] WP-2.7 Revenue Cycle E2E + demo screen     → gate v0.3-revenue

### Phase 3 — Ops & Maintenance Cycle
- [ ] WP-3.1 Work order core + state machine
- [ ] WP-3.2 Crew simulator + assignment
- [ ] WP-3.3 Parts issuance ↔ inventory
- [ ] WP-3.4 Completion → asset history + costs
- [ ] WP-3.5 Ops Cycle E2E + demo + WO UI         → gate v0.4-operations

### Phase 4 — Purchasing, Finance views, Dashboards, Admin
- [ ] WP-4.1 Procurement lifecycle
- [ ] WP-4.2 Finance views (AR/AP/journal/trial balance)
- [ ] WP-4.3 Dashboards
- [ ] WP-4.4 Approvals & audit surfaces
- [ ] WP-4.5 Admin users/roles/permissions        → gate v0.5-phase4

### Phase 5 — Hardening & Demo Deploy
- [ ] WP-5.1 Prod/demo compose + guards
- [ ] WP-5.2 Proxy + TLS
- [ ] WP-5.3 Backups + restore proof
- [ ] WP-5.4 Observability + full-suite gate       → gate v1.0
