import { defaultPanel, newId, type DashboardDef, type LayoutRect, type PanelDef } from "./model";

/**
 * The standard views: specialized breakdowns that ship with EQDeeps and sit
 * as sub-tabs under Overview — healing with overheal, tanking with the
 * defensive rates, and the progression sources (experience, faction, loot).
 *
 * Damage rankings and a "right now" view used to be here and are not, because
 * Summary already answers both: it carries the damage summary and a DPS chart
 * that follows live by default. A standard view has to earn its tab.
 *
 * These are DEFINED IN CODE and never stored. They used to be provisioned
 * into the user's dashboard store with stable ids, which is precisely why
 * they read as pre-provisioned dashboards: they were deletable, exportable
 * and drag-editable like anything the user had built. Now they are read-only
 * app furniture, and "customize" clones one into a real custom dashboard the
 * user owns (see `cloneForCustomizing`).
 */
export function standardViews(): DashboardDef[] {
  return [healing(), tanking(), stances(), experience(), faction(), loot()];
}

/**
 * The Stances view is conditional, unlike its neighbours: most servers and most
 * characters never log a stance switch, and a tab that is always empty teaches
 * the user to ignore that row of tabs. The session reports whether it saw any.
 */
export const STANCES_VIEW_ID = "preset-stances";

export const STANDARD_VIEW_IDS = new Set(standardViews().map((d) => d.id));

/**
 * Views that used to ship and no longer do. They stay listed because the
 * store migration has to keep recognising them: an install that never ran the
 * build which stripped provisioned presets would otherwise find them
 * resurrected as the user's own dashboards, which is not where they came from
 * and not what "removed" should mean.
 */
const RETIRED_VIEW_IDS = ["preset-raid-dps", "preset-right-now"];

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
  const dashboards = stored.filter(
    (d) => !STANDARD_VIEW_IDS.has(d.id) && !RETIRED_VIEW_IDS.includes(d.id),
  );
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

/**
 * The trends the Summary page carries beside the DPS chart. Healing and damage
 * taken are the other two halves of the same question a parse asks — output,
 * upkeep, and what the mob did back — so they belong on the landing view next
 * to damage rather than a tab away.
 *
 * They are panel definitions rather than bespoke components so they run the
 * same query path, get the same fight bands, and honour the same time frame
 * and window as everything else.
 */
export function summaryTrendPanels(): PanelDef[] {
  return [
    panel("summary-healing", {
      title: "Healing over time",
      viz: "line",
      source: "healing",
      scopeMode: "all",
      groupBy: ["player"],
      bucketSeconds: 1,
    }),
    panel("summary-tanking", {
      title: "Damage taken over time",
      viz: "line",
      source: "tanking",
      scopeMode: "all",
      groupBy: ["player"],
      bucketSeconds: 1,
    }),
  ];
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

/**
 * Stances: what switching actually bought you.
 *
 * Every panel here is `ownerOnly`, because a stance is a fact about the log's
 * own character. The parser can read your switches and nobody else's — their
 * client wrote theirs, into their log — so folding a raid's damage into your
 * stance would be inventing the thing the view exists to measure.
 *
 * The headline column is "per s held", not DPS. Plain DPS divides by the time
 * you were landing hits, which quietly refunds a stance every second it made
 * you slower — precisely the cost you switched stances to weigh. Dividing by
 * the time the stance was HELD is the comparison people mean, and both columns
 * are shown side by side so the gap between them is legible rather than a
 * matter of trusting one number.
 *
 * The line chart is the overlay in data form: one series per stance, non-zero
 * only while that stance was held, so the switch points are where one line
 * stops and the next begins — over the same fight bands every other chart uses.
 */
function stances(): DashboardDef {
  return build(STANCES_VIEW_ID, "Stances", [
    [
      {
        title: "Your damage by stance",
        viz: "table",
        source: "damage",
        ownerOnly: true,
        groupBy: ["stance", "spell"],
        metrics: [
          "total", "stanceDps", "dps", "stanceSeconds", "stanceUptime",
          "percentOfTotal", "hits", "avgHit", "critRate", "maxHit",
        ],
      },
      { x: 0, y: 0, w: 12, h: 10 },
    ],
    [
      {
        title: "DPS by stance over time",
        viz: "line",
        source: "damage",
        ownerOnly: true,
        groupBy: ["stance"],
        bucketSeconds: 1,
      },
      { x: 0, y: 10, w: 8, h: 9 },
    ],
    [
      {
        title: "Damage per second held",
        viz: "bar",
        source: "damage",
        ownerOnly: true,
        groupBy: ["stance"],
        primaryMetric: "stanceDps",
      },
      { x: 8, y: 10, w: 4, h: 5 },
    ],
    [
      {
        title: "Time in each stance",
        viz: "bar",
        source: "damage",
        ownerOnly: true,
        groupBy: ["stance"],
        primaryMetric: "stanceSeconds",
      },
      { x: 8, y: 15, w: 4, h: 4 },
    ],
    // The other half of the trade. A defensive stance that costs damage is
    // supposed to buy something back, and these two say whether it did.
    [
      {
        title: "Damage taken by stance",
        viz: "table",
        source: "tanking",
        ownerOnly: true,
        groupBy: ["stance"],
        metrics: [
          "total", "stanceDps", "stanceSeconds", "meleeAttempts",
          "undefendedRate", "avgHit", "maxHit",
        ],
      },
      { x: 0, y: 19, w: 6, h: 8 },
    ],
    [
      {
        title: "Your healing by stance",
        viz: "table",
        source: "healing",
        ownerOnly: true,
        groupBy: ["stance", "spell"],
        metrics: ["total", "stanceDps", "stanceSeconds", "overhealRate", "hits", "maxHit"],
      },
      { x: 6, y: 19, w: 6, h: 8 },
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
//
// The two tables are the same data read from both ends and sit side by side on
// purpose: "what dropped this item" and "what does this mob drop" are the two
// questions a loot log gets asked, and neither answers the other.
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
      { x: 0, y: 0, w: 6, h: 12 },
    ],
    [
      {
        title: "Drops by mob",
        viz: "droprate",
        source: "loot",
        scopeMode: "all",
        groupBy: ["target", "spell"],
        metrics: ["loots"],
      },
      { x: 6, y: 0, w: 6, h: 12 },
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
      { x: 0, y: 12, w: 2, h: 4 },
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
      { x: 0, y: 16, w: 2, h: 4 },
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
      },
      { x: 2, y: 12, w: 10, h: 8 },
    ],
  ]);
}
