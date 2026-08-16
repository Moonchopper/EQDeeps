import { cx } from "../lib/cx";

export interface SelectionChipProps {
  /** The entity's name — truncates rather than wrapping. */
  name: string;
  /** The identity swatch colour, e.g. one of the kit's SERIES_COLORS. */
  color: string;
  /** Pinned survives across every view and a restart, not just this one. */
  pinned?: boolean;
  onTogglePin?: () => void;
  onClear?: () => void;
  /** One size down, for a panel's own title bar rather than the app header. */
  compact?: boolean;
}

/**
 * The standing selection: point at one reading of an entity anywhere on
 * screen, and it lights up everywhere. This chip is what says which entity
 * that currently is, and lets you unpin or clear it.
 */
export function SelectionChip({
  name,
  color,
  pinned,
  onTogglePin,
  onClear,
  compact,
}: SelectionChipProps) {
  return (
    <span className={cx("selection-chip", pinned && "pinned", compact && "compact")}>
      <span className="color-chip" style={{ background: color }} />
      <span className="selection-name">{name}</span>
      {onTogglePin && (
        <button
          className="selection-btn"
          aria-pressed={pinned}
          onClick={onTogglePin}
          title={pinned ? "Unpin" : "Pin to every view"}
        >
          {pinned ? "★" : "☆"}
        </button>
      )}
      {onClear && (
        <button className="selection-btn" onClick={onClear} title="Clear selection">
          {"✕"}
        </button>
      )}
    </span>
  );
}
