import { cx } from "../lib/cx";
import type { RailIcon } from "./icons";

const ICON_SIZE = 16;
const ICON_STROKE = 1.75;

export interface NavRailItemProps {
  label: string;
  icon?: RailIcon;
  active?: boolean;
  /** Icons-only layout — the label moves into the hover title so it's still findable. */
  collapsed?: boolean;
  /** Longer hover text, shown expanded; folded into the title alongside the label when collapsed. */
  description?: string;
  /** The "new dashboard" affordance — accent-coloured, not a selectable destination. */
  variant?: "default" | "add";
  onClick?: () => void;
}

/**
 * One entry in a nav rail. The selected marker is a left edge, never an
 * outline — a rail of outlined pills reads as separate controls rather than
 * one list of views.
 */
export function NavRailItem({
  label,
  icon: Icon,
  active,
  collapsed,
  description,
  variant = "default",
  onClick,
}: NavRailItemProps) {
  const title = collapsed ? (description ? `${label} — ${description}` : label) : description;
  return (
    <button
      className={cx("rail-tab", active && "on", variant === "add" && "add")}
      onClick={onClick}
      title={title}
      aria-label={collapsed ? label : undefined}
      aria-current={active ? "page" : undefined}
    >
      {Icon && <Icon size={ICON_SIZE} stroke={ICON_STROKE} className="rail-icon" />}
      <span className="rail-label">{label}</span>
    </button>
  );
}
