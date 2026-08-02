/**
 * The window/span vocabulary shared by every time chart in the app — the
 * Overview DPS chart and the time panels on the standard views alike. Both
 * build their choice lists from here so the two can never drift into offering
 * different options for the same idea.
 *
 *  window — width of the rolling mean.
 *  span   — width of the visible viewport; "fit" shows everything there is.
 *
 * Both ladders are expressed as MULTIPLES OF THE BUCKET, not fixed seconds: a
 * 5-second window means nothing on a chart bucketed at a minute. At the
 * 1-second bucket every other chart uses, `windowChoices` reproduces the DPS
 * chart's original 1/3/5/10/30/60 s list exactly.
 */

export type Span = number | "fit";

const WINDOW_MULTIPLES = [1, 3, 5, 10, 30, 60];
const SPAN_MULTIPLES = [30, 60, 120, 300, 900, 3600];

export function fmtDuration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${+(seconds / 60).toFixed(seconds % 60 ? 1 : 0)}m`;
  return `${+(seconds / 3600).toFixed(seconds % 3600 ? 1 : 0)}h`;
}

export function windowChoices(bucketSeconds: number): number[] {
  const bucket = Math.max(1, bucketSeconds);
  return WINDOW_MULTIPLES.map((m) => m * bucket);
}

export function spanChoices(bucketSeconds: number): { value: Span; label: string }[] {
  const bucket = Math.max(1, bucketSeconds);
  return [
    { value: "fit" as Span, label: "fit" },
    ...SPAN_MULTIPLES.map((m) => ({ value: m * bucket as Span, label: fmtDuration(m * bucket) })),
  ];
}

export interface ChartSettings {
  windowSec: number;
  spanSec: Span;
}

interface Props {
  settings: ChartSettings;
  bucketSeconds: number;
  onChange: (next: ChartSettings) => void;
  /** Omitted when there is no other time chart to apply to. */
  onApplyToAll?: () => void;
}

/**
 * Panel-header controls for one time chart. "apply to all" copies this
 * chart's settings onto every other time chart in the same view — the fast
 * path when you want the whole view on one footing rather than tuning each
 * chart in turn.
 */
export function TimeControls({ settings, bucketSeconds, onChange, onApplyToAll }: Props) {
  // A stored value that isn't on the ladder (an older panel, or a setting
  // copied from a chart with a different bucket) still has to be selectable,
  // or the select would silently snap it to something else.
  const windows = [...new Set([...windowChoices(bucketSeconds), settings.windowSec])].sort(
    (a, b) => a - b,
  );
  const spans = spanChoices(bucketSeconds);
  if (settings.spanSec !== "fit" && !spans.some((s) => s.value === settings.spanSec)) {
    spans.push({ value: settings.spanSec, label: fmtDuration(settings.spanSec as number) });
    spans.sort((a, b) => (a.value === "fit" ? -1 : b.value === "fit" ? 1 : a.value - b.value));
  }

  return (
    <span className="time-controls">
      <label>
        window
        <select
          value={settings.windowSec}
          onChange={(e) => onChange({ ...settings, windowSec: Number(e.target.value) })}
        >
          {windows.map((w) => (
            <option key={w} value={w}>
              {fmtDuration(w)}
            </option>
          ))}
        </select>
      </label>
      <label>
        span
        <select
          value={String(settings.spanSec)}
          onChange={(e) =>
            onChange({
              ...settings,
              spanSec: e.target.value === "fit" ? "fit" : Number(e.target.value),
            })
          }
        >
          {spans.map((s) => (
            <option key={String(s.value)} value={String(s.value)}>
              {s.label}
            </option>
          ))}
        </select>
      </label>
      {onApplyToAll && (
        <button
          className="mini-btn"
          onClick={onApplyToAll}
          title="Copy this window and span to every other time chart in this view"
        >
          apply to all
        </button>
      )}
    </span>
  );
}
