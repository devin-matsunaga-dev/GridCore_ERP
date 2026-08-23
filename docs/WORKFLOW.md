# WORKFLOW.md — Utility ERP (Human Side)

Kickoff every session: **"Read docs/SESSION.md and proceed."** Everything else lives in the repo.

## One-time setup
Same as any of my projects — WSL2 + Ubuntu LTS, Docker Desktop (WSL integration), VS Code + WSL ext, `.wslconfig` memory bump. Inside WSL: git, **.NET 10 SDK**, **Aspire CLI**, **Node 24 (nvm)**, Claude Code (`npm i -g @anthropic-ai/claude-code`) + auth. Repo lives in WSL fs (`~/projects/...`), never `/mnt/c/`. Add root `CLAUDE.md`:
```
# Agent Instructions
On every session start: read docs/SESSION.md and follow it exactly.
```
Put all docs in `docs/`, screenshot in `docs/design/reference-dashboard.png`, `touch docs/DECISIONS.md` (already provided), commit, push.

## Per work package
1. `docs/STATUS.md` → Current WP + branch.
2. `git checkout main && git pull && git checkout -b feat/wp-X.Y-name`
3. `claude` → "Read docs/SESSION.md and proceed." → read scope summary → "go".
4. Build → Completion Report → updates STATUS/DECISIONS → stops.
5. **Verify:** run the FAST regression (below) → `aspire run`, walk the manual checklist in browser → `git diff main` (line-by-line for [SENSITIVE] money/auth WPs).
6. Fail? same session: "Step N failed: … Fix." (Confused? delete branch, restart WP.)
7. Merge:
   ```
   git add -A && git commit -m "feat(x): thing (WP-X.Y)"
   git checkout main && git merge --squash feat/wp-X.Y-name
   git commit -m "feat(x): thing (WP-X.Y)" && git push
   git branch -D feat/wp-X.Y-name
   ```
8. Phase gate? Run the FULL suite + gate check, `aspire update`, then `git tag vX.Y-phaseN && git push --tags`. Close session.

## The two regression commands (see CONVENTIONS.md ⚡)
**Per package (FAST — seconds):**
```
dotnet build -c Debug && \
dotnet test tests/*UnitTests -c Debug --no-build --filter "Category!=Integration" && \
npm --prefix web run test -- --run
```
**Phase gate (FULL — minutes):**
```
dotnet build -c Debug && dotnet test -c Debug --no-build && npm --prefix web run test -- --run
```

## Why the old project was slow (don't repeat)
`--maxcpucount:1` forced single-core; per-class Testcontainers rebuilt DBs constantly; Release builds every run. Fixed here: parallel by default, one shared container + Respawn reset, `--no-build`, unit tier for the loop, integration/E2E only at gates.

## Non-negotiables
Repo in WSL fs · main always green · fast loop stays under ~60s · [SENSITIVE] money/auth WPs get line-by-line review · one WP = one squash commit · LTS/latest versions only.
