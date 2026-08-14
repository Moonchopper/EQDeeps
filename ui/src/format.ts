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

/**
 * A "when" that says as much as it has to and no more.
 *
 * A learned index spans months, so a bare clock time is not a date — "22:33"
 * above "09:14" reads as out of order until you know they are different days,
 * which the row does not say. Today stays a time, because within a session that
 * is the useful precision; anything older grows the day, and anything from
 * another year grows the year. Nothing carries a component that is the same for
 * every row on screen.
 */
export function fmtWhen(iso: string): string {
  const d = new Date(iso);
  const now = new Date();
  const sameDay =
    d.getFullYear() === now.getFullYear() &&
    d.getMonth() === now.getMonth() &&
    d.getDate() === now.getDate();

  if (sameDay) {
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  }

  return d.getFullYear() === now.getFullYear()
    ? d.toLocaleString([], { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })
    : d.toLocaleDateString([], { day: "numeric", month: "short", year: "numeric" });
}

/**
 * A span of seconds as a duration people read: "3d 2h 30m 15s".
 *
 * Units run from the largest non-zero one down to seconds, keeping the zeros in
 * between — "3d 0h 0m 15s" rather than "3d 15s", which reads as three days and
 * fifteen seconds only if you already knew that was what it meant. Below a
 * minute it is bare seconds.
 */
export function fmtSpan(seconds: number): string {
  const total = Math.max(0, Math.round(seconds));
  if (total < 60) return `${total}s`;

  const units: [number, string][] = [
    [86400, "d"],
    [3600, "h"],
    [60, "m"],
    [1, "s"],
  ];
  const parts: string[] = [];
  let rest = total;
  for (const [size, suffix] of units) {
    const n = Math.floor(rest / size);
    rest -= n * size;
    // Skip leading zeros; keep them once something has been printed, so the
    // places stay readable.
    if (n === 0 && parts.length === 0) continue;
    parts.push(`${n}${suffix}`);
  }
  return parts.join(" ");
}

export function fmtDuration(beginIso: string, endIso: string): string {
  const seconds = Math.max(1, (new Date(endIso).getTime() - new Date(beginIso).getTime()) / 1000 + 1);
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return m > 0 ? `${m}m ${s.toString().padStart(2, "0")}s` : `${s}s`;
}

/**
 * Categorical slots for the dark panel surface (--surface #26211c), in claim
 * order. Re-derived for ADR-015; the structure of the previous set is kept
 * because its reasoning was right, but the gate it was validated against was
 * the wrong one for half its job.
 *
 * ## Two tiers, because CVD collapses hue and keeps lightness
 *
 * Slots 1–8 are eight distinct hues. Slots 9–16 are those same eight hue
 * families at a different lightness — protanopia and deuteranopia flatten hue
 * but preserve lightness, so a lightness step of a known-good hue separates
 * where a *new* hue squeezed between two existing ones does not. Their order
 * within the tier is brute-forced over all 8! arrangements for the best
 * worst-adjacent pair, including the junction into tier one and the wrap from
 * slot 16 back to slot 1.
 *
 * Tier two steps DOWN in lightness for most hues and UP for orange, olive and
 * blue. The previous set stepped everything down, which worked against the old
 * #1a1a19 surface; the panel is lighter now, and orange has no darker step left
 * that still clears 3:1 on it. The separation argument is about the size of the
 * lightness gap, not its direction.
 *
 * ## The gate, and why it changed
 *
 * A chart draws its eight series SIMULTANEOUSLY, so the right question for the
 * chart set is every pair, not neighbouring pairs. The previous palette was
 * validated on adjacency and passed; checked on all pairs its first eight
 * collapsed to ΔE 1.6 under deuteranopia (#d55181 against #199e70) and 7.1
 * under ordinary vision. Two series on the busiest chart in the app were, for
 * practical purposes, the same colour.
 *
 * So the two halves are now held to different standards, matching what each is
 * actually asked to do:
 *
 *   Slots 1–8   ALL PAIRS. Worst ΔE 8.2 protan, 15.3 normal-vision, every slot
 *               at or above 3:1 on both --surface and --surface-2. Verified
 *               against both, because a chip on a selected row sits on the
 *               lighter one.
 *   Slots 1–16  ADJACENT, the historical gate. Worst ΔE 9.5 deutan, 17.4
 *               normal-vision, all 16 at or above 3:1.
 *
 * All sixteen do NOT clear all-pairs, and no sixteen-colour set does. Searching
 * 9,443 candidates inside the dark band with a 3:1 floor, the best achievable
 * worst pair scores 0.68 against the 1.0 pass line at sixteen slots, 0.80 at
 * twelve, 0.94 at ten, and only reaches 1.06 at eight — and that one is close
 * to neon. Sixteen mutually distinguishable fills on a dark ground do not
 * exist, at any level of care. That is why slots 9–16 are reached only by table
 * rows, where the entity's NAME sits beside the chip and colour is never the
 * sole channel, and why charts fold everything past the eighth into "Other".
 *
 * Past the sixteenth the registry repeats this list rather than extending it: a
 * seventeenth colour that fails contrast is worse than reusing one that passes.
 *
 * A series colour is a 3:1 MARK. It is never text. Legend entries, axis labels
 * and tooltip bodies take --ink-2 and the swatch beside them carries identity;
 * chartTheme.ts enforces that for every chart in the app.
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
  // tier two — the same eight families, stepped in lightness; table tints only
  "#3195fe",
  "#a95469",
  "#2aae70",
  "#c96bca",
  "#e96a19",
  "#028f9e",
  "#79720e",
  "#6f63b8",
];

/** The eight chart-series slots — see SERIES_COLORS on why charts stop at eight. */
export const CHART_SERIES_LIMIT = 8;

/** Everything folded past the chart cap. Tracks --muted: "not an entity". */
export const OTHER_COLOR = "#968e7e";
