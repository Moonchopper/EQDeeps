import type { ButtonHTMLAttributes } from "react";
import { cx } from "../lib/cx";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** `primary` is the one committing action in a row — accent-filled, never more than one per row. */
  variant?: "default" | "primary";
}

export function Button({ variant = "default", className, ...rest }: ButtonProps) {
  return <button className={cx("btn", variant === "primary" && "primary", className)} {...rest} />;
}
