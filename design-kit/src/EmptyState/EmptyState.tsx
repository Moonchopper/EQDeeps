import type { ReactNode } from "react";

export type EmptyStateVariant = "empty" | "loading" | "error";

export interface EmptyStateProps {
  variant: EmptyStateVariant;
  /** Required for "empty"/"error"; ignored for "loading" (a skeleton has no words yet). */
  message?: ReactNode;
}

/**
 * Three treatments for "nothing to show", because one class serving all of
 * them makes a thing still arriving indistinguishable from a thing that
 * isn't there and from a thing that went wrong. Copy rule: literal and
 * declarative, no second person, no flavour verbs — "No combat recorded in
 * this range", never "The mobs grow restless...".
 */
export function EmptyState({ variant, message }: EmptyStateProps) {
  if (variant === "loading") {
    return <div className="empty-loading" aria-busy="true" aria-label="Loading" />;
  }
  if (variant === "error") {
    return (
      <div className="empty-error" role="alert">
        {message}
      </div>
    );
  }
  return <div className="empty">{message}</div>;
}
