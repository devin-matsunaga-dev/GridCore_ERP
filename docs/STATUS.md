# STATUS.md — Utility ERP State

> Updated at the end of every session (Claude's final task, with DECISIONS.md).

## Current position
- **Phase:** 0 — Foundation
- **Current WP:** WP-0.8 (Reference data + demo seeder) → then the Phase 0 gate
- **Current branch:** feat/wp-0.7-ci-test-harness (WP-0.7 complete, awaiting squash-merge; **not pushed** — owner pushes)
- **Last tag:** —

## Platform versions (law — see ARCHITECTURE.md)
.NET 10 LTS · Aspire 13.x (`aspire update` at gates) · React 19 + latest Vite · Node 24 LTS · PostgreSQL latest · Ubuntu LTS · Tailwind + shadcn/ui + lucide · Vitest + xUnit

## Testing note (read every session)
FAST loop = unit tests only, parallel, `--no-build`, NO `--maxcpucount:1`. Integration (shared Testcontainer + Respawn) and E2E run at gates only. See CONVENTIONS.md ⚡ section.

## In flight / carry-over
- **CI is live (WP-0.7)** — `.github/workflows/ci.yml`, three jobs: `dotnet-unit` and `web` in parallel on every push/PR, `integration` behind both. `.github/workflows/release.yml` builds and pushes the host image to GHCR on a `v*` tag only. `.github/dependabot.yml` watches nuget, npm and github-actions. **Nothing has been pushed yet, so no CI run has been observed** — the first push to `origin` is what proves the pipeline green.
- Node is pinned in **`web/.nvmrc`** (24) and CI reads the version from that file. Change the Node version there, not in the workflow.
- **`tests/Build.UnitTests` is new** — fast tests that assert on the repo's own build/CI configuration. It is what now catches a unit-test project missing from `tests/UnitTests.slnf`, a `--maxcpucount:1` creeping back into CI, a MassTransit 9.x bump, and a `lint` script that stopped linting. Adding a workflow flag that one of these forbids will fail the fast loop, by design.
- **The linter is `oxlint`, not ESLint** (`web/.oxlintrc.json`; `npm run lint` = `oxlint && tsc -b --noEmit`). ESLint is *impossible* here: `typescript@7` is the native compiler and its npm package exports only `{ version, versionMajorMinor }` — no `createSourceFile`/`createProgram` — so typescript-eslint cannot parse a file and ESLint cannot read `.ts`/`.tsx` at all. Revisit if typescript-eslint ships TS 7 support. The type check stays alongside it because oxlint is type-unaware.
- The SPA is served **unproxied on a fixed port 5173** — `WebComposition.WebAppPort`, pinned in the AppHost by `WithGridCoreWebAppEndpoint` (`Port`/`TargetPort` set, `IsProxied = false`) *and* in `web/vite.config.ts` (`strictPort`). Aspire's default proxied endpoint hands the browser a **randomly allocated** port, and Keycloak then refuses the login with "Invalid parameter: redirect_uri", because an OIDC client may only return to a URI it registered. Three places must name the same port: the AppHost constant, `vite.config.ts`, and the realm export's `redirectUris`/`webOrigins`/`rootUrl`/`post.logout.redirect.uris`. Two fast tests hold them together (`WebCompositionTests`, `KeycloakRealmExportTests`); changing the port also needs `docker volume rm` on the Keycloak volume, since a realm is imported only once.
- The browser talks to the API **same-origin** via Vite's `/api` proxy (target resolved in `web/src/lib/api-target.ts` from Aspire's `services__web-host__*` variables; Aspire currently injects http only). The dev server prints the resolved target, the OIDC authority, proxy errors and any 4xx/5xx on startup — check that banner first when the SPA loads but every request fails. That is why `Web.Host` still has **no CORS policy** — anything that changes the SPA's origin has to add one deliberately.
- The AppHost passes `VITE_OIDC_AUTHORITY/CLIENT_ID/AUDIENCE` from `WithGridCoreWebAppConfiguration`, built from the same expression as the API's `Authentication__Authority`. `AddGridCoreWebApp` now takes `GridCoreInfrastructure` as a second argument.
- **The gate tier now has one shared fixture** — `tests/IntegrationTests/Infrastructure/GateFixture.cs`. It starts one Postgres, one RabbitMQ and one Redis container for the whole run, migrates once, boots the **real `Web.Host`** through `WebApplicationFactory<Program>`, and resets between tests with Respawn. Every gate test class is `[Collection(GateCollection.Name)]` and `[Trait("Category","Integration")]`; a class that starts its own container is a bug. WP-0.5's separate `OutboxFixture` is gone.
- Two things the gate fixture learned the hard way: host configuration must arrive as **environment variables** (under minimal hosting `Program.cs` reads `builder.Configuration` before a test host's `ConfigureAppConfiguration` callbacks run, so the connection strings landed after the host had already thrown), and Respawn **must not** truncate MassTransit's outbox/inbox tables or `platform.processed_messages` — a background service polls them for the life of the run and `TRUNCATE` wants a lock it is holding. Tests isolate on fresh Guid v7 event ids instead.
- A new module's schema needs nothing added to the reset list: `__ef_migrations_history` tables are discovered from `information_schema`.
- Two failure-handling rules the shell already learned the hard way: anything that publishes state a child will read on mount (the access token) is assigned **during render**, because child effects run before parent effects; and a loading or error state fills in a control's text rather than replacing the control, so a stalled request can never take away the only way to sign out.
- Frontend conventions to copy: server state is **TanStack Query** with one typed client per module over `web/src/api/client.ts` (`api.get/post/...`, RFC 7807 → `ApiError`, 4xx never retried); errors surface through the shared `toast` facade, never `sonner` directly; every status renders through the semantic map in `web/src/components/ui/status.tsx`; all money/dates/percentages go through `web/src/lib/format.ts`.
- New protected pages need nothing extra — `RequireAuth` wraps the whole router. A **new nav destination is added in one place**, `web/src/components/shell/navigation.ts`; `routes/routes.tsx` derives the placeholder route from it, so a WP replacing an area swaps that entry's element.
- `web/src/features/dashboard/demo-data.ts` holds **static** reference-dashboard figures so the design system is provable today. **WP-4.3 owns replacing it with queries** — nothing else imports it. The dashboard now exercises sparklines, a paginated table, a combined bar/line chart and a description list, so those components exist for later WPs to reuse (`components/ui/pagination.tsx`, `components/ui/select.tsx`, `features/dashboard/components/sparkline.tsx`).
- Layout rules that bit once and will again: grid tracks are `minmax(0, 1fr)`, never bare `1fr` (a bare track floors at its content's min-content width and pushes the row past the viewport); the three-across card rows are **2xl**, two-across at the 1280 floor.
- Tailwind is **v4** (CSS-first): tokens live in `web/src/index.css` under `@theme inline`, there is no `tailwind.config.js`. shadcn components are hand-written under `web/src/components/ui/`; `components.json` is checked in so `npx shadcn@latest add` works.
- TypeScript is **7.x** (the native compiler): `baseUrl` is removed, so path aliases in `tsconfig.app.json` are relative to the file. It is also why ESLint cannot be used — see the oxlint note above.
- The production bundle is ~860 kB (~260 kB gzipped), mostly Recharts and `oidc-client-ts`. Fine for the demo; if it matters, route-level `React.lazy` is the fix, not a new dependency.
- Vitest runs 124 tests in ~3.0s; the whole fast loop (dotnet build + 232 .NET unit tests + Vitest) is **~11s**. The full gate suite (5 tests, three containers started from cold) is **~12s** — measured, not estimated.
- **WP-0.6 was verified live** with `aspire run`: the SPA comes up on `http://localhost:5173`, login through Keycloak completes, and `/api/me` returns the caller so the sidebar shows their name and role. Getting there needed three fixes worth remembering — the SPA endpoint had to be pinned and unproxied (Aspire's random port broke `redirect_uri`), the proxy's fallback target pointed at a port nothing listens on, and the access token was published from an effect so the first request went out unauthenticated.
- Docker **is** reachable now (WSL integration enabled), and WP-0.3 was verified live with `aspire run`: all resources green, `/health` 200, all eight roles logging in, `/api/me/admin-probe` 200 for Administrator and 403 for the other seven.
- The Aspire **CLI is 13.4.6** while the SDK/packages are **13.5.2** — run `aspire update` (STATUS says at gates) before the Phase 0 gate.
- `Web.Host` requires AppHost-supplied connection strings (`gridcore`, `redis`, `rabbitmq`) to start, **and** `Authentication:Authority` + `Authentication:Audience` (it throws a named `InvalidOperationException` without them). The gate fixture supplies all five; the stubbed authority never has to resolve, because a caller with no token is refused before any metadata fetch.
- `/health` is mapped by `MapDefaultEndpoints()` in **Development only** (Aspire's default, for the security reasons in aka.ms/aspire/healthchecks). If the demo deploy needs it exposed, that is WP-5.4.
- The Keycloak realm lives at `src/AppHost/keycloak/realms/gridcore-realm.json` and is imported **in Development only** (it carries eight test users, password `Dev!Passw0rd`). Keycloak imports a realm once into its data volume: after editing that file, `docker volume rm` the keycloak volume or the change is ignored. **WP-5.1 owes a production realm** — same roles and clients, real users, no default passwords — since nothing is imported outside Development.
- The host is now **secure by default**: a fallback policy requires an authenticated caller, so every new module endpoint needs `.RequirePermission(...)` (or a deliberate `.AllowAnonymous()`). Add new permissions to `Platform/Security/Permissions.cs` and grant them in `RolePermissionMap` — nowhere else.
- Aspire resolves Keycloak's `http` endpoint to an **https** proxy URL (`https://localhost:<port>/realms/gridcore`), so that is the issuer the SPA uses (WP-0.6 wires it through `VITE_OIDC_AUTHORITY`); using a different host for login than for token validation would break issuer matching.
- Audit and services take **`ICurrentUser`** (`Platform/Security/ICurrentUser.cs`) — never `IHttpContextAccessor` or raw claims. Outside a request it resolves to `SystemUser` (`system`, ungated); an anonymous caller inside a request holds nothing. WP-0.5 consumers get `system` attribution for free.
- MinIO is up as a container with `MinIO__Endpoint/AccessKey/SecretKey` handed to the host, but **no client is wired** — whichever WP first stores a document owns that.
- ~~Aspire project templates / CI~~ **resolved (WP-0.7):** CI needs neither. Templates are for scaffolding; the AppHost's `Sdk="Aspire.AppHost.Sdk/13.5.2"` resolves from nuget.org at restore. Verified with a cold restore into an empty package folder.
- New unit-test projects must be added to `tests/UnitTests.slnf` **and** `GridCore.slnx` or the fast loop silently skips them.
- **EF Core is now in the repo** (10.0.11 + Npgsql 10.0.3, central-pinned). A module adding persistence copies `Platform/Data/PlatformDbContext.cs`: own schema via `HasDefaultSchema`, explicit snake_case names in `IEntityTypeConfiguration`, `MigrationsHistoryTable("__ef_migrations_history", <schema>)`, and an `IDesignTimeDbContextFactory` because the module is a class library.
- Migrations run at startup via `PlatformDatabaseInitializer` **in Development only** (`Platform:ApplyMigrationsAtStartup` overrides). **WP-5.1 owes a production migration step** — nothing applies migrations outside Development.
- The fast tier runs the real EF model on **SQLite in-memory** (`tests/Platform.UnitTests/Data/PlatformTestDatabase.cs`) — copy it for module DbContexts. Two SQLite gotchas it works around: no `jsonb` (the context relaxes the column type off Npgsql) and **no `ORDER BY` on `DateTimeOffset`** (order by the Guid v7 key instead, which sorts chronologically on both providers).
- New entities use `Guid.CreateVersion7(now)` so the PK index orders chronologically. Entries created in the *same* clock instant have no defined order — a test with a frozen `FakeClock` must advance it between writes.
- **Cross-context atomicity is settled (WP-0.5).** A module registers its context with `services.AddGridCoreDbContext<TContext>((builder, connection) => builder.UseNpgsql(connection, ...))` — never plain `AddDbContext`, which would give it its own connection and its own transaction. A write then wraps itself in `IUnitOfWork.ExecuteAsync(async ct => { ... })` and **never calls `SaveChanges` itself**: the unit of work attaches every registered context to one transaction, saves them all and commits. That is what makes a module write + its audit entry + its outbox row atomic.
- `Platform` exposes internals to `GridCore.Platform.UnitTests` (`InternalsVisibleTo`) for `ScheduledJobRunner.RunOnceAsync`.
- FluentValidation is still **not** referenced. WP-1.1 owns introducing it plus the validator-registration convention CONVENTIONS.md prescribes.
- Recurring work registers with `services.AddScheduledJob<TJob>()` (scoped, resolved per run). Nothing is registered yet — the runner starts with an empty schedule.
- **The bus is live (WP-0.5).** Publish with `IEventPublisher.PublishAsync(SomeEvent.For(...), ct)` **inside** an `IUnitOfWork.ExecuteAsync` — with MassTransit's bus outbox, that writes a row to `platform.outbox_message` rather than reaching RabbitMQ, and the delivery service ships it after the commit. Publishing without then committing publishes nothing, by design.
- Consume by deriving `IdempotentConsumer<TEvent>` and registering it from the module's `AddServices` with `services.AddEventConsumer<TConsumer>()`. `ConsumerName` is the dedupe key in `platform.processed_messages` — **stable forever**; renaming one replays every event it ever handled.
- `AddGridCoreMessaging` is called **after** `AddModules` in `Web.Host/Program.cs` because it reads the consumers back off the service collection. A module registering a consumer after that line would be silently ignored.
- **MassTransit is pinned to 8.5.10 — the last Apache-2.0 line. Never bump to 9.x** (commercial licence). Dependabot (WP-0.7) must be configured to hold it.
- New events go in `src/Contracts/Events/` as positional records implementing `IIntegrationEvent`, with a static `For(...)` that stamps `Guid.CreateVersion7(occurredAt)`. The publisher rejects an event with an empty `EventId`.
- Finance's ledger is still a **no-op**: `IJournalPostingSeam` → `LoggingJournalPostingSeam` logs the balanced entry it would post. WP-2.6 replaces the implementation; the mapping (`FinancePostings`) and the balanced-or-throw guard (`JournalPostingIntent.For`) are already real.
- `FinanceAccounts` account codes (1000/1100/1300/2000/4000) are placeholders until **WP-0.8** ships the chart of accounts by migration.
- The MassTransit 8.x pin is now enforced twice: `Directory.Packages.props` holds it, `.github/dependabot.yml` refuses to offer 9.x, and `DependencyPolicyTests` fails the fast loop if either is edited away.
- `tests/Contracts.UnitTests` exists now and is registered in both `GridCore.slnx` and `tests/UnitTests.slnf`.

## Work packages

### Phase 0 — Foundation
- [x] WP-0.1 Skeleton + docs
- [x] WP-0.2 Aspire + infra
- [x] WP-0.3 Auth + RBAC (8 roles)
- [x] WP-0.4 Audit + approvals
- [x] WP-0.5 Bus + outbox + Finance seam
- [x] WP-0.6 React shell
- [x] WP-0.7 CI + FAST test harness
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
