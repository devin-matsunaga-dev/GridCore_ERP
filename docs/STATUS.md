# STATUS.md — Utility ERP State

> Updated at the end of every session (Claude's final task, with DECISIONS.md).

## Current position
- **Phase:** 0 — Foundation
- **Current WP:** WP-0.4 (Audit + approvals + platform)
- **Current branch:** feat/wp-0.3-auth-rbac (WP-0.3 complete, awaiting squash-merge)
- **Last tag:** —

## Platform versions (law — see ARCHITECTURE.md)
.NET 10 LTS · Aspire 13.x (`aspire update` at gates) · React 19 + latest Vite · Node 24 LTS · PostgreSQL latest · Ubuntu LTS · Tailwind + shadcn/ui + lucide · Vitest + xUnit

## Testing note (read every session)
FAST loop = unit tests only, parallel, `--no-build`, NO `--maxcpucount:1`. Integration (shared Testcontainer + Respawn) and E2E run at gates only. See CONVENTIONS.md ⚡ section.

## In flight / carry-over
- `docs/design/reference-dashboard.png` (canonical UI reference named by DESIGN.md) is **missing** — needed before WP-0.6.
- `web/` React app not created yet — the fast loop's `npm --prefix web run test` line is inert until WP-0.6, and the AppHost skips its Vite dev-server resource while the directory is absent (WP-0.6 creates `web/`; nothing else to wire).
- Docker **is** reachable now (WSL integration enabled), and WP-0.3 was verified live with `aspire run`: all resources green, `/health` 200, all eight roles logging in, `/api/me/admin-probe` 200 for Administrator and 403 for the other seven.
- The Aspire **CLI is 13.4.6** while the SDK/packages are **13.5.2** — run `aspire update` (STATUS says at gates) before the Phase 0 gate.
- `Web.Host` now requires AppHost-supplied connection strings (`gridcore`, `redis`, `rabbitmq`) to start, **and** `Authentication:Authority` + `Authentication:Audience` (it throws a named `InvalidOperationException` without them); the gate-tier `WebApplicationFactory` boot (WP-0.7) must supply the connection strings from the shared Testcontainer and stub the authority.
- `/health` is mapped by `MapDefaultEndpoints()` in **Development only** (Aspire's default, for the security reasons in aka.ms/aspire/healthchecks). If the demo deploy needs it exposed, that is WP-5.4.
- The Keycloak realm lives at `src/AppHost/keycloak/realms/gridcore-realm.json` and is imported **in Development only** (it carries eight test users, password `Dev!Passw0rd`). Keycloak imports a realm once into its data volume: after editing that file, `docker volume rm` the keycloak volume or the change is ignored. **WP-5.1 owes a production realm** — same roles and clients, real users, no default passwords — since nothing is imported outside Development.
- The host is now **secure by default**: a fallback policy requires an authenticated caller, so every new module endpoint needs `.RequirePermission(...)` (or a deliberate `.AllowAnonymous()`). Add new permissions to `Platform/Security/Permissions.cs` and grant them in `RolePermissionMap` — nowhere else.
- Aspire resolves Keycloak's `http` endpoint to an **https** proxy URL (`https://localhost:<port>/realms/gridcore`), so that is the issuer the SPA must use in WP-0.6; using a different host for login than for token validation would break issuer matching.
- WP-0.4 (audit) should write entries against `ClaimsPrincipal.UserId()` / `UserName()` from `Platform/Security/GridCorePrincipal.cs` rather than reading claims directly.
- MinIO is up as a container with `MinIO__Endpoint/AccessKey/SecretKey` handed to the host, but **no client is wired** — whichever WP first stores a document owns that.
- Aspire project templates were installed locally via `dotnet new install Aspire.ProjectTemplates` (13.5.2); CI must do the same or vendor the AppHost SDK — WP-0.7.
- New unit-test projects must be added to `tests/UnitTests.slnf` **and** `GridCore.slnx` or the fast loop silently skips them.

## Work packages

### Phase 0 — Foundation
- [x] WP-0.1 Skeleton + docs
- [x] WP-0.2 Aspire + infra
- [x] WP-0.3 Auth + RBAC (8 roles)
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
