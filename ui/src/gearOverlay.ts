import type { GearChange, GearSlotChange } from "./api";

/**
 * Vertical marks on a time chart for the moments the player's gear changed.
 *
 * Where fight bands are areas — a stretch of time with a name — a gear change
 * is a single instant, so it gets a line. That difference is the point: the
 * question the mark answers is "is this the same character on both sides of
 * here", and a band would imply a duration that nothing in the data supports.
 *
 * A caveat rides along with every one of these. The instant drawn is when the
 * change was *proven* (the snapshot), not when it happened — the player could
 * have swapped a weapon at any point since the previous dump. The mark is
 * therefore drawn as the right-hand edge of an uncertainty window, and the
 * tooltip says so rather than leaving the line to be read as precise.
 */

/** Past this many marks the chart is a picket fence and tells nobody anything. */
const MAX_MARKS = 40;

const MARK_COLOR = "#c9a227";

export interface MarkLine {
  silent: true;
  symbol: string[];
  label: Record<string, unknown>;
  lineStyle: Record<string, unknown>;
  data: unknown[];
}

/** One slot's change, in words. Shared with the Gear tab so the two agree. */
export function describeSlot(slot: GearSlotChange): string {
  switch (slot.kind) {
    case "upgraded":
      return `${slot.location} ${slot.before?.baseName ?? ""} +${slot.before?.plus ?? 0} → +${slot.after?.plus ?? 0}`;
    case "replaced":
      return `${slot.location} ${slot.before?.baseName ?? "?"} → ${slot.after?.baseName ?? "?"}`;
    case "equipped":
      return `${slot.location} ${slot.after?.name ?? ""} equipped`;
    case "removed":
      return `${slot.location} ${slot.before?.name ?? ""} removed`;
    case "reaugmented":
      return `${slot.location} augments changed`;
    default:
      return slot.location;
  }
}

/**
 * A whole change in one line. "13 slots" is true and useless — a swap that
 * size still has one item in it that mattered most, so lead with the biggest
 * upgrade and count the rest.
 */
export function summariseChange(slots: GearSlotChange[]): string {
  if (slots.length === 0) return "gear changed";
  const delta = (s: GearSlotChange) => Math.abs((s.after?.plus ?? 0) - (s.before?.plus ?? 0));
  const lead = [...slots].sort((a, b) => delta(b) - delta(a))[0];
  return slots.length === 1
    ? describeSlot(lead)
    : `${describeSlot(lead)} · ${slots.length - 1} more`;
}

/** What a chart mark is labelled with. */
export function describeChange(change: GearChange): string {
  return summariseChange(change.slots);
}

/**
 * Marks for the gear changes inside [fromMs, toMs] — the chart's own extent, so
 * nothing is built for time that isn't on screen. Returns undefined when there
 * is nothing worth drawing, which the caller can pass straight to ECharts.
 */
export function gearMarkLine(
  changes: GearChange[],
  fromMs: number,
  toMs: number,
  /**
   * Whether the chart overlay is on at all. Gear marks are always named when
   * they are drawn — unlike fight bands, which can sensibly be shading with no
   * text, an unnamed vertical line is a mystery rather than a backdrop.
   */
  enabled = true,
): MarkLine | undefined {
  if (!enabled) {
    return undefined;
  }

  const marks: { at: number; text: string }[] = [];
  for (const change of changes) {
    const at = new Date(change.at).getTime();
    if (at < fromMs || at > toMs) {
      continue;
    }

    marks.push({ at, text: describeChange(change) });
    if (marks.length > MAX_MARKS) {
      return undefined;
    }
  }

  if (marks.length === 0) {
    return undefined;
  }

  return {
    silent: true,
    symbol: ["none", "none"],
    label: {
      show: true,
      // "{b}" is the mark's own name — a markLine label reads `name` off the
      // data item, not an arbitrary field, so the text has to live there.
      formatter: "{b}",
      position: "insideEndTop",
      distance: 4,
      color: MARK_COLOR,
      fontSize: 10,
    },
    lineStyle: { color: MARK_COLOR, type: "dashed", width: 1, opacity: 0.7 },
    data: marks.map((mark) => ({ xAxis: mark.at, name: mark.text })),
  };
}
