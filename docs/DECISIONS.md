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
