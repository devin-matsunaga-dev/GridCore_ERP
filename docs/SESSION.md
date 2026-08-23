# SESSION.md — Utility ERP: Claude Code Session Kickoff

**If told to read this file, this is your complete instruction set for the session. Follow it exactly.**

## Step 1 — Load context (in order)
1. `docs/ARCHITECTURE.md` — design + invariants. Binding.
2. `docs/CONVENTIONS.md` — code standards **incl. the ⚡ testing-speed rules**. Binding.
3. `docs/DESIGN.md` — UI system; reference at `docs/design/reference-dashboard.png`. Binding for any frontend WP.
4. `docs/STATUS.md` — current position + in-flight notes.
5. `docs/DECISIONS.md` — settled choices; don't relitigate.

## Step 2 — Identify the work package
- Read **Current WP** from `docs/STATUS.md`; find its full text in `docs/WORK_PACKAGES.md` (your spec).
- Confirm the git branch matches `feat/wp-X.Y-*`; if not, STOP and tell me.
- State back in 3–5 bullets: WP number, scope understanding, anything ambiguous. **Wait for my "go".**

## Step 3 — Implementation rules
1. ONLY this WP's scope. Outside-scope → stop and ask; default "note in STATUS.md In flight, stay in scope."
2. Follow ARCHITECTURE.md, CONVENTIONS.md, DESIGN.md exactly. No new patterns/libraries without asking.
3. **Tests must follow the ⚡ pyramid:** default to fast unit tests (no DB/containers); add integration tests ONLY where DB/bus is the subject, tagged `Category=Integration` for the gate suite. Never make the fast loop slow. Include ≥1 failure-path test.
4. Money is `decimal`; financial postings must balance (assert debits=credits).
5. Never break another module's schema, the outbox, or applied migrations unless the WP says so.
6. External effects go through provider interfaces only — never call a simulator directly from domain code.

## Step 4 — Completion (do all, then STOP)
**Package Completion Report:**
1. **Changes** — files added/modified, one line each.
2. **Tests** — what you wrote, which tier (unit/integration), coverage, pass/fail output. Confirm the fast loop stayed fast (report its runtime).
3. **Manual verification** — numbered steps: exact URLs/commands, expected results, ≥1 failure-path (e.g. "adjust a bill without permission → 403").
4. **Regression command** — the FAST per-package command from CONVENTIONS.md (unit only, parallel, `--no-build`).
5. **Git suggestion** — conventional commit referencing the WP.

Then: append decisions to `docs/DECISIONS.md` (or "none"); update `docs/STATUS.md` (check WP, set next, note in-flight). Then **STOP** — do not start the next WP.

## If I report a failed check
Fix only what's needed, re-run affected tests, issue a deltas-only report. No unrelated refactors.
