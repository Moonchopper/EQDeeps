import type { InputHTMLAttributes } from "react";
import { cx } from "../lib/cx";

export interface TextInputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "type"> {
  /** The narrow numeric-field treatment (a filter value, not a full-width form field). */
  numeric?: boolean;
}

export function TextInput({ numeric, className, ...rest }: TextInputProps) {
  return (
    <input
      type={numeric ? "number" : "text"}
      className={cx(numeric ? "num-input" : "text-input", className)}
      {...rest}
    />
  );
}
