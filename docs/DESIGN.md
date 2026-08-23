# DESIGN.md — Utility ERP UI

> Visual law for every screen. Canonical reference: `docs/design/reference-dashboard.png`. Every page must look like it belongs in that screenshot.

## Direction

Clean, light, enterprise operations console. White canvas, generous whitespace, one green accent, restrained semantic color for status. Dense data presented calmly. Left dark-green sidebar, sectioned nav (Operations / Enterprise / Reports).

## Stack
Tailwind CSS + shadcn/ui; lucide-react icons (20px, stroke 1.75); Recharts; Inter font.

## Tokens

| Token | Hex | Use |
|---|---|---|
| primary | #1F7A4D | Buttons, active nav, links, chart line, focus |
| primary-soft | #E7F2EC | Active nav bg, tinted icon circles |
| sidebar-bg | #14231C | Sidebar (deep green-black), full height |
| sidebar-text | #9DB0A6 | Inactive nav |
| sidebar-section | #6E8478 | Nav section labels (uppercase, small) |
| canvas | #F7F8F7 | Page background |
| card | #FFFFFF | Cards |
| border | #E8EBE9 | Card borders, dividers, table rules |
| text-heading | #1A211E | Titles, KPI values |
| text-body | #4A534E | Body |
| text-muted | #8A938D | Labels, table headers, timestamps |

**Semantic (status):** Online/Completed/Paid/success #22A06B (soft #E3F5EC) · Warning/Scheduled/Medium/overdue-soon #E8A33D (soft #FBF0DD) · Outage/Overdue/High/Declined #E5484D (soft #FCE8E8) · In Progress/Info #3B7DD8 (soft #E4EEFB) · On Hold/Neutral/Closed #8A938D (soft #F0F2F1).
Chart categorical order: #1F7A4D → #3B7DD8 → #E8A33D → #22A06B → #C4CCC7.

## Type
Page greeting/title 26/700 · card title 16/600 · KPI value 30/700 · body 14/400-500 · labels & table headers 13/500 muted · pills 12/500. Sentence case. `tabular-nums` for money/counts. Money shown with thousands separators and currency prefix.

## Layout
- **Shell:** fixed dark-green sidebar ~248px, logo top, nav grouped under muted section headers (Operations, Enterprise, Reports), user card pinned bottom. Content scrolls.
- **Topbar (in content):** greeting + subline left; global search (⌘K) center-right; notifications bell w/ count, help, org switcher right.
- **KPI row:** 5 equal stat cards — tinted icon circle, label, big value, delta line (arrow + % + "vs <period>"), delta color by sentiment.
- **Grid:** 24px padding + gaps. Rows mix thirds/halves (System Overview 1/3, Work Orders donut 1/3, Alerts 1/3; then feed 1/3, chart 1/3, quick actions 1/3).
- **Cards:** white, 1px border, 14px radius, subtle shadow, 20-24px padding; header = title + optional subtitle left, action link/select right.
- **Density:** compact tables (44-48px rows), muted 13px headers, no zebra, row-hover canvas tint, status as pills inline, right-aligned numerics, ID column muted.

## Components
- **Buttons:** primary solid green; secondary white+border; destructive red; icon-ghost. 36-40px, 10px radius.
- **Status pills:** soft bg + semantic text, 6px radius, no border.
- **Status dots:** filled circle + label (as in System Overview legend / alerts).
- **KPI card / delta:** sentiment coloring (cost down = green, assets up = green).
- **Alerts list:** icon in soft severity circle + title + subtext + relative time; "View all" in header.
- **Quick actions:** grid of square tiles, icon + 2-line label, border, hover lift.
- **Donut:** center total + label, legend right with counts + %.
- **Line/area chart:** 2px primary line, faint gradient fill, dashed gridlines, muted axis labels, month ticks.
- **Forms:** shadcn inputs, 8px radius, green focus ring, labels above; errors red 12px below.
- **Workflow status:** the many state machines (service account, bill, work order, PO) render as pills using the semantic map; show allowed transitions as buttons, disable illegal ones.
- **Empty/loading:** friendly empty states with icon+action; skeleton shimmer, never spinners in cards.

## Dark mode
Invert neutrals (canvas #10161300… slate-green dark, cards #1A2420, borders #2A352F, heading #EEF2F0, body #A9B3AD); sidebar already dark; semantic hues hold, soft pills → 15% alpha. Charts gridlines dim.

## Per-area
- **Dashboards** (Home + module dashboards): the reference look.
- **Registries** (customers, meters, assets, inventory, work orders): dense filterable tables + detail drawers/pages; 360° detail pages (e.g. customer → service accounts → meters → bills → payments).
- **Finance:** journal/trial-balance as tight numeric tables, debits/credits aligned, totals bold.
- **Workflow screens:** wizard-style for the two demonstration cycles so a demo flows top-to-bottom.

## Quality floor
Responsive to 1280px (tables scroll below), visible green focus ring, WCAG AA contrast, skeleton loading, reduced-motion respected, all money/dates formatted centrally, timestamps user-local.
