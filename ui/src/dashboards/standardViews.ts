import { defaultPanel, newId, type DashboardDef, type LayoutRect, type PanelDef } from "./model";

/**
 * The standard views: specialized breakdowns that ship with EQDeeps and sit
 * as sub-tabs under Overview. They cover what the parsing community actually
 * argues about — damage rankings, healing with overheal, tanking with the
 * defensive rates, a fight-agnostic "right now", and the progression sources
 * (experience, faction, loot).
 *
 * These are DEFINED IN CODE and never stored. They used to be provisioned
 * into the user's dashboard store with stable ids, which is precisely why
 * they read as pre-provisioned dashboards: they were deletable, exportable
 * and drag-editable like anything the user had built. Now they are read-only
 * app furniture, and "customize" clones one into a real custom dashboard the
 * user owns (see `cloneForCustomizing`).
 */
export function standardViews(): DashboardDef[] {
  return [raidDps(), healing(), tanking(), rightNow(), experience(), faction(), loot()];
}

export const STANDARD_VIEW_IDS = new Set(standardViews().map((d) => d.id));

/** The sub-tab that shows the hand-built Overview, not a standard view. */
export const SUMMARY_VIEW = "summary";

export interface MigrationResult {
  dashboards: DashboardDef[];
  changed: boolean;
}

/**
 * One-time migration off the old model: drop the provisioned copies of the
 * standard views from the stored dashboard list, since the app now renders
 * them from code. Anything the user built themselves is left untouched —
 * including a former preset they renamed, because that no longer carries a
 * standard-view id.
 */
export function stripStandardViews(stored: DashboardDef[]): MigrationResult {
  const dashboards = stored.filter((d) => !STANDARD_VIEW_IDS.has(d.id));
  return { dashboards, changed: dashboards.length !== stored.length };
}

/**
 * "Customize" on a standard view: a deep copy under fresh ids, so editing it
 * is ordinary custom-dashboard editing and the standard view stays pristine.
 */
export function cloneForCustomizing(view: DashboardDef): DashboardDef {
  const id = newId("d");
  const idMap = new Map(view.panels.map((p, i) => [p.id, `${id}-p${i}`]));
  return {
    id,
    name: `${view.name} (custom)`,
    panels: view.panels.map((p) => ({ ...p, id: idMap.get(p.id)! })),
    layout: view.layout.map((l) => ({ ...l, i: idMap.get(l.i) ?? l.i })),
  };
}

function panel(id: string, overrides: Partial<PanelDef>): PanelDef {
  return { ...defaultPanel(), ...overrides, id };
}

function build(
  id: string,
  name: string,
  entries: [Partial<PanelDef>, Omit<LayoutRect, "i">][],
): DashboardDef {
  const panels: PanelDef[] = [];
  const layout: LayoutRect[] = [];
  entries.forEach(([def, rect], index) => {
    const p = panel(`${id}-p${index}`, def);
    panels.push(p);
    layout.push({ i: p.id, ...rect });
  });

  return { id, name, panels, layout };
}

function raidDps(): DashboardDef {
  return build("preset-raid-dps", "Raid DPS", [
    [
      {
        title: "Damage rankings",
        viz: "table",
        source: "damage",
        groupBy: ["player", "spell"],
        metrics: ["total", "dps", "sdps", "percentOfTotal", "critRate", "twincastRate", "maxHit"],
      },
      { x: 0, y: 0, w: 7, h: 10 },
    ],
    [
      {
        title: "DPS over time",
        viz: "line",
        source: "damage",
        groupBy: ["player"],
        bucketSeconds: 1,
        windowSec: 5,
      },
      { x: 7, y: 0, w: 5, h: 10 },
    ],
    [
      {
        title: "Damage by ability",
        viz: "bar",
        source: "damage",
        groupBy: ["spell"],
        primaryMetric: "total",
      },
      { x: 0, y: 10, w: 7, h: 8 },
    ],
    [
      {
        title: "Total damage",
        viz: "tile",
        source: "damage",
        groupBy: ["player"],
        primaryMetric: "total",
      },
      { x: 7, y: 10, w: 2, h: 4 },
    ],
    [
      {
        title: "Raid deaths",
        viz: "tile",
        source: "deaths",
        groupBy: ["player"],
        primaryMetric: "deaths",
      },
      { x: 9, y: 10, w: 3, h: 4 },
    ],
    [
      {
        title: "Kill shots excluded",
        viz: "table",
        source: "damage",
        groupBy: ["player"],
        metrics: ["total", "sdps", "percentOfTotal"],
        excludeFlags: ["headshot", "assassinate", "finishingBlow", "slayUndead"],
      },
      { x: 7, y: 14, w: 5, h: 4 },
    ],
  ]);
}

function healing(): DashboardDef {
  return build("preset-healing", "Healing", [
    [
      {
        title: "Healer rankings",
        viz: "table",
        source: "healing",
        groupBy: ["player", "spell"],
        metrics: ["total", "dps", "percentOfTotal", "overhealRate", "critRate", "hits", "maxHit"],
      },
      { x: 0, y: 0, w: 7, h: 10 },
    ],
    [
      {
        title: "Healing over time",
        viz: "line",
        source: "healing",
        groupBy: ["player"],
        bucketSeconds: 1,
        windowSec: 5,
      },
      { x: 7, y: 0, w: 5, h: 10 },
    ],
    [
      {
        title: "Healing received",
        viz: "table",
        source: "healing",
        groupBy: ["target", "player"],
        metrics: ["total", "percentOfTotal", "hits", "maxHit"],
      },
      { x: 0, y: 10, w: 6, h: 8 },
    ],
    [
      {
        title: "Healing by spell",
        viz: "bar",
        source: "healing",
        groupBy: ["spell"],
        primaryMetric: "total",
      },
      { x: 6, y: 10, w: 6, h: 8 },
    ],
  ]);
}

function tanking(): DashboardDef {
  return build("preset-tanking", "Tanking", [
    [
      {
        title: "Damage taken",
        viz: "table",
        source: "tanking",
        groupBy: ["player", "spell"],
        metrics: ["total", "dps", "percentOfTotal", "meleeAttempts", "undefendedRate", "maxHit"],
      },
      { x: 0, y: 0, w: 7, h: 10 },
    ],
    [
      {
        title: "Hardest hitters",
        viz: "bar",
        source: "tanking",
        groupBy: ["target"],
        primaryMetric: "total",
      },
      { x: 7, y: 0, w: 5, h: 10 },
    ],
    [
      {
        title: "Incoming damage over time",
        viz: "line",
        source: "tanking",
        groupBy: ["player"],
        bucketSeconds: 1,
        windowSec: 5,
      },
      { x: 0, y: 10, w: 7, h: 8 },
    ],
    [
      {
        title: "Biggest hit taken",
        viz: "tile",
        source: "tanking",
        groupBy: ["player"],
        primaryMetric: "maxHit",
      },
      { x: 7, y: 10, w: 2, h: 4 },
    ],
    [
      {
        title: "Deaths",
        viz: "table",
        source: "deaths",
        groupBy: ["player", "target"],
        metrics: ["deaths"],
      },
      { x: 9, y: 10, w: 3, h: 8 },
    ],
  ]);
}

function rightNow(): DashboardDef {
  return build("preset-right-now", "Right now", [
    [
      {
        title: "Rolling DPS — last 2 minutes",
        viz: "line",
        source: "damage",
        scopeMode: "recent",
        lastSeconds: 120,
        groupBy: ["player"],
        bucketSeconds: 1,
        windowSec: 5,
      },
      { x: 0, y: 0, w: 8, h: 10 },
    ],
    [
      {
        title: "Damage — last 60 s",
        viz: "table",
        source: "damage",
        scopeMode: "recent",
        lastSeconds: 60,
        groupBy: ["player"],
        metrics: ["total", "dps", "percentOfTotal"],
      },
      { x: 8, y: 0, w: 4, h: 10 },
    ],
    [
      {
        title: "Abilities — last 60 s",
        viz: "bar",
        source: "damage",
        scopeMode: "recent",
        lastSeconds: 60,
        groupBy: ["spell"],
        primaryMetric: "total",
      },
      { x: 0, y: 10, w: 8, h: 8 },
    ],
    [
      {
        title: "Healing — last 60 s",
        viz: "table",
        source: "healing",
        scopeMode: "recent",
        lastSeconds: 60,
        groupBy: ["player"],
        metrics: ["total", "dps", "overhealRate"],
      },
      { x: 8, y: 10, w: 4, h: 8 },
    ],
  ]);
}

// XP arrives at kills but also outside fights entirely (quests, turn-ins), so
// every panel scopes to the whole log rather than the fight selection.
function experience(): DashboardDef {
  return build("preset-experience", "Experience", [
    [
      {
        title: "XP over time",
        viz: "line",
        source: "experience",
        scopeMode: "all",
        groupBy: ["character"],
        primaryMetric: "xpPercent",
        bucketSeconds: 60,
        windowSec: 300,
      },
      { x: 0, y: 0, w: 8, h: 10 },
    ],
    [
      {
        title: "XP gained",
        viz: "tile",
        source: "experience",
        scopeMode: "all",
        groupBy: ["character"],
        primaryMetric: "xpPercent",
      },
      { x: 8, y: 0, w: 4, h: 4 },
    ],
    [
      {
        title: "XP per hour",
        viz: "tile",
        source: "experience",
        scopeMode: "all",
        groupBy: ["character"],
        primaryMetric: "xpPerHour",
      },
      { x: 8, y: 4, w: 4, h: 3 },
    ],
    [
      {
        title: "AA points",
        viz: "tile",
        source: "experience",
        scopeMode: "all",
        groupBy: ["character"],
        primaryMetric: "aaPoints",
      },
      { x: 8, y: 7, w: 4, h: 3 },
    ],
    [
      {
        title: "By kind",
        viz: "table",
        source: "experience",
        scopeMode: "all",
        groupBy: ["spell"],
        metrics: ["xpPercent", "xpPerHour", "xpGains", "aaPoints"],
      },
      { x: 0, y: 10, w: 8, h: 8 },
    ],
  ]);
}

// Faction moves at kills and quest turn-ins alike, so panels scope to the
// whole log; rows/series are the factions themselves.
function faction(): DashboardDef {
  return build("preset-faction", "Faction", [
    [
      {
        title: "Standing changes over time",
        viz: "line",
        source: "faction",
        scopeMode: "all",
        groupBy: ["player"],
        primaryMetric: "factionNet",
        bucketSeconds: 60,
        windowSec: 300,
      },
      { x: 0, y: 0, w: 8, h: 10 },
    ],
    [
      {
        title: "Net standing",
        viz: "tile",
        source: "faction",
        scopeMode: "all",
        groupBy: ["player"],
        primaryMetric: "factionNet",
      },
      { x: 8, y: 0, w: 4, h: 4 },
    ],
    [
      {
        title: "By faction",
        viz: "table",
        source: "faction",
        scopeMode: "all",
        groupBy: ["player", "spell"],
        metrics: ["factionNet", "factionUps", "factionDowns", "factionCapped"],
      },
      { x: 0, y: 10, w: 12, h: 8 },
    ],
  ]);
}

// Loot lands after the kill (outside fight spans) — whole-log scope. Items
// group on the spell dimension, their source corpse on target.
function loot(): DashboardDef {
  return build("preset-loot", "Loot", [
    [
      {
        title: "Items looted",
        viz: "table",
        source: "loot",
        scopeMode: "all",
        groupBy: ["spell", "target"],
        metrics: ["loots", "platinum"],
      },
      { x: 0, y: 0, w: 8, h: 10 },
    ],
    [
      {
        title: "Coin (plat)",
        viz: "tile",
        source: "loot",
        scopeMode: "all",
        groupBy: ["player"],
        primaryMetric: "platinum",
      },
      { x: 8, y: 0, w: 4, h: 4 },
    ],
    [
      {
        title: "Plat per hour",
        viz: "tile",
        source: "loot",
        scopeMode: "all",
        groupBy: ["player"],
        primaryMetric: "platPerHour",
      },
      { x: 8, y: 4, w: 4, h: 3 },
    ],
    [
      {
        title: "Coin over time",
        viz: "line",
        source: "loot",
        scopeMode: "all",
        groupBy: ["character"],
        primaryMetric: "platinum",
        bucketSeconds: 60,
        windowSec: 300,
      },
      { x: 0, y: 10, w: 12, h: 8 },
    ],
  ]);
}
