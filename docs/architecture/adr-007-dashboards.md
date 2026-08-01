# ADR-007: Query builder & custom dashboards

Status: accepted (2026-08-01). Scope: phase 7 — features F6 (composable query
UI) and F8 (custom dashboards).

## Design

- **A panel is a stored QuerySpec plus presentation** (`PanelDef`): source,
  scope mode (selected fights / all fights / trailing-N-seconds), trim,
  up to two grouping dimensions, metric columns or a primary metric, validity
  exclusions, player/ability filters, and — for line panels — bucket width and
  rolling-window smoothing. `buildSpec` binds the stored definition to the live
  context (current fight selection, global pet-rollup) at render time, so a
  panel saved as "selected fights" rescopes with every click and never stores
  stale fight ids.
- **Four generic renderers** (table with drill-down, smoothed line, ranked
  horizontal bar, stat tile) consume any spec — the canned Overview panels and
  custom panels share the query engine, formats, palette, and mark specs. The
  "edit as panel" button on the summary proves the F4 acceptance criterion:
  the classic views are presets of the same form, not bespoke code paths.
- **Grid**: react-grid-layout v2 (MIT), 12 columns, drag by title bar, resize
  from the corner. Layout rects live beside the panels in the dashboard
  document.
- **Persistence = verbatim JSON documents.** The server's `DocumentStore`
  keeps one file per well-known key (`dashboards`, `saved-queries`,
  `ui-settings`) under `%AppData%\EQDeeps`, written atomically (temp+move);
  `GET/PUT /api/store/{key}` stores what the client sends. Export/import is
  therefore literally the persisted shape — a dashboard downloaded on one
  machine imports on another unchanged (F8 AC). Corrupt files read as absent
  instead of wedging startup.
- The client saves with an 800 ms debounce on any dashboard change (layout
  drags included), and loads once at startup.

## Rejected alternatives

- Server-side dashboard schema/validation — the client owns the shape; the
  server storing documents verbatim keeps export/import trivial and avoids
  double-maintaining a schema. Revisit if a non-UI consumer appears.
- CSS-grid hand-rolled drag — react-grid-layout is MIT, tiny, and battle-tested;
  the architecture doc explicitly sanctioned it.

## Verification

`DocumentStoreTests` cover round-trip, overwrite, corrupt-file recovery, and
key traversal rejection. UI: TypeScript-strict build; builder → panel → drag/
resize → persistence exercised against the live backend during development.
