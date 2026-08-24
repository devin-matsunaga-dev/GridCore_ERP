# CONVENTIONS.md — Utility ERP Code Standards

> Generated code must look like the rest of the repo. When unsure, copy the nearest existing example.

## Solution layout
```
src/
  AppHost/                       # Aspire orchestration
  ServiceDefaults/               # Aspire shared defaults (telemetry, health, resilience)
  Web.Host/                      # ASP.NET Core host (thin endpoints)
  Modules/<Name>/
    Features/<Feature>/          # vertical slice: endpoint, service, models
    Data/                        # DbContext, configs, migrations
  Platform/                      # auth, audit, approvals, events, seed
  Contracts/                     # events, shared DTOs, provider interfaces
tests/
  UnitTests.slnf                 # solution filter: the fast tier only
  <Module>.UnitTests/            # FAST — no infra, run every WP
  IntegrationTests/              # SLOW — Testcontainers, run at gates/on-demand
  Web.ComponentTests/            # React (Vitest) — fast
web/                             # React app
docs/
```

## .NET
- Target **net10.0**, `LangVersion` latest. Nullable on, warnings as errors, file-scoped namespaces. Never scaffold net8/net9 (EOL Nov 2026).
- REST, plural nouns, kebab-case routes; non-CRUD actions as POST sub-resources (`/api/work-orders/{id}/assign`).
- Errors: RFC 7807 ProblemDetails. 400 validation / 401-403 auth / 404 / 409 workflow conflict.
- DTOs are records; never expose EF entities. Validation via FluentValidation at the edge.
- IDs `Guid` v7. **Money `decimal`** (never double/float); centralize rounding in one helper.
- EF: `IEntityTypeConfiguration` classes, snake_case tables, migrations `WPxy_Desc`. Register a module context with `AddGridCoreDbContext<T>` (never plain `AddDbContext`) and wrap every write in `IUnitOfWork.ExecuteAsync` — a context on its own connection cannot share a transaction with the audit trail or the outbox.

## Events / Finance
- Events past-tense in `Contracts` (`BillIssued`, `PaymentApproved`, `GoodsReceived`). Records with EventId, OccurredAt, ids + facts, built by a static `For(...)` that stamps a Guid v7 EventId from OccurredAt. Publish with `IEventPublisher`; consume by deriving `IdempotentConsumer<TEvent>` and giving it a stable `ConsumerName` (never renamed — the name is the dedupe key).
- Journal posting lives only in Finance, triggered by events. Every posting balanced; assert it in code (throw if debits≠credits).

## React
- TS strict, function components + hooks. TanStack Query for server state (keys `['work-orders', filters]`); one typed API client per module; components never call fetch directly. react-hook-form + zod. Errors via shared toast.

---

# ⚡ TESTING — SPEED IS A FIRST-CLASS REQUIREMENT

> The prior project's suite took *hours*. That is a bug, not a cost of doing business. The rules below keep the per-package loop under ~60 seconds and the full suite under a few minutes. Follow them exactly.

## The test pyramid (target ratio ~85/13/2)
1. **Unit tests (the vast majority):** pure domain logic — rate engine, consumption calc, double-entry balancing, state-machine transitions, provider simulators. **No database, no containers, no bus.** Milliseconds each. These run on EVERY work package.
2. **Integration tests (few):** only where DB/bus behavior is the thing under test (outbox delivery, a full repository query, one slice per module). Run at phase gates or on demand — NOT every package.
3. **E2E workflow tests (tiny handful):** the two demonstration workflows, end to end. Gate-only.

If a behavior can be tested without infrastructure, it MUST be — pushing logic into pure, injectable units is both better design and faster tests.

## Hard rules that keep it fast

**A. Never force single-core.** The regression command must run tests in **parallel**. Do NOT use `--maxcpucount:1` (this alone made the old suite crawl). xUnit runs test classes in parallel by default — keep it that way; only opt specific integration collections out of parallelism. xUnit's default only parallelises *within* an assembly, though: VSTest runs test assemblies one after another unless told otherwise, so the commands below pass `-- RunConfiguration.MaxCpuCount=0` (0 = one worker per core). `tests/Build.UnitTests` fails the loop if that flag is ever dropped from CI or replaced with a 1.

**B. Don't rebuild on every test run.** Build once, then test with `--no-build --no-restore` in the loop. The regression command below does this.

**C. Unit tests use in-memory data, not containers.** Repository/domain logic tests use SQLite in-memory or EF InMemory or plain fakes — never spin a Postgres container to test a calculation.

**D. ONE shared Postgres container for ALL integration tests, reset between tests with Respawn.** Never one container per test class (that was the hours-long killer). Use a single xUnit **collection fixture**: container starts once for the whole integration run; each test wipes tables with Respawn (millisecond truncate) instead of recreating the DB or container. Apply migrations once at fixture startup.

**E. Split test projects so the loop runs only the fast ones.** `*.UnitTests` are the per-WP regression. `IntegrationTests` + E2E run at gates. Tag integration tests `[Trait("Category","Integration")]` so they can be filtered out.

**F. React tests use Vitest** (fast, Vite-native) — never Jest with heavy transforms. Component tests via Testing Library; no full-browser E2E in the loop.

**G. Keep tests independent and parallel-safe:** no shared mutable static state, no ordering dependencies, unique data per test (Guids), no `Thread.Sleep` — await real signals.

## The two regression commands

**Per-package loop (fast — this is what SESSION.md's report uses, runs in seconds):**
```bash
dotnet build -c Debug && \
dotnet test tests/UnitTests.slnf -c Debug --no-build --filter "Category!=Integration" -- RunConfiguration.MaxCpuCount=0 && \
npm --prefix web run test -- --run
```

**Phase-gate (full — unit + integration + E2E):**
```bash
dotnet build -c Debug && \
dotnet test GridCore.slnx -c Debug --no-build -- RunConfiguration.MaxCpuCount=0 && \
npm --prefix web run test -- --run
```
`tests/UnitTests.slnf` is a solution filter listing only the `*.UnitTests` projects — `dotnet test` takes exactly one project/solution argument, so a `tests/*UnitTests` glob does not work. Add every new unit-test project to that filter. The `npm` line applies once `web/` exists (WP-0.6).

(No `--maxcpucount:1`. No `-c Release` for routine testing — Release builds are slower to produce and only needed when publishing images.)

## Every WP adds
At least one failure-path unit test. New cross-module effect → one integration test (gate suite). Touches a demonstration workflow → extend its E2E test. If a WP's tests push the fast loop over ~90s, something belongs in the integration tier — move it.

## Git
Branch `feat/wp-X.Y-name`; squash-merge to main; conventional commits `feat(billing): tiered rate engine (WP-3.1)`. Phase tags `vX.Y-phaseN`.

## Docs duties each session
New decisions → `docs/DECISIONS.md`. WP done → `docs/STATUS.md`. Architecture change → `docs/ARCHITECTURE.md` same WP.
