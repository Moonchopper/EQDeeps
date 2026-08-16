import type { ReactNode } from "react";
import { IconChevronsLeft, IconChevronsRight } from "@tabler/icons-react";
import { cx } from "../lib/cx";
import { NavRailGroup } from "./NavRailGroup";
import { NavRailItem } from "./NavRailItem";
import type { RailIcon } from "./icons";

export interface NavRailEntry {
  key: string;
  label: string;
  icon?: RailIcon;
  description?: string;
  variant?: "default" | "add";
}

export interface NavRailGroupData {
  key: string;
  heading: string;
  entries: NavRailEntry[];
}

export interface NavRailProps {
  groups: NavRailGroupData[];
  activeKey?: string;
  collapsed?: boolean;
  onSelect?: (key: string) => void;
  onToggleCollapsed?: () => void;
  /** The utility cluster at the foot — Settings, Logs, a version string. Anything, rendered as-is. */
  footer?: ReactNode;
}

const ICON_SIZE = 16;
const ICON_STROKE = 1.75;

/**
 * The grouped, collapsible navigation rail: entries grouped by the question
 * they answer, a selected-state left edge, and a collapsed-to-icons state
 * that's the same rail, narrower — not a different control. Compose it from
 * NavRailGroup/NavRailItem directly for anything this data shape doesn't fit.
 */
export function NavRail({ groups, activeKey, collapsed, onSelect, onToggleCollapsed, footer }: NavRailProps) {
  const Chevrons = collapsed ? IconChevronsRight : IconChevronsLeft;
  const toggleTitle = collapsed ? "Expand the rail: icons and names" : "Collapse the rail to icons";

  return (
    <nav className={cx("nav-rail", collapsed && "collapsed")} aria-label="Views">
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
      {groups.map((g) => (
        <NavRailGroup key={g.key} heading={g.heading} collapsed={collapsed}>
          {g.entries.map((e) => (
            <NavRailItem
              key={e.key}
              label={e.label}
              icon={e.icon}
              description={e.description}
              variant={e.variant}
              active={activeKey === e.key}
              collapsed={collapsed}
              onClick={() => onSelect?.(e.key)}
            />
          ))}
        </NavRailGroup>
      ))}
      {footer && <div className="rail-foot">{footer}</div>}
    </nav>
  );
}
