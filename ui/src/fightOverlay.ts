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
 * How many names fit before they start colliding, at the reference size.
 * Bigger text needs more room, so the cap scales down with it — otherwise
 * turning the size up would turn the labels into a smear.
 */
const MAX_LABELS_AT_9PX = 18;

export const DEFAULT_LABEL_PX = 9;

/** Sizes offered in the top bar. 0 is off: shading with no names. */
export const LABEL_SIZE_CHOICES: { value: number; label: string }[] = [
  { value: 0, label: "off" },
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
  /** Name size in px; 0 draws the bands without naming them. */
  labelPx: number = DEFAULT_LABEL_PX,
): MarkArea | undefined {
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

  // `|| DEFAULT` keeps the divisor sane when the size is 0 (off).
  const maxLabels = Math.max(
    4,
    Math.round((MAX_LABELS_AT_9PX * DEFAULT_LABEL_PX) / (labelPx || DEFAULT_LABEL_PX)),
  );
  return {
    silent: true,
    label: {
      show: labelPx > 0 && bands.length <= maxLabels,
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
      },
      { xAxis: band.end },
    ]),
  };
}
