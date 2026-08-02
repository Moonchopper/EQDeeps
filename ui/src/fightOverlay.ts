import type { FightInfo } from "./api";

/**
 * Fight bands drawn behind a time chart: the stretches where something was
 * actually being fought, named by the mob.
 *
 * This is what turns a trough into information. Without it every dip reads the
 * same — "nothing happened" — when the interesting question is whether you
 * were between pulls, running to the next camp, or fighting something that
 * simply did not hurt. The bands are deliberately faint: they are the
 * backdrop, and the line is still the subject.
 */

/** Past this many bands the shading is noise rather than context. */
const MAX_BANDS = 120;

/**
 * Clearance a rotated name needs beside itself. Rotated text is only about as
 * WIDE as its font size — the length runs vertically — so whether two names
 * collide depends on how many pixels its band spans, not on how many bands
 * are on screen. Counting bands was the wrong model: it hid names at 15
 * minutes where they had 70-odd pixels each to sit in.
 */
const LABEL_CLEARANCE_PX = 3;

export const DEFAULT_LABEL_PX = 14;

/** No overlay at all — not even the shading. */
export const OVERLAY_OFF = -1;

/**
 * One control for the whole overlay, because it is one feature: whether the
 * bands are there, and how loudly they are named. 0 keeps the shading and
 * drops the names, which is the useful middle ground on a dense chart.
 */
export const LABEL_SIZE_CHOICES: { value: number; label: string }[] = [
  { value: OVERLAY_OFF, label: "off" },
  { value: 0, label: "bands" },
  { value: 9, label: "small" },
  { value: 11, label: "medium" },
  { value: 14, label: "large" },
];

// Alternating tints so two pulls that touch are still distinguishable.
const TINT_A = "rgba(255, 255, 255, 0.05)";
const TINT_B = "rgba(255, 255, 255, 0.09)";

export interface MarkArea {
  silent: true;
  label: Record<string, unknown>;
  data: unknown[];
}

/**
 * Bands for the fights overlapping [fromMs, toMs] — the chart's own data
 * extent, so nothing is built for time that isn't on screen. Returns
 * undefined when there is nothing worth drawing, which the caller can pass
 * straight to ECharts.
 */
export function fightMarkArea(
  fights: FightInfo[],
  fromMs: number,
  toMs: number,
  /** Height of the plot area, so a name can never run off the top of it. */
  plotHeightPx: number,
  /** Width of the plot area, for deciding which bands have room to be named. */
  plotWidthPx: number,
  /** Name size in px; 0 draws the bands without naming them. */
  labelPx: number = DEFAULT_LABEL_PX,
): MarkArea | undefined {
  if (labelPx <= OVERLAY_OFF) {
    return undefined; // overlay switched off entirely
  }

  const bands: { begin: number; end: number; name: string }[] = [];
  for (const fight of fights) {
    const begin = new Date(fight.beginTime).getTime();
    const end = new Date(fight.lastDamageTime).getTime();
    if (end < fromMs || begin > toMs) {
      continue;
    }

    bands.push({ begin, end, name: fight.name });
    if (bands.length > MAX_BANDS) {
      return undefined; // a whole evening of pulls: solid shading tells nobody anything
    }
  }

  if (bands.length === 0) {
    return undefined;
  }

  // A band earns its name when it is wider on screen than the name is thick.
  const pxPerMs = toMs > fromMs ? plotWidthPx / (toMs - fromMs) : 0;
  const minBandPx = labelPx + LABEL_CLEARANCE_PX;
  const fits = (band: { begin: number; end: number }) =>
    labelPx > 0 && (band.end - band.begin) * pxPerMs >= minBandPx;

  return {
    silent: true,
    label: {
      show: labelPx > 0,
      // Anchored at the BOTTOM and read upward. Rotated text grows along the
      // rotated x-axis, which points up — so anchoring at the top sent every
      // name straight out of the plot and the chart clipped it. Growing up
      // from the floor keeps it inside by construction, and the explicit
      // width (the run length of the text, vertical once rotated) stops a
      // long mob name reaching the ceiling.
      position: "insideBottom",
      distance: 6,
      rotate: 90,
      align: "left",
      verticalAlign: "middle",
      color: "#898781",
      fontSize: labelPx || DEFAULT_LABEL_PX,
      overflow: "truncate",
      width: Math.max(24, plotHeightPx - 24),
    },
    data: bands.map((band, i) => [
      {
        xAxis: band.begin,
        name: band.name,
        itemStyle: { color: i % 2 === 0 ? TINT_A : TINT_B },
        // Per band, so a wide pull is still named on a chart where the short
        // ones around it have no room.
        label: { show: fits(band) },
      },
      { xAxis: band.end },
    ]),
  };
}
