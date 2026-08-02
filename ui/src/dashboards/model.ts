import type { Dimension, QueryFilter, QuerySource, QuerySpec } from "../api";

// The user-facing panel definition: a QuerySpec plus presentation. This is
// what dashboards persist and what export/import moves between machines.

export type PanelViz = "table" | "line" | "bar" | "tile";
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
  /** Line panels: bucket width and rolling-mean window. */
  bucketSeconds: number;
  windowSec: number;
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
  activeSeconds: "Active s",
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
};

export const RATE_METRICS = new Set([
  "percentOfTotal", "critRate", "luckyRate", "twincastRate", "flurryRate",
  "riposteRate", "strikethroughRate", "meleeHitRate", "meleeAccuracy",
  "undefendedRate", "overhealRate",
]);

export const DIMENSIONS: { value: Dimension; label: string }[] = [
  { value: "player", label: "player" },
  { value: "spell", label: "ability/spell" },
  { value: "target", label: "target" },
  { value: "damageType", label: "damage type" },
  { value: "character", label: "character" },
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
    bucketSeconds: 1,
    windowSec: 5,
  };
}

export function newDashboard(name: string): DashboardDef {
  return { id: newId("d"), name, panels: [], layout: [] };
}

/** Binds a panel's stored definition to the live context at render time. */
export function buildSpec(panel: PanelDef, fightIds: number[], petRollup: boolean): QuerySpec {
  const scope: QuerySpec["scope"] = {};
  if (panel.scopeMode === "recent") {
    scope.lastSeconds = panel.lastSeconds + (panel.viz === "line" ? panel.windowSec : 0);
  } else {
    if (panel.scopeMode === "selection") {
      scope.fightIds = fightIds;
    }
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
      panel.viz === "table"
        ? panel.metrics
        : [...new Set([panel.primaryMetric, "total"])],
    filters,
    bucketSeconds: panel.viz === "line" ? panel.bucketSeconds : undefined,
    petRollup,
  };
}
