import type { CSSProperties, ReactNode } from "react";

export type StatusBadgeVariant =
  | "sample"
  | "update"
  | "update-quiet"
  | "update-progress"
  | "update-failed"
  | "live";

export interface StatusBadgeProps {
  variant: StatusBadgeVariant;
  children: ReactNode;
  /** 0-100. Only meaningful for "update-progress" — drives the pill's own fill. */
  progress?: number;
}

const VARIANT_CLASS: Record<StatusBadgeVariant, string> = {
  sample: "sample-badge",
  update: "update-pill",
  "update-quiet": "update-pill update-pill-quiet",
  "update-progress": "update-pill update-pill-progress",
  "update-failed": "update-pill update-pill-failed",
  live: "status-badge-live",
};

/**
 * The kit's small labeled status marks — never text-sized, always a compact
 * inline mark beside the thing it describes. "sample" is the dashed-violet
 * treatment that keeps a demo/preview log from ever reading as a real one;
 * the "update" family is a determinate-progress pill (see the
 * update-progress story for the fill); "live" is a standalone version of the
 * pulsing-row treatment for anywhere that needs a compact "in progress" mark.
 */
export function StatusBadge({ variant, children, progress }: StatusBadgeProps) {
  const style: CSSProperties | undefined =
    variant === "update-progress" ? ({ "--pct": `${progress ?? 0}%` } as CSSProperties) : undefined;
  const content = variant === "update-progress" ? <span className="update-pill-label">{children}</span> : children;

  return (
    <span className={VARIANT_CLASS[variant]} style={style}>
      {content}
    </span>
  );
}
