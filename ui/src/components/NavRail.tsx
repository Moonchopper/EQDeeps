import type { ComponentType } from "react";
import {
  IconChartLine,
  IconChevronsLeft,
  IconChevronsRight,
  IconDiamond,
  IconFileText,
  IconHeart,
  IconLayoutDashboard,
  IconLayoutSidebarLeftCollapse,
  IconLayoutSidebarLeftExpand,
  IconMap2,
  IconPlus,
  IconRefresh,
  IconSettings,
  IconShield,
  IconBook,
  IconSkull,
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
  BESTIARY_VIEW,
  MOBS_VIEW,
  STANCES_VIEW_ID,
  SUMMARY_VIEW,
} from "../dashboards/standardViews";

type IconProps = { size?: number | string; stroke?: number | string; className?: string };
type Icon = ComponentType<IconProps>;

/**
 * A fencer in the lunge, for Stances — the one glyph the set did not have.
 * Drawn on Tabler's 24-unit grid with its stroke, caps and joins, so it sits
 * in the column as one of them: mask, blade out and up, front leg long, back
 * leg bent, off hand back. Line art rather than a filled silhouette because
 * at sixteen pixels a silhouette is a blot beside fifteen line icons.
 */
function IconFencer({ size = 24, stroke = 2, className }: IconProps) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={stroke}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden="true"
    >
      {/* mask */}
      <circle cx="15" cy="4.5" r="1.7" />
      {/* torso, leaning into the lunge */}
      <path d="M14.6 6.6 L13.2 12" />
      {/* sword arm, and the blade running up and out */}
      <path d="M14.2 7.6 L9.2 9.2 L2.5 2.5" />
      {/* off hand back on the hip */}
      <path d="M15 7.6 L18.8 10 L17.6 12.6" />
      {/* front leg, long and low; back leg bent */}
      <path d="M13.2 12 L4.5 19.5 M13.2 12 L17.6 15.2 L19.6 19.6" />
      {/* feet */}
      <path d="M2.8 20 L6 20 M18.6 20.5 L21.8 20.5" />
    </svg>
  );
}

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
  [BESTIARY_VIEW]: {
    name: "Bestiary",
    title: "Every mob in the game, searchable — and what your own logs measured",
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
  [STANCES_VIEW_ID]: IconFencer,
  [HITS_VIEW]: IconTargetArrow,
  "preset-experience": IconTrendingUp,
  "preset-faction": IconUsersGroup,
  "preset-loot": IconDiamond,
  [MOBS_VIEW]: IconSkull,
  [BESTIARY_VIEW]: IconBook,
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
  onCheckForUpdate: () => void;
  /** Transient result of a manual check, e.g. "up to date". */
  checkNote: string | null;
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
  onCheckForUpdate,
  checkNote,
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
  const Chevrons = collapsed ? IconChevronsRight : IconChevronsLeft;
  const toggleTitle = collapsed ? "Expand the rail: icons and names" : "Collapse the rail to icons";
  const updateWaiting = Boolean(update && (update.restartRequired || update.promptRequired));

  return (
    <nav className={"nav-rail" + (collapsed ? " collapsed" : "")} aria-label="Views">
      {/* The same toggle twice: a chevron at the top, where the eye lands
          first and where every app puts one, and the labelled entry at the
          foot with the other utilities. */}
      <div className="rail-top">
        <button
          className="rail-chevron"
          onClick={onToggleCollapsed}
          title={toggleTitle}
          aria-label={toggleTitle}
          aria-expanded={!collapsed}
        >
          <Chevrons size={ICON_SIZE} stroke={ICON_STROKE} />
        </button>
      </div>
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
        {/* A rail entry like the others, not a chevron in a corner: the way
            to a narrower rail should be as findable as anything else in it,
            and its label says what it does. */}
        <button
          className="rail-tab rail-collapse"
          onClick={onToggleCollapsed}
          title={toggleTitle}
          aria-label={collapsed ? "Expand the rail" : undefined}
          aria-expanded={!collapsed}
        >
          <Toggle size={ICON_SIZE} stroke={ICON_STROKE} className="rail-icon" />
          <span className="rail-label">Collapse</span>
        </button>
        {/* The version, and a check for a newer one beside it — here rather
            than only inside Settings, because "is there an update" is a
            question people ask on the way past, and the answer belongs next
            to the number it is about. The result shows in place for a few
            seconds; a found update lights the dot on Settings and the pill
            in the header. */}
        {update && (
          <div className="rail-foot-row">
            <span className="rail-version rail-label">
              {checkNote ?? `v${update.version}`}
            </span>
            <button
              className="rail-check"
              onClick={onCheckForUpdate}
              disabled={update.stage === "checking"}
              title={
                collapsed
                  ? `v${update.version} — check for updates${checkNote ? ` (${checkNote})` : ""}`
                  : "Check for updates now"
              }
              aria-label="Check for updates now"
            >
              <IconRefresh
                size={ICON_SIZE}
                stroke={ICON_STROKE}
                className={update.stage === "checking" ? "spinning" : undefined}
              />
            </button>
          </div>
        )}
      </div>
    </nav>
  );
}
