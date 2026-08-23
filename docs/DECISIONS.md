# DECISIONS.md — Utility ERP

> One line per settled choice. Appended by Claude each session; not relitigated without a WP.

- Bootstrap: modular monolith (ASP.NET Core + Aspire), schema-per-module, events for cross-module effects
- Bootstrap: no Python — this project has no polling/monitoring need (unlike ITMS)
- Bootstrap: external systems simulated behind provider interfaces (IMeterReadingProvider, IPaymentProvider, IVendorProvider, ICrewProvider) from day one
- Bootstrap: Finance is downstream-only — consumes events, posts balanced journal entries, never calls back
- Bootstrap: money is decimal; ledger append-only; corrections are new entries
- Bootstrap: TEST SPEED is a first-class requirement — fast unit pyramid, shared Testcontainer+Respawn for integration, parallel, never --maxcpucount:1 (fixes ITMS slowness)
- Bootstrap: UI = Tailwind + shadcn + lucide per DESIGN.md reference dashboard
- WP-0.1: assembly/namespace prefix is `GridCore` (`GridCore.Modules.<Name>`, `GridCore.Platform`, …); solution file is `GridCore.slnx` (.NET 10 XML format)
- WP-0.1: central package management (`Directory.Packages.props`) + shared `Directory.Build.props` — one place for net10.0, nullable, warnings-as-errors and the xUnit stack; test projects are identified by an `*Tests` name suffix because `IsTestProject` is set too late for a props-file condition
- WP-0.1: modules compose via a `Platform.Modules.IModule` seam (`Name`/`AddServices`/`MapEndpoints`), listed explicitly in `Web.Host/Program.cs` rather than assembly-scanned
- WP-0.1: Aspire `ServiceDefaults` project added alongside `AppHost` (standard Aspire pair; consumed by Web.Host from WP-0.2)
- WP-0.1: fast loop runs `tests/UnitTests.slnf` — `dotnet test` accepts only one project/solution argument, so the documented `tests/*UnitTests` glob was corrected in CONVENTIONS.md
- WP-0.1: no assertion library beyond xUnit's `Assert` (FluentAssertions deliberately not added — 8.x is commercially licensed)
- WP-0.2: `aspire.config.json` lives at the repository root (pointing at `src/AppHost`) so `aspire run` resolves the AppHost from anywhere in a fresh clone
- WP-0.2: AppHost composition lives in `InfrastructureComposition`/`WebComposition` extension methods rather than inline in `AppHost.cs`, so the application model is unit-testable in the fast tier without Docker (`tests/AppHost.UnitTests`)
- WP-0.2: MinIO is composed as a pinned plain `AddContainer` (`minio/minio:RELEASE.2025-09-07T16-13-09Z`) — there is no first-party Aspire integration and the community-toolkit package ships pre-release only; never `latest`, which would change the object store under a demo
- WP-0.2: Keycloak uses the official `Aspire.Hosting.Keycloak` 13.5.2-preview — preview is the only channel Microsoft ships it on; its version tracks the rest of Aspire
- WP-0.2: `AddNpmApp` no longer exists in Aspire 13 (`Aspire.Hosting.NodeJs` → `Aspire.Hosting.JavaScript`); the WP-0.2 spec's "web dev server via AddNpmApp" is implemented as `AddViteApp(...).WithNpm()`
- WP-0.2: the React dev-server resource is skipped when `web/` is absent, so `aspire run` stays green until WP-0.6 creates it
- WP-0.2: infra credentials come from named `Parameters:` in `src/AppHost/appsettings.Development.json` (checked in, dev-only) because Postgres/RabbitMQ/Keycloak/MinIO persist their users into their data volumes — a per-run generated password would lock the AppHost out of the volume it created last run. Production supplies the same parameter names from the environment; Redis keeps Aspire's generated password since no user is persisted in its volume
- WP-0.2: `/health` aggregation comes from the Aspire client integrations registered in `Web.Host` (Aspire.Npgsql, Aspire.StackExchange.Redis, Aspire.RabbitMQ.Client); consequence — `Web.Host` no longer starts standalone, it requires the AppHost-supplied connection strings
- WP-0.3: authentication is plain `AddJwtBearer` bound to an `Authentication:*` section (Authority/Audience/RequireHttpsMetadata/RolesClaimPath/NameClaimType), NOT the Keycloak-specific Aspire client integration — swapping OIDC provider is then a configuration change with no code change, as the WP requires
- WP-0.3: endpoints are gated on **permissions**, never on role names; `RolePermissionMap` is the single place roles become permissions, so re-cutting a role touches one file. `Permissions.All` is discovered by reflection over the constants so a new permission can never be missing from the Administrator grant
- WP-0.3: permission policies are built on demand by `PermissionPolicyProvider` for any `perm:<permission>` policy name (`.RequirePermission(Permissions.Billing.Adjust)`), rather than registering a named policy per permission at startup
- WP-0.3: the host is **secure by default** — a fallback policy requires an authenticated caller, so a new module endpoint cannot ship accidentally public; `/health` and `/alive` in ServiceDefaults opt out with `AllowAnonymous` because Aspire's probes carry no token
- WP-0.3: role claims are normalised to `ClaimTypes.Role` by `GridCoreClaimsTransformation` reading a configured dotted path (`realm_access.roles` for Keycloak, a flat `roles` claim for other providers); an unreadable claim yields no roles rather than an exception, so a token GridCore cannot parse means no access, never a 500
- WP-0.3: roles the realm does not define are ignored rather than rejected — the IdP may carry roles for other systems (`offline_access`, `default-roles-*`) and an unknown role must never widen access
- WP-0.3: the Keycloak realm is checked in as `src/AppHost/keycloak/realms/gridcore-realm.json` and imported **in Development only**, because it carries eight test users with a well-known password; a production realm (same roles and clients, real users) is WP-5.1's to supply
- WP-0.3: `gridcore-web` is a public PKCE client whose audience mapper targets `gridcore-api`; direct access grants stay enabled in the dev realm so the RBAC checks can fetch a token with curl
- WP-0.3: `Web.Host` registers `AddProblemDetails()` + `UseStatusCodePages()` so the framework's own 401/403 carry RFC 7807 bodies, per CONVENTIONS.md
