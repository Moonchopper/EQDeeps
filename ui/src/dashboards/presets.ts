import { defaultPanel, type DashboardDef, type LayoutRect, type PanelDef } from "./model";

/**
 * Preset dashboards covering the breakdowns the parsing community actually
 * argues about: damage rankings, healing with overheal, tanking with the
 * defensive rates, and a fight-agnostic "right now" view.
 *
 * Presets are provisioned, not added: they carry STABLE ids, and
 * `reconcilePresets` runs at every load — presets missing from the store
 * appear automatically, an edited preset keeps the user's version (same id),
 * and a deleted preset stays deleted via the hidden list. "Restore presets"
 * rewrites them to this pristine definition, which is idempotent.
 */
export function presetDashboards(): DashboardDef[] {
  return [raidDps(), healing(), tanking(), rightNow()];
}

export const PRESET_IDS = new Set(presetDashboards().map((d) => d.id));

export interface ReconcileResult {
  dashboards: DashboardDef[];
  changed: boolean;
}

export function reconcilePresets(stored: DashboardDef[], hidden: string[]): ReconcileResult {
  const presets = presetDashboards();
  let changed = false;
  let dashboards = [...stored];

  // Migration: the old "add presets" button cloned packs under random ids.
  // Collapse them — the first dashboard named like a preset adopts the stable
  // id (keeping any customization); later same-named copies are dropped.
  for (const preset of presets) {
    if (dashboards.some((d) => d.id === preset.id)) {
      continue;
    }

    const twins = dashboards.filter((d) => d.name === preset.name);
    if (twins.length > 0) {
      changed = true;
      const keeper = { ...twins[0], id: preset.id };
      dashboards = dashboards.filter((d) => d.name !== preset.name);
      dashboards.push(keeper);
    }
  }

  // Provision presets that are neither stored nor deliberately hidden.
  for (const preset of presets) {
    if (!hidden.includes(preset.id) && !dashboards.some((d) => d.id === preset.id)) {
      dashboards.push(preset);
      changed = true;
    }
  }

  return { dashboards, changed };
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
