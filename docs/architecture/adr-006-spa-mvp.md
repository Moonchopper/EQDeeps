# ADR-006: SPA MVP

Status: accepted (2026-08-01). Scope: phase 6 — the React frontend and the
default dashboard (features F1–F5, F7; the query-builder UI is phase 7).

## Decisions

- **Stack**: React 18 + TypeScript via Vite; ECharts (Apache-2.0) for the DPS
  chart; `@microsoft/signalr` (MIT) for the live channel. No component library —
  hand-rolled CSS keeps the dependency surface at four runtime packages, all
  MIT/Apache-2.0.
- **Build integration**: `ui/` builds straight into `src/EQDeeps.Server/wwwroot`
  (`npm run build`), which the backend serves with an SPA fallback when present.
  Built assets and `node_modules` are not committed; dev mode proxies `/api` +
  `/hubs` (websockets) to the backend on 5487. Packaging (phase 8) embeds
  wwwroot into the published exe.
- **Dark-first, single theme for the MVP.** Raiders run dark UIs at night.
  *(Superseded by ADR-015. The surfaces are now `#0f0d0b`/`#26211c` and the
  series palette has been re-derived; the eight chart slots are validated on
  ALL pairs rather than adjacent ones, which the original set failed at ΔE 1.6
  under deuteranopia. The sentence that used to end this bullet — "a light
  theme is a later variable swap, not a rework" — was **wrong**, and stayed
  wrong for long enough to be worth naming: 74 colour literals live in
  `.ts`/`.tsx` where no CSS variable reaches, seven surfaces were additive
  white, and the categorical palette needs re-derivation rather than inversion.
  The app is dark-only by decision now, not by staging.)*
- **Chart discipline** (dataviz method): line chart, one axis, per-second
  landed totals; top-8 players by total with the rest folded into a dashed
  gray "Other" — never a ninth generated hue; colors assigned per entity for
  the life of a selection (live ticks don't reshuffle); lines break with null
  points across dead time instead of drawing over gaps; crosshair tooltip;
  legend always present; text wears ink tokens, series color lives in marks.
- **Selection model**: fight list selection (click / ctrl-click / pull-chain
  header) scopes every panel; "follow live" tracks the active fight from meter
  ticks and any manual selection turns it off. Summaries refetch on selection
  change and on hub events, throttled to 1 Hz — the backend cache makes
  repeated specs cheap.
- **Summaries are canned specs** executed through `POST /query` — the same
  JSON a power user will edit in the phase-7 builder; the exclude-damage-shield
  toggle on the damage tab is literally a `QueryFilter` on the spec (F6 AC).

## Verification

TypeScript-strict build is clean. Full-stack smoke on a 2 MB synthetic log:
SPA served from the backend, 21,870 records backfilled with 0 unrecognized
lines, 26 fights with pull-chain groups, pet-rollup rows ("Raider05 +Pets")
via the query endpoint, and a scripted append surfacing a new active fight
through the API within the latency budget. Interactive polish (drag/resize
dashboards, custom panels) is phase 7; real-log visual review is the owner's
release-gate pass.
