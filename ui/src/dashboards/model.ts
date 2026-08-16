import type { Dimension, QueryFilter, QuerySource, QuerySpec } from "../api";
import { windowSeconds, type ChartSettings } from "../timeControls";
import { frameAtSpan, frameScope, frameSpanSeconds, type TimeFrame } from "../timeFrame";
import { queryBucketSeconds } from "../chartInteractions";

// The user-facing panel definition: a QuerySpec plus presentation. This is
// what dashboards persist and what export/import moves between machines.

/**
 * "droprate" is the one viz that is not a plain reading of its own query: it
 * joins the loot rows to a kill count from the death source (see DropRatePanel).
 * It still carries an ordinary QuerySpec, so it queries, scopes and filters
 * like everything else.
 */
/**
 * "map" is the one viz that reads no query at all (F27). Every other panel
 * here, droprate included, is a reading of records the log produced; a map is a
 * drawing on disk. Its panel still carries a QuerySpec because `PanelDef` has
 * one, but nothing runs it — see ADR-016 for why the map is a rail destination
 * first and a panel second.
 */
/**
 * "items" is the item feed (F29): looted, sold, bought and named in chat over
 * the time frame, newest first — a list, not an aggregation, so like "map" it
 * runs no QuerySpec of its own; it reads the record stream through its own
 * endpoint and takes only the panel's scope from the frame.
 */
export type PanelViz = "table" | "line" | "bar" | "tile" | "droprate" | "map" | "items";
export type PanelScopeMode = "selection" | "all" | "recent";

export interface PanelDef {
  id: string;
  title: string;
  viz: PanelViz;
  source: QuerySource;
  scopeMode: PanelScopeMode;
  lastSeconds: number;
  skipFirstSeconds: number;
  maxSeconds: number | null;
  groupBy: Dimension[];
  /** Table columns, in order. */
  metrics: string[];
  /** The one metric a bar/tile shows. */
  primaryMetric: string;
  /** Validity categories excluded from the parse. */
  excludeFlags: string[];
  playerFilter: string[];
  spellFilter: string[];
  /**
   * Restrict the panel to the log's own character (and their pets).
   *
   * A name filter cannot express this in a saved panel: the panel outlives the
   * session it was built in, and a dashboard exported from one character would
   * report on that character forever. This resolves against whichever log is
   * open, which is what "me" has to mean for a stored view.
   */
  ownerOnly?: boolean;
  /**
   * Map panels: the zone to show, as a map short name. Unset means "wherever
   * the character is", which is what makes the panel worth having beside a
   * parse — pinning it is for watching somewhere you are not.
   */
  mapZone?: string;
  /**
   * Line panels: the server-side bucket width. This is a QUERY parameter —
   * it decides what the server aggregates. The rolling window and the
   * viewport span are presentation and live in the app-wide chart settings
   * (see DEFAULT_CHART_SETTINGS), not here, so every chart starts uniform.
   */
  bucketSeconds: number;
}

export interface LayoutRect {
  i: string;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface DashboardDef {
  id: string;
  name: string;
  panels: PanelDef[];
  layout: LayoutRect[];
}

export const METRIC_LABELS: Record<string, string> = {
  total: "Total",
  dps: "DPS",
  sdps: "SDPS",
  pdps: "PDPS",
  percentOfTotal: "% of total",
  hits: "Hits",
  avgHit: "Avg hit",
  maxHit: "Max hit",
  critRate: "Crit %",
  luckyRate: "Lucky %",
  twincastRate: "Twincast %",
  flurryRate: "Flurry %",
  riposteRate: "Riposte %",
  strikethroughRate: "Strikethrough %",
  meleeHitRate: "Hit %",
  meleeAccuracy: "Accuracy %",
  undefendedRate: "Undefended %",
  overhealRate: "Overheal %",
  extra: "Overheal",
  potential: "Potential",
  activeSeconds: "Active time",
  deaths: "Deaths",
  casts: "Casts",
  interrupts: "Interrupts",
  fizzles: "Fizzles",
  xpPercent: "XP %",
  xpPerHour: "XP %/hr",
  xpGains: "Gains",
  aaPoints: "AA points",
  factionNet: "Net",
  factionUps: "Gains",
  factionDowns: "Losses",
  factionCapped: "Capped",
  loots: "Items",
  platinum: "Plat",
  platPerHour: "Plat/hr",
  considers: "Considers",
  conLevel: "Level",
  stanceSeconds: "Time held",
  raidSeconds: "Elapsed",
  stanceDps: "Per s held",
  stanceUptime: "Uptime %",
};

export const RATE_METRICS = new Set([
  "percentOfTotal", "critRate", "luckyRate", "twincastRate", "flurryRate",
  "riposteRate", "strikethroughRate", "meleeHitRate", "meleeAccuracy",
  "undefendedRate", "overhealRate", "stanceUptime",
]);

export const DIMENSIONS: { value: Dimension; label: string }[] = [
  { value: "player", label: "player" },
  { value: "spell", label: "ability/spell" },
  { value: "target", label: "target" },
  { value: "damageType", label: "damage type" },
  { value: "character", label: "character" },
  { value: "stance", label: "your stance" },
];

export const VALIDITY_FLAGS: { value: string; label: string }[] = [
  { value: "damageShield", label: "damage shields" },
  { value: "headshot", label: "headshots" },
  { value: "assassinate", label: "assassinates" },
  { value: "finishingBlow", label: "finishing blows" },
  { value: "slayUndead", label: "slay undead" },
];

let counter = 0;

export function newId(prefix: string): string {
  return `${prefix}${Date.now().toString(36)}${(counter++).toString(36)}`;
}

export function defaultPanel(): PanelDef {
  return {
    id: newId("p"),
    title: "Damage summary",
    viz: "table",
    source: "damage",
    scopeMode: "selection",
    lastSeconds: 60,
    skipFirstSeconds: 0,
    maxSeconds: null,
    groupBy: ["player", "spell"],
    metrics: ["total", "dps", "sdps", "percentOfTotal", "critRate"],
    primaryMetric: "total",
    excludeFlags: [],
    playerFilter: [],
    spellFilter: [],
    ownerOnly: false,
    bucketSeconds: 1,
  };
}

export function newDashboard(name: string): DashboardDef {
  return { id: newId("d"), name, panels: [], layout: [] };
}

/**
 * Binds a panel's stored definition to the live context at render time.
 *
 * The time frame is the query, not just the picture. A whole-log panel is
 * scoped to the span the user is looking at, so the tile beside a chart
 * counts exactly the seconds the chart draws — a "2 m" span with an all-time
 * total next to it is just two different questions sharing a panel border.
 * Span "fit" means the whole log, which is where these started.
 *
 * Time charts additionally fetch one rolling window of extra history, so the
 * mean is already warm at the left edge of the viewport instead of ramping up
 * from nothing. That history sits outside the drawn axis range.
 *
 * A panel whose header sets a span of its own is asking that panel to report
 * over that span, so a live frame is taken at the panel's span rather than the
 * app's (see frameAtSpan). Without an override the two are the same value.
 */
/**
 * The bucket a time panel queries at: its own width, coarsened when the range
 * is long enough that its width would fetch more points than a chart can show.
 * Exported so the panel draws on exactly the grid it asked for.
 */
/** The rolling window a time panel actually smooths over, at its query bucket. */
export function panelWindowSeconds(
  panel: PanelDef,
  frame: TimeFrame,
  settings: ChartSettings,
  logSpanSeconds: number,
): number {
  // The window is a bucket COUNT, so its length in seconds is that count times
  // whatever the server actually aggregated at — including the coarser bucket a
  // long range falls back to, which widens the window in step for free. Scaling
  // it against the panel's own nominal bucket instead was the bug: on a
  // minute-bucketed panel that cancelled out and left a window of seconds, which
  // rounds to one bucket and smooths nothing.
  return windowSeconds(
    settings.windowBuckets,
    panelBucketSeconds(panel, frame, settings, logSpanSeconds),
  );
}

export function panelBucketSeconds(
  panel: PanelDef,
  frame: TimeFrame,
  settings: ChartSettings,
  logSpanSeconds: number,
): number {
  return queryBucketSeconds(
    panel.bucketSeconds,
    frameSpanSeconds(frame, settings.spanSec, logSpanSeconds),
  );
}

export function buildSpec(
  panel: PanelDef,
  frame: TimeFrame,
  petRollup: boolean,
  settings: ChartSettings,
  logSpanSeconds: number,
  /** The open log's character, for panels marked `ownerOnly`. */
  character = "",
  /**
   * Measure a framed range over the time actually played rather than the time
   * it spans. Set app-wide rather than per panel: every reading on the screen
   * has to be divided by the same hours, or a tile and the table beside it
   * disagree about how long the evening was.
   */
  playedTimeOnly = false,
): QuerySpec {
  const warmup =
    panel.viz === "line" ? panelWindowSeconds(panel, frame, settings, logSpanSeconds) : 0;
  // A "recent" panel keeps a fixed window of its own, independent of the
  // frame. No standard view uses it now, but the query builder still offers
  // it for a custom panel that wants a fixed trailing window.
  const scope: QuerySpec["scope"] =
    panel.scopeMode === "recent"
      ? { lastSeconds: panel.lastSeconds + warmup }
      : frameScope(frameAtSpan(frame, settings.spanSec), warmup);
  // A trailing window is a window of clock by definition, so it takes the
  // setting too: "the last six hours" spent mostly asleep is the same question
  // as any other range.
  if (playedTimeOnly) {
    scope.playedTimeOnly = true;
  }

  if (panel.scopeMode !== "recent") {
    if (panel.skipFirstSeconds > 0) {
      scope.skipFirstSeconds = panel.skipFirstSeconds;
    }
    if (panel.maxSeconds) {
      scope.maxSeconds = panel.maxSeconds;
    }
  }

  const filters: QueryFilter[] = panel.excludeFlags.map((flag) => ({
    flag: flag as QueryFilter["flag"],
    exclude: true,
  }));
  // Pet rollup maps a pet's records onto its owner before the filter is
  // tested, so "me" already includes my pets — no need to name them.
  if (panel.ownerOnly && character) {
    filters.push({ dim: "player", values: [character] });
  }
  if (panel.playerFilter.length > 0) {
    filters.push({ dim: "player", values: panel.playerFilter });
  }
  if (panel.spellFilter.length > 0) {
    filters.push({ dim: "spell", values: panel.spellFilter });
  }

  return {
    source: panel.source,
    scope,
    groupBy: panel.groupBy,
    metrics:
      panel.viz === "droprate"
        ? // Fixed columns, so the query asks for exactly what they need — and
          // "loots" first, which is what the server ranks the rows by.
          ["loots"]
        : panel.viz === "table"
          ? panel.metrics
          : [...new Set([panel.primaryMetric, "total"])],
    filters,
    bucketSeconds:
      panel.viz === "line"
        ? panelBucketSeconds(panel, frame, settings, logSpanSeconds)
        : undefined,
    petRollup,
  };
}
