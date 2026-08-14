import type { ContextSpan, ContextTimeline } from "./api";
import { chartInk } from "./chartTheme";

/**
 * The context strip: where the character was and what level they were, drawn
 * as thin labelled bands along the top of a time chart.
 *
 * Fight bands answer "was I fighting". This answers the two questions a reader
 * asks next about a step in the numbers — where was I, and was I even the same
 * character then. A DPS floor that doubles halfway through a log means one
 * thing if the level went up in that hour and quite another if it did not, and
 * an XP rate is not comparable across two zones.
 *
 * Deliberately NOT drawn the way fights are. Fight bands tint the full height
 * of the plot, and a second full-height layer would tint the same pixels
 * twice: two overlapping washes, neither readable, over a line that is still
 * supposed to be the subject. A strip stacks instead of overlapping — it costs
 * a little height at the top and nothing at all in clarity. Zones sit above
 * levels because a zone changes more often and reads as the heading.
 */

/** What the strip shows. */
export type ContextMode = "off" | "zone" | "level" | "both";

export const CONTEXT_MODES: { value: ContextMode; label: string }[] = [
  { value: "off", label: "off" },
  { value: "zone", label: "zone" },
  { value: "level", label: "level" },
  { value: "both", label: "zone + level" },
];

export const DEFAULT_CONTEXT_MODE: ContextMode = "both";

/** Fraction of the plot height one lane of the strip takes. */
const LANE_HEIGHT = 0.055;

/**
 * Past this many bands in a lane the strip is stripes rather than labels. Zones
 * change far less often than pulls do, so this is generous and rarely reached —
 * but a log full of zone-hopping should stop drawing rather than produce a
 * comb.
 */
const MAX_BANDS = 60;

/** A band narrower than this cannot hold even a truncated name. */
const MIN_LABEL_PX = 26;

// Alternating tints, so two zones that touch are still two zones. Warmer than
// the fight bands' neutral wash: this is a different kind of fact and reading
// it as "another pull" would be worse than not drawing it.
const ZONE_TINT_A = "rgba(6, 113, 209, 0.22)"; // SERIES_COLORS slot 4
const ZONE_TINT_B = "rgba(6, 113, 209, 0.12)";
const LEVEL_TINT_A = "rgba(224, 182, 78, 0.16)"; // --gold
const LEVEL_TINT_B = "rgba(224, 182, 78, 0.09)";

export interface ContextMarkArea {
  silent: true;
  label: Record<string, unknown>;
  data: unknown[];
}

interface Lane {
  spans: ContextSpan[];
  tints: [string, string];
  /** 0 is the topmost lane. */
  index: number;
  prefix: string;
}

/**
 * Bands for the spans overlapping [fromMs, toMs], as an ECharts markArea.
 *
 * The vertical extent is given in axis values rather than pixels or percents,
 * because the caller already knows the axis it just computed and mixing pixel
 * coordinates into a markArea keyed by xAxis is where this stops being
 * predictable. Returns undefined when there is nothing worth drawing, which
 * the caller can hand straight to ECharts.
 */
export function contextMarkArea(
  context: ContextTimeline | null,
  mode: ContextMode,
  fromMs: number,
  toMs: number,
  /** Top of the value axis — the strip hangs from here. */
  axisTop: number,
  /** Bottom of the value axis, so lane height scales with the plot. */
  axisFloor: number,
  /** Width of the plot area, for deciding which bands have room to be named. */
  plotWidthPx: number,
): ContextMarkArea | undefined {
  if (!context || mode === "off" || !(axisTop > axisFloor)) {
    return undefined;
  }

  const lanes: Lane[] = [];
  if (mode === "zone" || mode === "both") {
    lanes.push({ spans: context.zones, tints: [ZONE_TINT_A, ZONE_TINT_B], index: 0, prefix: "" });
  }
  if (mode === "level" || mode === "both") {
    lanes.push({
      spans: context.levels,
      tints: [LEVEL_TINT_A, LEVEL_TINT_B],
      index: lanes.length,
      // A bare number in a band is a mystery; "L42" is a level.
      prefix: "L",
    });
  }

  const height = (axisTop - axisFloor) * LANE_HEIGHT;
  const pxPerMs = toMs > fromMs ? plotWidthPx / (toMs - fromMs) : 0;
  const data: unknown[] = [];

  for (const lane of lanes) {
    const visible: { begin: number; end: number; label: string }[] = [];
    for (const span of lane.spans) {
      const begin = new Date(span.range.begin).getTime();
      const end = new Date(span.range.end).getTime();
      if (end < fromMs || begin > toMs) {
        continue;
      }

      visible.push({ begin, end, label: lane.prefix + span.label });
      if (visible.length > MAX_BANDS) {
        break; // this lane is a comb; drop it and keep the other one
      }
    }

    if (visible.length === 0 || visible.length > MAX_BANDS) {
      continue;
    }

    // Lanes hang from the top of the axis downward, so adding a second one
    // never moves the first.
    const top = axisTop - lane.index * height;
    const bottom = top - height;
    for (let i = 0; i < visible.length; i++) {
      const band = visible[i];
      data.push([
        {
          xAxis: band.begin,
          yAxis: bottom,
          name: band.label,
          itemStyle: { color: i % 2 === 0 ? lane.tints[0] : lane.tints[1] },
          // Per band: a long stay is still named on a chart where the brief
          // ones around it have no room.
          label: { show: (band.end - band.begin) * pxPerMs >= MIN_LABEL_PX },
        },
        { xAxis: band.end, yAxis: top },
      ]);
    }
  }

  if (data.length === 0) {
    return undefined;
  }

  return {
    silent: true,
    label: {
      show: true,
      position: "inside",
      color: chartInk().ink2,
      fontSize: 10,
      overflow: "truncate",
    },
    data,
  };
}
