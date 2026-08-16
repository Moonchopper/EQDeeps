import type { ReactNode } from "react";
import { cx } from "../lib/cx";

export interface PanelProps {
  /** Title bar text. Omit for a bare surface with no header rule. */
  title?: ReactNode;
  /** Rendered at the right end of the title bar — a chip, a tab strip, a toggle. */
  titleActions?: ReactNode;
  children?: ReactNode;
  className?: string;
}

/**
 * The one surface every table, chart and form sits on. Elevation is a
 * lightness step plus a rim-light on the top edge, never a drop shadow —
 * shadows don't read on a dark ground.
 */
export function Panel({ title, titleActions, children, className }: PanelProps) {
  return (
    <div className={cx("panel", className)}>
      {title != null && (
        <div className="panel-title">
          <span>{title}</span>
          {titleActions && <div className="title-controls">{titleActions}</div>}
        </div>
      )}
      <div className="panel-body">{children}</div>
    </div>
  );
}
