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

/** And past this many, the names stop fitting and start overlapping. */
const MAX_LABELS = 18;

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

  return {
    silent: true,
    label: {
      show: bands.length <= MAX_LABELS,
      position: "insideTop",
      distance: 4,
      rotate: 90,
      align: "left",
      verticalAlign: "middle",
      color: "#898781",
      fontSize: 9,
      overflow: "truncate",
      width: 90,
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
