import type { ButtonHTMLAttributes } from "react";
import { cx } from "../lib/cx";

export interface TabProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  active?: boolean;
  size?: "default" | "small";
}

/** One entry in a tab strip — a filled block when selected, never an outline. */
export function Tab({ active, size = "default", className, ...rest }: TabProps) {
  return (
    <button
      className={cx("tab", active && "on", size === "small" && "small", className)}
      aria-pressed={active}
      {...rest}
    />
  );
}
