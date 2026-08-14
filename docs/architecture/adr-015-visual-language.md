# ADR-015: A rounded, dark visual language, and the limits that shape it

Status: accepted (2026-08-13). Scope: the whole SPA surface. Supersedes the
palette and theme parts of ADR-006; leaves its structural decisions standing.

## Context

The app read as a default dark dashboard: near-black ground, one blue accent,
`system-ui`, and a 6–8px radius on everything. None of that was wrong, exactly —
it was undecided. The neutral ramp was already warm-tinted (`#1a1a19`,
`#232322`, `#c3c2b7` are all R=G>B, a real choice) while `--page: #0d0d0d` and
`--ink: #ffffff` were pure achromatic, so the mid-ramp had been authored and
both endpoints inherited. `--border` was `rgba(255,255,255,0.1)`, the canonical
framework divider. `--accent` was 3.4 ΔE from Tailwind `blue-500`.

Two defects were found while measuring rather than while looking:

- **`--accent` and `SERIES_COLORS[0]` were the same hex.** "Selected" and
  "series one" were one signal.
- **No chart set a root `textStyle`.** Every axis label, legend, tooltip and bar
  label rendered in ECharts' stock `sans-serif` while the DOM rendered
  `system-ui` — two typefaces on every panel, in every shipped build.

The owner supplied the direction: rounded and flowy, soft surfaces, curves
rather than polylines. Dark only; light mode explicitly not wanted.

## Decision 1: chrome recedes, content comes forward

`--page` (`#0f0d0b`) carries the nav rail, session bar, fight list and panel
gutter. `--surface` (`#26211c`) carries panels. The step measures **1.216:1**.

That number looks weak and is close to the ceiling. The `+0.05` term in the WCAG
formula caps a dark-on-dark ratio, and against pure black this surface tops out
at 1.317:1. Anyone "fixing" the low number by lightening `--surface` breaks
`SERIES_COLORS` first, because those are 3:1 marks with no margin. The token
carries that warning inline.

Elevation is a lightness step plus a 1px rim-light on the top edge. Shadows do
not read on a dark ground; the one real shadow is reserved for things that float
over the page — menus, modals, chart tooltips.

## Decision 2: three opaque rule tiers, not one alpha

`--grid` 1.30, `--border` 1.70, `--baseline` 2.29 against the panel. A panel
edge, a table row rule and an input border previously carried identical weight,
so nothing read as a region boundary. Opaque also matters beyond hierarchy:
additive white inverts wrong, and a light theme cannot be built on it.

The strongest separator — a lit line over a dark one — is spent in exactly two
places, under the session bar and under the sticky table header, because those
are the two places a region genuinely ends.

## Decision 3: eight chart colours, sixteen row tints, two different gates

A chart draws its series *simultaneously*, so the right question for the chart
set is every pair. Validated that way, the previous first eight collapsed:
`#d55181` against `#199e70` measured ΔE 1.6 under deuteranopia and 7.1 under
ordinary vision. The palette's own comment said it had been validated — against
adjacency, which is the right gate for a stacked bar and the wrong one here.

- **Slots 1–8** clear **all pairs**: ΔE 8.2 protan, 15.3 normal-vision, every
  slot ≥3:1 on both `--surface` and `--surface-2`.
- **Slots 1–16** keep the adjacent gate: ΔE 9.5 deutan, 17.4 normal-vision.

**All sixteen do not clear all-pairs, and no sixteen-colour set does.** Searching
9,443 candidates inside the OKLCH dark band with a 3:1 floor, the best achievable
worst pair scores 0.68 against the 1.0 pass line at sixteen slots, 0.80 at
twelve, 0.94 at ten, and only clears at eight — and that one is close to neon.
This is arithmetic, not a shortfall of care, and it is the reason slots 9–16 are
reached only by table rows where the entity's name sits beside the chip, and why
charts fold everything past the eighth into "Other".

Tier two is the same eight hue families at a different lightness, because CVD
flattens hue and preserves lightness. It steps *up* for orange, olive and blue:
the panel is lighter than it was and orange has no darker step left that clears
3:1. The gap does the work, not its direction.

A series colour is a 3:1 **mark**. It is never text; `chartTheme.ts` paints every
legend and axis label from `--ink-2`.

## Decision 4: one bundled typeface, and weight as the hierarchy channel

IBM Plex Sans, variable, Latin subset, 45.7 KB, self-hosted (SIL OFL 1.1, see
NOTICE). Bundled rather than linked because the app is a localhost exe with no
network — a webfont URL would fall back to Segoe UI on every launch and nobody
would notice.

One variable file instead of four statics pays for the ladder 400 / 450 / 520 /
600 / 700. The 450 and 520 are the point: **hierarchy in a dense table is
carried by weight, never by brightness.** Promoting a row costs no contrast,
where brightening it spends the little a dark ground has. Row height went from
30.0px to 29.0px, so this cost no density.

## Decision 5: the EverQuest content, and what was refused

Researched from the installed client rather than from memory. Three findings
changed the design:

- The logo is **not blackletter** — it is Letraset-era Tarragon, Art Nouveau. A
  blackletter wordmark would read as a metal band.
- **There is no EverQuest typeface to honour.** `defaults.ini` has one font
  line, `Font.us.0=Arial`, and no font files ship. Players install mods to
  escape it. The UI face is a free choice.
- **EQ has no item rarity system.** Every item link is the same magenta. The
  Loot view inherits nothing.

What was taken: the **relevance triad**. The client encodes relevance as
brightness — what you do is white, what happens to you is red, what two other
people do to each other is grey. It runs on a channel the entity palette does not
use, so it costs no hue budget and no CVD budget, and it answers a question
every table already had. Here it is spent as weight rather than brightness, for
the reason in Decision 4.

What was refused: stone, parchment, bevels, filigree, blackletter, and a
`SERIES_TEXT` array. That last one was derived and then dropped — forcing each
hue to 4.5:1 against `--selected` while holding the hue collapses most of the
sixteen into muddy near-greys that are harder to tell apart than the marks they
came from.

Also refused: the **con-colour level chip**, which is the best flavour idea in
the whole set and is blocked. `docs/domain/eq-legends-loadouts.md` says outright
that a character has no single current level, so a con computed against "the
character's level" would repaint the same mob green, yellow and red depending on
which loadout was active. It needs a DTO change and its own ADR.

## Decision 6: measure the dense surfaces

`ui/scripts/layout-check.mjs`, wired into CI. Three layout faults had shipped
undetected — a 17px panel on Incoming, the same shape at 4px on Mobs, and the
tier ladder's numeric columns walking 23px sideways — because the UI had no
tests and nothing had ever measured the rendered output.

Geometry assertions, not a pixel diff. A pixel diff would have caught all three
and then failed on every deliberate colour change for the rest of the project,
which is how baselines end up regenerated without being read. The checks encode
invariants: a panel is never shorter than its own title bar, a column of numbers
shares a right edge, a sticky header is opaque, every focusable thing shows a
focus ring, the monitored character's row is heavier than everyone else's.

**Every check is confirmed red before it is trusted** — `--css` points the
harness at another revision for exactly that. This is not ceremony. Three checks
passed over broken output during this work, each by asserting the easy adjacent
property instead of the one carrying the meaning: fill width where paint was
what mattered, font-family availability where loading was what mattered,
`outline-width` where `outline-style` was what mattered.

## Consequences

- ADR-006's "a light theme is a later variable swap, not a rework" is **false**
  and is amended there. 74 colour literals live in `.ts`/`.tsx` where no CSS
  variable reaches, seven surfaces were additive white, and the series palette
  needs re-derivation rather than inversion. The token work paid most of that
  bill, but the app is dark-only by decision now, not by staging.
- `meterStyle` no longer returns a finished background. It hands over custom
  properties and a pseudo-element draws the fill, because a `<tr>` background
  has no box to round. This also retires a real hazard: the old contract built
  its colour by string-concatenating hex and alpha, so anything but 6-digit hex
  produced an invalid gradient stop, which resolves to `none`, which meant every
  meter bar in every table vanished with no error.
- `rowHeight: 36` is untouched and must stay so. It is persisted user data in
  every `dashboards.json` plus ~30 hardcoded `h` values in `standardViews.ts`;
  changing it is a store migration, not a restyle.
- Class names were not renamed. Consolidating eleven button treatments and eight
  field treatments was done by settling their *treatment* in one block at the end
  of the stylesheet, not by renaming `.mini-btn` across twelve components.

## Decision 7: two densities, comfortable by default

One `--row-pad-y` token drives table rows, sticky headers and live-meter rows
together, switched by `data-density` on the root: comfortable 31px rows,
compact 27px. It also retires a hardcoded `height: 24px` on `.meter-row`, one
of the font-metric couplings the audit flagged as clipping the moment the base
size moves.

Comfortable is the default and compact is the opt-in, which is the way round it
has to be. This audience is 35-55, plays at night, and the recurring thread on
the EverQuest interface forums is literally "font and everything too small to
read". Four visible rows is the right price for legibility, and anyone who
wants them back says so once.

The harness holds each mode to its own ceiling — 32px comfortable, 28px compact
— rather than letting the tighter one inherit the looser budget.

## Still open

The `LORE ITEM`-style property tags, the ~30 Unicode glyph icons that still fall
through to Segoe UI Symbol and will look increasingly out of place beside Plex,
and the con chip above.

None of it has been seen in the running app on a real log. The ground split, the
pill meters, the amber accent and the spline curvature are all things that would
be obviously wrong within seconds of looking, and everything to date has been
verified against fixtures and a headless browser.
