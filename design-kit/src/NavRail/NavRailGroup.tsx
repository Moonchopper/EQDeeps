import type { ReactNode } from "react";

export interface NavRailGroupProps {
  /** Named for the question its entries answer — "Combat", "Character", "World". */
  heading: string;
  collapsed?: boolean;
  children: ReactNode;
}

/**
 * One column of rail entries under a heading. The heading sits brighter than
 * its entries (which sit at --muted) and set off by a rule from the group
 * before it — a heading and its entries at the same grey read as one flat
 * list with a few rows in caps.
 */
export function NavRailGroup({ heading, collapsed, children }: NavRailGroupProps) {
  return (
    <div className="rail-group">
      <div className="rail-heading">{!collapsed && <span className="rail-heading-text">{heading}</span>}</div>
      {children}
    </div>
  );
}
