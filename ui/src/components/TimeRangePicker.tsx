import { useEffect, useRef, useState } from "react";
import type { FightInfo } from "../api";
import { fmtDuration, parseDuration, spanChoices, type Span } from "../timeControls";
import { frameLabel, type TimeFrame } from "../timeFrame";

interface Props {
  frame: TimeFrame;
  spanSec: Span;
  fights: FightInfo[];
  /** A trailing window: the last N seconds, or everything. */
  onSpan: (span: Span) => void;
  /** A fixed window between two instants. */
  onAbsolute: (beginMs: number, endMs: number) => void;
}

/** `datetime-local` speaks local wall-clock parts, which is also what we mean. */
function toLocalInput(ms: number): string {
  const d = new Date(ms);
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` +
    `T${pad(d.getHours())}:${pad(d.getMinutes())}`
  );
}

/**
 * The time range control: quick picks, a typed relative window, and an
 * absolute from/to.
 *
 * A dropdown of fixed spans only answers "the last N" for the handful of N
 * someone thought of. "-500m" and "between 18:04 and 18:12 last Tuesday" are
 * both ordinary things to want and neither fits a list, so the list becomes
 * the fast path rather than the only path.
 */
export function TimeRangePicker({ frame, spanSec, fights, onSpan, onAbsolute }: Props) {
  const [open, setOpen] = useState(false);
  const [relative, setRelative] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const rootRef = useRef<HTMLSpanElement>(null);

  // Seed the absolute fields from whatever is framed now, so the panel opens
  // on the current window rather than on nothing.
  useEffect(() => {
    if (!open) return;
    const end = frame.kind === "range" ? new Date(frame.end).getTime() : Date.now();
    const begin =
      frame.kind === "range"
        ? new Date(frame.begin).getTime()
        : end - (spanSec === "fit" ? 3600 : spanSec) * 1000;
    setFrom(toLocalInput(begin));
    setTo(toLocalInput(end));
    setRelative("");
  }, [open, frame, spanSec]);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const parsed = parseDuration(relative);
  const applyRelative = () => {
    if (parsed === null) return;
    onSpan(parsed);
    setOpen(false);
  };

  const applyAbsolute = () => {
    const beginMs = new Date(from).getTime();
    const endMs = new Date(to).getTime();
    if (!Number.isFinite(beginMs) || !Number.isFinite(endMs) || beginMs === endMs) return;
    onAbsolute(beginMs, endMs);
    setOpen(false);
  };

  return (
    <span className="range-picker" ref={rootRef}>
      {/* Named like every other control in this bar. Without it the trigger is
          an unlabelled chip reading "15m", and there is nothing to say what it
          governs until a fixed range makes the text self-describing. */}
      <span className="range-label">time range</span>
      <button
        className="range-trigger"
        onClick={() => setOpen((o) => !o)}
        title="Time range: quick picks, a typed window like -6h or 90m, or exact from/to"
      >
        {/* And say which kind of window it is: a trailing one reads "last 15m",
            a fixed one names when it starts and how long it runs. */}
        {frame.kind === "range"
          ? frameLabel(frame, fights)
          : spanSec === "fit"
            ? "everything"
            : `last ${fmtDuration(spanSec)}`}
        <span className="range-caret">▾</span>
      </button>

      {open && (
        <div className="range-menu">
          <div className="range-section">quick</div>
          <div className="range-quick">
            {spanChoices(1).map((c) => (
              <button
                key={String(c.value)}
                className={
                  "range-chip" + (frame.kind !== "range" && c.value === spanSec ? " on" : "")
                }
                onClick={() => {
                  onSpan(c.value);
                  setOpen(false);
                }}
              >
                {c.label}
              </button>
            ))}
          </div>

          <div className="range-section">last</div>
          <div className="range-row">
            <input
              value={relative}
              onChange={(e) => setRelative(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && applyRelative()}
              placeholder="-6h · 20m · 500m · 1h30m"
              spellCheck={false}
              aria-label="Relative time range"
            />
            <button className="mini-btn" onClick={applyRelative} disabled={parsed === null}>
              set
            </button>
          </div>
          {relative.trim().length > 0 && (
            <div className={parsed === null ? "range-note bad" : "range-note"}>
              {parsed === null ? "needs a unit — s, m, h or d" : `last ${fmtDuration(parsed)}`}
            </div>
          )}

          <div className="range-section">between</div>
          <div className="range-row">
            <input
              type="datetime-local"
              value={from}
              onChange={(e) => setFrom(e.target.value)}
              aria-label="Range start"
            />
          </div>
          <div className="range-row">
            <input
              type="datetime-local"
              value={to}
              onChange={(e) => setTo(e.target.value)}
              aria-label="Range end"
            />
            <button className="mini-btn" onClick={applyAbsolute}>
              set
            </button>
          </div>
        </div>
      )}
    </span>
  );
}
