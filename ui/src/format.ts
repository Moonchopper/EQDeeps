// Display formatting per the domain conventions: K/M/B with one decimal,
// rates to one decimal place.

export function fmtNum(value: number): string {
  const abs = Math.abs(value);
  if (abs >= 1e9) return (value / 1e9).toFixed(1) + "B";
  if (abs >= 1e6) return (value / 1e6).toFixed(1) + "M";
  if (abs >= 1e3) return (value / 1e3).toFixed(1) + "K";
  return Math.round(value).toString();
}

export function fmtRate(value: number): string {
  return value.toFixed(1) + "%";
}

export function fmtClock(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

export function fmtDuration(beginIso: string, endIso: string): string {
  const seconds = Math.max(1, (new Date(endIso).getTime() - new Date(beginIso).getTime()) / 1000 + 1);
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return m > 0 ? `${m}m ${s.toString().padStart(2, "0")}s` : `${s}s`;
}

/**
 * Categorical slots for the dark surface (#1a1a19), in claim order.
 *
 * Slots 1–8 are the validated eight and have not moved — a palette's ORDER is
 * its colorblind-safety mechanism, so re-ordering it is never cosmetic.
 *
 * Slots 9–16 are those same eight hue families stepped down in lightness
 * (OKLCH L 0.545, chroma held). Lightness is the axis worth growing along:
 * protanopia and deuteranopia collapse hue but preserve lightness, so a darker
 * step of a hue stays separable where a *new* hue squeezed between two existing
 * ones does not. An earlier attempt at four extra hues at the same lightness
 * failed outright — indigo against pink came out at ΔE 3.3 under deuteranopia.
 * Their order within the tier was brute-forced over all 8! arrangements for the
 * best worst-adjacent pair.
 *
 * Sixteen is the ceiling here, not a round number to stop at. A third tier has
 * nowhere to go: the dark band is L 0.48–0.67, and stepping below ~0.52 drops
 * under 3:1 contrast on this surface while collapsing into the tier above it
 * (a 24-slot attempt failed on both counts — worst adjacent ΔE 4.5 deutan, 8.2
 * normal-vision, with eight slots under contrast). So past the sixteenth the
 * registry REPEATS this list instead of extending it: a seventeenth color that
 * fails contrast would be worse than reusing one that passes. The wrap pair
 * (slot 16 → slot 1) is validated alongside the rest.
 *
 * Validated as a set with the data-viz validator (adjacent pairlist, dark mode,
 * surface #1a1a19): lightness band, chroma floor, CVD separation (worst
 * adjacent ΔE 8.4 protan — the original yellow↔aqua pair, unchanged by the
 * extension), normal-vision floor (16.3, over the 15 gate), and contrast (all
 * 16 at or above 3:1). Charts still cap at eight series and fold the rest into
 * "Other": a ninth SERIES is a different question from a ninth row tint, and
 * only the tint has a label beside it doing the identifying.
 */
export const SERIES_COLORS = [
  "#3987e5",
  "#d95926",
  "#199e70",
  "#c98500",
  "#d55181",
  "#008300",
  "#9085e9",
  "#e66767",
  "#0e880c",
  "#1e6fcb",
  "#bd4100",
  "#06855c",
  "#986405",
  "#ba386a",
  "#6c5fbf",
  "#bb3f43",
];

/** The eight chart-series slots — see SERIES_COLORS on why charts stop at eight. */
export const CHART_SERIES_LIMIT = 8;

export const OTHER_COLOR = "#898781";
