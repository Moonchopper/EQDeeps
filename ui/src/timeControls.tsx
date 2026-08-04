/**
 * The window/span vocabulary shared by every time chart in the app — the
 * Overview DPS chart and the time panels on the standard views alike. Both
 * build their choice lists from here so the two can never drift into offering
 * different options for the same idea.
 *
 *  window     — width of the rolling mean.
 *  time range — how much time is on screen and reported over; "fit" is
 *               everything there is. Called `spanSec` in the code, since a
 *               fixed range picked off a chart or the fight list is a
 *               different thing (see TimeFrame).
 *
 * Both ladders are expressed as MULTIPLES OF THE BUCKET, not fixed seconds: a
 * 5-second window means nothing on a chart bucketed at a minute. At the
 * 1-second bucket every other chart uses, `windowChoices` reproduces the DPS
 * chart's original 1/3/5/10/30/60 s list exactly.
 *
 * The window is therefore STORED as that multiple — a bucket count — and only
 * rendered in seconds. It used to be stored in seconds, which quietly meant two
 * different things depending on which control wrote it: the top bar sits at the
 * 1-second bucket, so its "10" was ten buckets, while a minute-bucketed panel's
 * own ladder wrote 600 for the same ten. Every chart then divided by its own
 * bucket to get a ring length, so the minute-bucketed charts (XP, faction,
 * coin) turned every setting under 90 s into a single bucket and appeared to
 * ignore the control entirely. A count cannot be misread that way.
 */

export type Span = number | "fit";

const WINDOW_MULTIPLES = [1, 3, 5, 10, 30, 60];
// Reaches from half a minute to a day at the 1-second bucket, so a window can
// be picked outright rather than only by selecting fights.
const SPAN_MULTIPLES = [30, 60, 120, 300, 900, 1800, 3600, 7200, 21600, 86400];

export function fmtDuration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${+(seconds / 60).toFixed(seconds % 60 ? 1 : 0)}m`;
  return `${+(seconds / 3600).toFixed(seconds % 3600 ? 1 : 0)}h`;
}

/** The window ladder for a chart at this bucket: counts, labelled in seconds. */
export function windowChoices(bucketSeconds: number): { value: number; label: string }[] {
  const bucket = Math.max(1, bucketSeconds);
  return WINDOW_MULTIPLES.map((m) => ({ value: m, label: fmtDuration(m * bucket) }));
}

/** How long a window actually is, on a chart aggregated at this bucket. */
export function windowSeconds(windowBuckets: number, bucketSeconds: number): number {
  return Math.max(1, windowBuckets) * Math.max(1, bucketSeconds);
}

export function spanChoices(bucketSeconds: number): { value: Span; label: string }[] {
  const bucket = Math.max(1, bucketSeconds);
  return [
    { value: "fit" as Span, label: "fit" },
    ...SPAN_MULTIPLES.map((m) => ({ value: m * bucket as Span, label: fmtDuration(m * bucket) })),
  ];
}

export interface ChartSettings {
  /** Rolling-mean width, in BUCKETS — see the note above on why not seconds. */
  windowBuckets: number;
  spanSec: Span;
}

/**
 * THE default for every time chart in the app — Summary's DPS chart and every
 * standard view's panels alike. It is deliberately the only one: window and
 * span are presentation, not properties of a panel, so no panel definition
 * carries its own. The top-bar control seeds from this and pushes any change
 * down to every chart.
 */
export const DEFAULT_CHART_SETTINGS: ChartSettings = { windowBuckets: 10, spanSec: 900 };

interface Props {
  settings: ChartSettings;
  bucketSeconds: number;
  /** Panel headers still set their own range; the top bar uses the picker. */
  showSpan?: boolean;
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
export function TimeControls({
  settings,
  bucketSeconds,
  showSpan,
  onChange,
  onApplyToAll,
}: Props) {
  // A stored count that isn't on the ladder still has to be selectable, or the
  // select would silently snap it to something else.
  const ladder = windowChoices(bucketSeconds);
  const windows = ladder.some((w) => w.value === settings.windowBuckets)
    ? ladder
    : [...ladder, {
        value: settings.windowBuckets,
        label: fmtDuration(windowSeconds(settings.windowBuckets, bucketSeconds)),
      }].sort((a, b) => a.value - b.value);
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
          value={settings.windowBuckets}
          onChange={(e) => onChange({ ...settings, windowBuckets: Number(e.target.value) })}
        >
          {windows.map((w) => (
            <option key={w.value} value={w.value}>
              {w.label}
            </option>
          ))}
        </select>
      </label>
      {showSpan !== false && (
        <label>
          time range
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
      )}
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

/**
 * Parses a relative duration into seconds: "6h", "-20m", "500m", "1h30m",
 * "90s", "2d". A leading minus is accepted and ignored — "-6h" and "6h" both
 * mean "the last six hours", and reading the sign literally would produce a
 * window running backwards.
 *
 * Units are required. A bare "500" could be seconds or minutes with equal
 * plausibility, and guessing wrong silently gives a window 60x off.
 */
export function parseDuration(text: string): number | null {
  const cleaned = text.trim().toLowerCase().replace(/^(last|past)\s+/, "").replace(/^-/, "");
  if (cleaned.length === 0) {
    return null;
  }

  const units: Record<string, number> = { s: 1, m: 60, h: 3600, d: 86400 };
  const pattern = /(\d+(?:\.\d+)?)\s*([smhd])/g;
  let total = 0;
  let matchedTo = 0;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(cleaned)) !== null) {
    if (cleaned.slice(matchedTo, match.index).trim().length !== 0) {
      return null; // something other than spacing between the parts
    }
    total += Number(match[1]) * units[match[2]];
    matchedTo = pattern.lastIndex;
  }

  // Every character had to belong to a part, and the total has to be usable.
  const trailing = cleaned.slice(matchedTo).trim();
  return trailing.length === 0 && total > 0 ? Math.round(total) : null;
}
