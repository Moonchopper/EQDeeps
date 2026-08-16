import type { ComponentType } from "react";
import {
  IconChartLine,
  IconDiamond,
  IconFileText,
  IconHeart,
  IconLayoutDashboard,
  IconLayoutSidebarLeftCollapse,
  IconLayoutSidebarLeftExpand,
  IconMap2,
  IconPlus,
  IconSettings,
  IconShield,
  IconSkull,
  IconSwords,
  IconTargetArrow,
  IconTrendingUp,
  IconUsersGroup,
} from "@tabler/icons-react";
import type { UpdateState } from "../api";
import type { DashboardDef } from "../dashboards/model";
import { RAIL_GROUPS } from "../dashboards/railGroups";
import {
  HITS_VIEW,
  MAPS_VIEW,
  MOBS_VIEW,
  STANCES_VIEW_ID,
  SUMMARY_VIEW,
} from "../dashboards/standardViews";

type Icon = ComponentType<{ size?: number | string; stroke?: number | string; className?: string }>;

/**
 * The rail entries that are not standard-view dashboards, keyed by view id.
 * The standard views bring their own names; these four are hand-built screens
 * and need theirs spelled out here.
 */
const RAIL_ENTRIES: Record<string, { name: string; title?: string }> = {
  [SUMMARY_VIEW]: { name: "Summary" },
  [MOBS_VIEW]: {
    name: "Mobs",
    title: "What this server's mobs are worth, and what a difficulty tier costs",
  },
  [HITS_VIEW]: {
    name: "Incoming",
    title: "What is hitting you, in order, and what this server's mobs hit for",
  },
  [MAPS_VIEW]: { name: "Map", title: "Your own zone maps, and how the world joins up" },
};

/**
 * One icon per shipped view, so the rail still says where you can go once
 * it is collapsed to a column of glyphs. Chosen for the question the view
 * answers rather than the mechanism: a heart for healing, a shield for
 * tanking, a skull for what a mob is worth. Every user dashboard shares one
 * icon — they are the user's, and their names are what tell them apart.
 */
const RAIL_ICONS: Record<string, Icon> = {
  [SUMMARY_VIEW]: IconChartLine,
  "preset-healing": IconHeart,
  "preset-tanking": IconShield,
  [STANCES_VIEW_ID]: IconSwords,
  [HITS_VIEW]: IconTargetArrow,
  "preset-experience": IconTrendingUp,
  "preset-faction": IconUsersGroup,
  "preset-loot": IconDiamond,
  [MOBS_VIEW]: IconSkull,
  [MAPS_VIEW]: IconMap2,
};

const ICON_SIZE = 16;
const ICON_STROKE = 1.75;

interface Props {
  /** The standard views this log actually has (Stances is conditional). */
  standard: DashboardDef[];
  dashboards: DashboardDef[];
  /** "overview" or a dashboard id. */
  view: string;
  /** Which shipped view is on screen when `view` is "overview". */
  activeStdView: string;
  onSelectStdView: (id: string) => void;
  onSelectDashboard: (id: string) => void;
  onRenameDashboard: (id: string) => void;
  onAddDashboard: () => void;
  onExportDashboard: (id: string) => void;
  onImportDashboard: (file: File) => void;
  onDeleteDashboard: (id: string) => void;
  onOpenLogs: () => void;
  onOpenSettings: () => void;
  update: UpdateState | null;
  collapsed: boolean;
  onToggleCollapsed: () => void;
}

/**
 * The navigation rail (ADR-014, ADR-017): the shipped views grouped by the
 * question they answer, the user's dashboards, and the utility cluster at the
 * foot. Collapsed, it is a column of icons with the labels as hover titles —
 * the same rail, narrower, not a different control.
 */
export function NavRail({
  standard,
  dashboards,
  view,
  activeStdView,
  onSelectStdView,
  onSelectDashboard,
  onRenameDashboard,
  onAddDashboard,
  onExportDashboard,
  onImportDashboard,
  onDeleteDashboard,
  onOpenLogs,
  onOpenSettings,
  update,
  collapsed,
  onToggleCollapsed,
}: Props) {
  const stdClass = (id: string) =>
    "rail-tab" + (view === "overview" && activeStdView === id ? " on" : "");
  // Collapsed, the label moves into the title so hovering still names the
  // entry; expanded, the title is the longer description where there is one.
  const titleFor = (name: string, description?: string) =>
    collapsed ? (description ? `${name} — ${description}` : name) : description;

  const Toggle = collapsed ? IconLayoutSidebarLeftExpand : IconLayoutSidebarLeftCollapse;
  const updateWaiting = Boolean(update && (update.restartRequired || update.promptRequired));

  return (
    <nav className={"nav-rail" + (collapsed ? " collapsed" : "")} aria-label="Views">
      {RAIL_GROUPS.map((g) => (
        <div key={g.key} className="rail-group">
          <div className="rail-heading">
            <span className="rail-heading-text">{g.label}</span>
          </div>
          {g.ids.map((id) => {
            const special = RAIL_ENTRIES[id];
            const std = special ? null : standard.find((d) => d.id === id);
            // A standard view this log does not have (Stances) is simply
            // absent, not disabled.
            if (!special && !std) return null;
            const name = special?.name ?? std!.name;
            const Glyph = RAIL_ICONS[id];
            return (
              <button
                key={id}
                className={stdClass(id)}
                onClick={() => onSelectStdView(id)}
                title={titleFor(name, special?.title)}
                aria-label={collapsed ? name : undefined}
              >
                {Glyph && <Glyph size={ICON_SIZE} stroke={ICON_STROKE} className="rail-icon" />}
                <span className="rail-label">{name}</span>
              </button>
            );
          })}
        </div>
      ))}
      <div className="rail-group">
        <div className="rail-heading">
          <span className="rail-heading-text">Dashboards</span>
        </div>
        {dashboards.map((d) => (
          <button
            key={d.id}
            className={"rail-tab" + (view === d.id ? " on" : "")}
            onClick={() => onSelectDashboard(d.id)}
            onDoubleClick={() => onRenameDashboard(d.id)}
            title={collapsed ? d.name : "Double-click to rename"}
            aria-label={collapsed ? d.name : undefined}
          >
            <IconLayoutDashboard size={ICON_SIZE} stroke={ICON_STROKE} className="rail-icon" />
            <span className="rail-label">{d.name}</span>
          </button>
        ))}
        <button
          className="rail-tab add"
          onClick={onAddDashboard}
          title="New dashboard"
          aria-label={collapsed ? "New dashboard" : undefined}
        >
          <IconPlus size={ICON_SIZE} stroke={ICON_STROKE} className="rail-icon" />
          <span className="rail-label">New</span>
        </button>
        {/* Only ever the selected dashboard's actions, so they sit under the
            list they act on; a collapsed rail has no room for three words,
            and they come back with the labels. */}
        {view !== "overview" && !collapsed && (
          <div className="rail-actions">
            <button className="mini-btn" onClick={() => onExportDashboard(view)}>
              export
            </button>
            <label className="mini-btn" title="Import a dashboard JSON">
              import
              <input
                type="file"
                accept=".json"
                style={{ display: "none" }}
                onChange={(e) => {
                  const file = e.target.files?.[0];
                  if (file) onImportDashboard(file);
                  e.target.value = "";
                }}
              />
            </label>
            <button className="mini-btn" onClick={() => onDeleteDashboard(view)}>
              delete
            </button>
          </div>
        )}
      </div>
      {/* The utility cluster, pinned to the foot of the rail: what the app is
          and how it is set, apart from where you are in it (ADR-017). */}
      <div className="rail-foot">
        <button
          className="rail-tab"
          onClick={onOpenLogs}
          title={titleFor("Logs", "Every log EQDeeps can see, and a box for one it can't")}
          aria-label={collapsed ? "Logs" : undefined}
        >
          <IconFileText size={ICON_SIZE} stroke={ICON_STROKE} className="rail-icon" />
          <span className="rail-label">Logs</span>
        </button>
        <button
          className="rail-tab"
          onClick={onOpenSettings}
          title={titleFor("Settings", "Display, chart and update preferences")}
          aria-label={collapsed ? "Settings" : undefined}
        >
          <span className="rail-icon-wrap">
            <IconSettings size={ICON_SIZE} stroke={ICON_STROKE} className="rail-icon" />
            {/* A staged or offered update is the one thing in here worth a
                glance before you open it; on the icon so it survives collapse. */}
            {updateWaiting && <span className="rail-dot" aria-label="Update available" />}
          </span>
          <span className="rail-label">Settings</span>
        </button>
        <div className="rail-foot-row">
          {update && <span className="rail-version rail-label">v{update.version}</span>}
          <button
            className="rail-collapse"
            onClick={onToggleCollapsed}
            title={collapsed ? "Expand the rail" : "Collapse the rail to icons"}
            aria-label={collapsed ? "Expand the rail" : "Collapse the rail to icons"}
            aria-expanded={!collapsed}
          >
            <Toggle size={ICON_SIZE} stroke={ICON_STROKE} />
          </button>
        </div>
      </div>
    </nav>
  );
}
