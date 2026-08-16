/**
 * The 8-slot categorical chart palette, and the 8 more that extend it for
 * table-row tints. Ported verbatim from EQDeeps' ui/src/format.ts.
 *
 * These are plain hex literals rather than CSS custom properties because a
 * charting library (or any canvas/SVG renderer) needs a JS-readable colour,
 * not a `var()` the browser resolves at paint time — the source app carries
 * the same duplication for the same reason. If you retune these, retune them
 * in both places and re-validate: see docs/architecture/adr-015-visual-language.md
 * decision 3 for the contrast/ΔE gates each slot has to clear (all-pairs for
 * slots 1–8, since a chart draws its series simultaneously; adjacent-only for
 * 9–16, which only ever appear next to a couple of neighbours in a table).
 */
export const SERIES_COLORS = [
  // tier one — the chart set, mutually separable across all pairs
  "#e56386",
  "#03a8ba",
  "#ba5003",
  "#0671d1",
  "#a2991b",
  "#9280f6",
  "#00814e",
  "#9f51a0",
  // tier two — the same eight hue families, stepped in lightness; table
  // row tints only, never charts
  "#3195fe",
  "#a95469",
  "#2aae70",
  "#c96bca",
  "#e96a19",
  "#028f9e",
  "#79720e",
  "#6f63b8",
] as const;

/** A chart stops drawing distinct series past this many — see SERIES_COLORS. */
export const CHART_SERIES_LIMIT = 8;

/** Everything folded past the chart cap. Tracks --muted: "not an entity". */
export const OTHER_COLOR = "#968e7e";
