# DECISIONS.md — Utility ERP

> One line per settled choice. Appended by Claude each session; not relitigated without a WP.

- Bootstrap: modular monolith (ASP.NET Core + Aspire), schema-per-module, events for cross-module effects
- Bootstrap: no Python — this project has no polling/monitoring need (unlike ITMS)
- Bootstrap: external systems simulated behind provider interfaces (IMeterReadingProvider, IPaymentProvider, IVendorProvider, ICrewProvider) from day one
- Bootstrap: Finance is downstream-only — consumes events, posts balanced journal entries, never calls back
- Bootstrap: money is decimal; ledger append-only; corrections are new entries
- Bootstrap: TEST SPEED is a first-class requirement — fast unit pyramid, shared Testcontainer+Respawn for integration, parallel, never --maxcpucount:1 (fixes ITMS slowness)
- Bootstrap: UI = Tailwind + shadcn + lucide per DESIGN.md reference dashboard
