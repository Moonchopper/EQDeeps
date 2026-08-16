import type { ButtonHTMLAttributes } from "react";
import { cx } from "../lib/cx";

export interface MiniButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** A pressed/standing state — "following live", "this filter is on". */
  active?: boolean;
}

/** The smallest button in the kit — a toggle riding a toolbar or an action row. */
export function MiniButton({ active, className, ...rest }: MiniButtonProps) {
  return <button className={cx("mini-btn", active && "on", className)} {...rest} />;
}
