import type { ReactNode } from "react";

export interface FormRowProps {
  label: string;
  children: ReactNode;
}

/**
 * One label-left/control-right row. Deliberately self-contained rather than
 * requiring every field to share one parent grid: each row carries its own
 * `.form-grid` (label column fixed at 90px, control column flexible), so
 * FormRows compose freely — stack a few in a plain flex column and every
 * label still lines up, without the caller having to hand-place children
 * into one shared grid.
 */
export function FormRow({ label, children }: FormRowProps) {
  return (
    <div className="form-grid">
      <label>{label}</label>
      <div>{children}</div>
    </div>
  );
}
