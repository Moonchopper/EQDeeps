## Conventions

**Dark-only, by decision, not by staging.** There is no light theme and none
is planned — every token is designed against a dark page/panel ground. Don't
invert colors or add a light variant when building with this kit.

**Wrap the page ground once, at the root.** Import the kit's stylesheet
(`eqdeeps-ui-kit/styles.css`) and apply the `eqd-page` class to whatever
element is the actual page — not to every component. It sets the dark
background, the base ink color, and the UI font (IBM Plex Sans, self-hosted,
embedded in the stylesheet). Deliberately not a bare `body` selector, so
importing the stylesheet never repaints an unrelated host page.

**Chrome recedes, content comes forward.** `--page` is darker than
`--surface`; the page ground carries navigation and gutters, panels carry
content. Elevation is a lightness step plus a 1px rim-light on the top edge —
never a drop shadow. The one real shadow (`--shadow-overlay`) is reserved for
things that float over the page: menus, modals, tooltips.

**Three opaque rule tiers, never a single alpha border.** `--grid` (cell
rules, chart split lines) < `--border` (panel and control edges) <
`--baseline` (region boundaries — used sparingly, for places a region
genuinely ends, like under a sticky header). Don't introduce a fourth
weight or fall back to `rgba(255,255,255,…)`.

**Hierarchy is carried by font weight, not brightness.** The weight ladder
(`--w-normal` 400 through `--w-heavy` 700) is the tool for promoting one row
or label over another in a dense table or list. Brightening text spends
scarce contrast budget on a dark ground; a weight step doesn't.

**Radius is keyed to element size**, not one value everywhere: `--r-tiny`
(chips), `--r-chip` (badges, mini-buttons), `--r-control` (inputs, buttons),
`--r-inner` (nested cards), `--r-card` (panels, 16px — not the 8px framework
default), `--r-modal` (20px), `--r-pill` (999px, fully round).

**Charts stop at 8 series.** `SERIES_COLORS` (exported from the package)
holds 16 hex values: the first 8 are validated against *every pair*
(a chart draws its series simultaneously) and are the only slots a chart
should use — fold anything past the 8th into an "Other" bucket. The second 8
are the same hue families stepped in lightness, valid only for adjacent
table-row tints, never for a chart legend. A series color is a 3:1 contrast
*mark*, never text — pair it with a swatch, not colored text.

**Compose, don't reimplement.** `FormRow` is a self-contained label-left/
control-right unit (its own small grid, not a shared ancestor) — stack
several to build a form. `DataTable` is fully controlled: sort state,
selection, and linked-row highlighting are props, not internal state, so it
composes with whatever owns the data. `Modal` renders its own backdrop —
mount it directly, don't nest it inside a `Panel`.

**Realistic content over placeholder text.** Every component in this kit was
authored with plausible combat-log-style data (character names, damage
numbers, class names) rather than "foo"/"lorem ipsum" — match that register
when composing new screens with it.
