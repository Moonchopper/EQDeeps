import type { FightInfo, QuerySpec } from "./api";
import { DEFAULT_CHART_SETTINGS, type ChartSettings, type Span } from "./timeControls";
import { DEFAULT_LABEL_PX } from "./fightOverlay";

/**
 * What slice of the log the whole app is looking at. There is exactly one of
 * these, and every panel reports over it.
 *
 * Time is the substrate: every record has a timestamp, and much of what
 * matters — XP turn-ins, faction, loot, downtime itself — happens outside any
 * fight. Fights are a derived artifact (the parser's read of where a pull
 * started and stopped), so they are a way of *picking* a frame rather than the
 * axis everything hangs off. Selecting fights produces a range; the server
 * still subdivides that range per fight for combat, so DPS means what it
 * always meant.
 *
 *  live  — the trailing `spanSec` of the record stream, anchored to the newest
 *          record. Inherently follows live play: new records move the window.
 *          `spanSec: "fit"` is the whole log.
 *  range — a fixed window. Comes from the fight list, or straight off a chart
 *          the user zoomed into. Does not follow.
 */
export type TimeFrame =
  | { kind: "live"; spanSec: Span }
  | { kind: "range"; fightIds: number[]; begin: string; end: string };

export const DEFAULT_FRAME: TimeFrame = { kind: "live", spanSec: DEFAULT_CHART_SETTINGS.spanSec };

/** A live frame tracks the newest record; a fixed range does not. */
export function isLive(frame: TimeFrame): boolean {
  return frame.kind === "live";
}

/** Fight ids to highlight in the list — empty when the frame is a live tail. */
export function framedFightIds(frame: TimeFrame): number[] {
  return frame.kind === "range" ? frame.fightIds : [];
}

/**
 * The server parses timeRanges as LOCAL DateTime, matching the timestamps it
 * emits — so an epoch millisecond has to be written out in local parts.
 * `toISOString()` would hand it UTC and silently shift the window by the whole
 * UTC offset.
 */
function localIso(ms: number): string {
  const d = new Date(ms);
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` +
    `T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
  );
}

/**
 * The frame as a query scope. `warmupSec` is extra history for a rolling
 * mean's left edge; it widens the query without widening what gets drawn.
 */
export function frameScope(frame: TimeFrame, warmupSec = 0): QuerySpec["scope"] {
  if (frame.kind === "range") {
    // localIso, NOT toISOString: the latter hands back UTC, which the server
    // then reads as local. Off by the whole offset, the warmed-up begin lands
    // after the end and the range returns nothing at all — an empty chart
    // wherever a fixed range meets a rolling mean.
    const begin =
      warmupSec > 0
        ? localIso(new Date(frame.begin).getTime() - warmupSec * 1000)
        : frame.begin;
    return { timeRanges: [{ begin, end: frame.end }] };
  }

  // "fit" is the whole log: no bound at all rather than a very large one, so
  // the server takes its own unrestricted path.
  return frame.spanSec === "fit" ? {} : { lastSeconds: frame.spanSec + warmupSec };
}

/**
 * Seconds one chart's query will cover — what the bucket width has to be
 * chosen against. Mirrors frameAtSpan: a fixed range is its own length, and a
 * live tail follows the chart's span rather than the frame's. "fit" is however
 * long the log is.
 */
export function frameSpanSeconds(
  frame: TimeFrame,
  spanSec: Span,
  logSpanSeconds: number,
): number {
  if (frame.kind === "range") {
    const ms = new Date(frame.end).getTime() - new Date(frame.begin).getTime();
    return Math.max(1, Math.round(ms / 1000));
  }

  return spanSec === "fit" ? Math.max(1, logSpanSeconds) : spanSec;
}

/**
 * The frame as one chart sees it.
 *
 * A time chart carries its own span — the panel header's control, or the DPS
 * chart's own — and that span is what it puts on screen: the viewport is
 * [latest − span, latest], zero-filled, so quiet time draws along the floor.
 * Query the frame's span instead and a chart set wider than the frame draws a
 * window it never fetched: the part beyond the frame is zero-filled with
 * nothing, which reads as "no XP was gained" rather than "not asked for". A
 * live tail therefore follows the chart's span, not the frame's.
 *
 * Only live tails widen. A fixed range is a statement about which slice of the
 * log everything reports over, and no chart's viewport overrides that.
 */
export function frameAtSpan(frame: TimeFrame, spanSec: Span): TimeFrame {
  return frame.kind === "live" && frame.spanSec !== spanSec ? { kind: "live", spanSec } : frame;
}

/**
 * Builds a frame from a fight selection: the wall-clock window those fights
 * span, downtime between them included. One fight is just that fight.
 */
export function frameFromFights(fights: FightInfo[], ids: number[]): TimeFrame | null {
  const chosen = fights.filter((f) => ids.includes(f.id));
  if (chosen.length === 0) {
    return null;
  }

  let begin = chosen[0].beginTime;
  let end = chosen[0].lastDamageTime;
  for (const fight of chosen) {
    if (fight.beginTime < begin) begin = fight.beginTime;
    if (fight.lastDamageTime > end) end = fight.lastDamageTime;
  }

  return { kind: "range", fightIds: [...ids], begin, end };
}


/**
 * A frame from an arbitrary window — what a chart hands over when the user
 * zooms into something interesting and promotes it. No fights attached: the
 * window is the statement, and it need not line up with any pull.
 */
export function frameFromRange(beginMs: number, endMs: number): TimeFrame {
  const [from, to] = beginMs <= endMs ? [beginMs, endMs] : [endMs, beginMs];
  return { kind: "range", fightIds: [], begin: localIso(from), end: localIso(to) };
}

/** Inclusive index range between two fight-list positions, in either order. */
export function fightIdsBetween(fights: FightInfo[], anchorId: number, targetId: number): number[] {
  const a = fights.findIndex((f) => f.id === anchorId);
  const b = fights.findIndex((f) => f.id === targetId);
  if (a < 0 || b < 0) {
    return [targetId];
  }

  const [lo, hi] = a <= b ? [a, b] : [b, a];
  return fights.slice(lo, hi + 1).map((f) => f.id);
}

/**
 * The fights the frame covers. The timeline is inherently fight-shaped — it
 * draws per-combatant lanes — so it needs the fights inside the window rather
 * than the window itself. A live frame resolves against the newest fight,
 * matching how the server anchors its trailing scope.
 */
export function fightsInFrame(frame: TimeFrame, fights: FightInfo[]): number[] {
  if (frame.kind === "range") {
    return frame.fightIds;
  }

  if (frame.spanSec === "fit" || fights.length === 0) {
    return fights.map((f) => f.id);
  }

  const newest = new Date(fights[fights.length - 1].lastDamageTime).getTime();
  const from = newest - frame.spanSec * 1000;
  return fights.filter((f) => new Date(f.lastDamageTime).getTime() >= from).map((f) => f.id);
}

/** Short human description for the top bar. */
export function frameLabel(frame: TimeFrame, fights: FightInfo[]): string {
  if (frame.kind === "live") {
    return frame.spanSec === "fit" ? "whole log" : "live";
  }

  const chosen = fights.filter((f) => frame.fightIds.includes(f.id));
  if (chosen.length === 1) {
    return chosen[0].name;
  }

  const seconds = Math.max(
    0,
    Math.round((new Date(frame.end).getTime() - new Date(frame.begin).getTime()) / 1000),
  );
  const length = seconds >= 3600
    ? `${(seconds / 3600).toFixed(1)}h`
    : seconds >= 60
      ? `${Math.round(seconds / 60)}m`
      : `${seconds}s`;

  // A hand-picked window has no fights to name, so say when it is instead.
  if (frame.fightIds.length === 0) {
    const at = new Date(frame.begin);
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${pad(at.getHours())}:${pad(at.getMinutes())} · ${length}`;
  }

  return `${chosen.length || frame.fightIds.length} fights · ${length}`;
}

/** True when nothing has been changed from the app's opening state. */
export function isDefaultState(
  frame: TimeFrame,
  settings: ChartSettings,
  fightLabelPx: number,
): boolean {
  return (
    frame.kind === "live" &&
    frame.spanSec === DEFAULT_CHART_SETTINGS.spanSec &&
    settings.windowSec === DEFAULT_CHART_SETTINGS.windowSec &&
    fightLabelPx === DEFAULT_LABEL_PX
  );
}
