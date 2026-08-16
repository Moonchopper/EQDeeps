import type { CSSProperties } from "react";
import type { DiscoveredLog, FightInfo, SessionInfo, UpdateState } from "../api";
import type { BackfillEvent } from "../live";
import { TimeControls, type ChartSettings } from "../timeControls";
import { isDefaultState, type TimeFrame } from "../timeFrame";
import { TimeRangePicker } from "./TimeRangePicker";
import { SelectionChip } from "./SelectionChip";

interface Props {
  sessions: SessionInfo[];
  /** Colour for the selection chip's swatch — the same registry the panels use. */
  colorFor: (key: string, pool: string) => string;
  activeId: string | null;
  backfill: BackfillEvent | null;
  discovered: DiscoveredLog[];
  update: UpdateState | null;
  onShowUpdatePrompt: () => void;
  onApplyUpdate: () => void;
  /** App-wide window/span. Owned here, pushed down to every chart. */
  chartDefaults: ChartSettings;
  onChartDefaults: (next: ChartSettings) => void;
  /** The one time frame, for the readout beside the controls. */
  frame: TimeFrame;
  fights: FightInfo[];
  onResetDefaults: () => void;
  /**
   * Whether the current view reports over the app-wide time frame. The
   * World views (Mobs, Map) do not, and the time controls go with the fight
   * list there: hidden, not disabled — the frame itself stays in force
   * (ADR-014, ADR-017).
   */
  framed: boolean;
  /** Absolute window straight from the picker. */
  onAbsoluteRange: (beginMs: number, endMs: number) => void;
  /** Opens the Logs dialog — the `+` after the session tabs. */
  onOpenLogs: () => void;
  onActivate: (id: string) => void;
  onClose: (id: string) => void;
  error: string | null;
}

export function describeAge(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime();
  const minutes = Math.floor(ms / 60000);
  if (minutes < 2) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

/** Top bar: open a log by path or pick a detected one, switch characters. */
export function SessionBar({
  sessions,
  colorFor,
  activeId,
  backfill,
  discovered,
  update,
  onShowUpdatePrompt,
  onApplyUpdate,
  chartDefaults,
  onChartDefaults,
  frame,
  fights,
  onResetDefaults,
  framed,
  onAbsoluteRange,
  onOpenLogs,
  onActivate,
  onClose,
  error,
}: Props) {
  // The demo log (source "sample") is kept apart from the player's real logs:
  // a badge on its session tab, and its own row in the Logs dialog.
  const samplePaths = new Set(
    discovered.filter((d) => d.source === "sample").map((d) => d.path.toLowerCase()),
  );

  return (
    <header className="session-bar">
      <span className="brand">EQDeeps</span>
      <div className="session-tabs">
        {sessions.map((s) => (
          <span key={s.id} className={"session-tab" + (s.id === activeId ? " on" : "")}>
            <button className="session-name" onClick={() => onActivate(s.id)}>
              {samplePaths.has(s.path.toLowerCase()) && (
                <span className="sample-badge">sample</span>
              )}{" "}
              {s.character} <span className="subtle">@{s.server}</span>
            </button>
            <button className="session-close" title="Close" onClick={() => onClose(s.id)}>
              ×
            </button>
          </span>
        ))}
      </div>
      {/* Opening a log is a start-of-session act, so it gets a `+` where a
          browser puts a new tab and the rest lives in the Logs dialog —
          not a dropdown, a rescan button and a path box in permanent
          residence across the header. */}
      <button className="session-add" onClick={onOpenLogs} title="Open a log" aria-label="Open a log">
        +
      </button>
      <SelectionChip colorFor={colorFor} />
      {backfill && !backfill.complete && backfill.totalBytes > 0 && (
        <span className="backfill">
          loading {Math.round((backfill.bytesProcessed / backfill.totalBytes) * 100)}%
        </span>
      )}
      {error && <span className="error">{error}</span>}
      {/* The parent window/span for every chart in the app. It sits up here
          rather than on a panel precisely because it belongs to none of them:
          changing it pushes down and clears any per-panel deviation. */}
      {framed && (
        <span
          className="global-time-controls"
          title="Rolling window and time range for every chart"
        >
          <TimeRangePicker
            frame={frame}
            spanSec={chartDefaults.spanSec}
            fights={fights}
            onSpan={(span) => onChartDefaults({ ...chartDefaults, spanSec: span })}
            onAbsolute={onAbsoluteRange}
          />
          <TimeControls
            settings={chartDefaults}
            bucketSeconds={1}
            showSpan={false}
            onChange={onChartDefaults}
          />
          <button
            className="mini-btn"
            onClick={onResetDefaults}
            disabled={isDefaultState(frame, chartDefaults)}
            title="Back to the opening state: live, 10 s window, 15 m time range"
          >
            reset
          </button>
        </span>
      )}
      {/* Only the update's live state stays up here — a download in flight,
          a staged install, a failure — because those are things happening,
          not things to set. Preferences and the version moved to Settings. */}
      {update && (
        <span className="version">
          <UpdatePill
            state={update}
            onShowPrompt={onShowUpdatePrompt}
            onApply={onApplyUpdate}
          />
        </span>
      )}
    </header>
  );
}

const mb = (bytes: number) => (bytes / 1_048_576).toFixed(1);

/**
 * The persistent reminder next to the version number. It mirrors whichever
 * stage the update is in, so the download never happens invisibly and a staged
 * install always has a way to be applied on demand.
 */
function UpdatePill({
  state,
  onShowPrompt,
  onApply,
}: {
  state: UpdateState;
  onShowPrompt: () => void;
  onApply: () => void;
}) {
  if (state.stage === "checking") {
    return (
      <span className="update-pill update-pill-quiet" title="Checking for a new release">
        checking…
      </span>
    );
  }

  if (state.stage === "downloading") {
    // The bar is the point: this is a ~60 MB download, and a bare percentage
    // reads as frozen on a slow connection.
    const size = state.downloadSizeBytes
      ? ` · ${mb(state.downloadedBytes)}/${mb(state.downloadSizeBytes)} MB`
      : "";
    return (
      <span
        className="update-pill update-pill-progress"
        style={{ "--pct": `${state.downloadPercent}%` } as CSSProperties}
        title={`Downloading v${state.latestVersion} in the background`}
      >
        <span className="update-pill-label">
          ↓ {state.downloadPercent}%{size}
        </span>
      </span>
    );
  }

  if (state.stage === "failed") {
    // Silence here would be the worst outcome: the user clicked Update and
    // would otherwise see the pill simply vanish.
    return (
      <span
        className="update-pill update-pill-failed"
        title={state.error ?? "The update could not be completed."}
      >
        ! update failed
      </span>
    );
  }

  if (state.restartRequired) {
    return (
      <button
        className="update-pill"
        onClick={onApply}
        title={
          state.requiresElevation
            ? "Installs now — Windows will ask for permission, since EQDeeps lives in a protected folder"
            : "Installs when you close EQDeeps. Click to do it now."
        }
      >
        <span className="update-star">★</span> restart to update
      </button>
    );
  }

  if (state.latestVersion && state.stage === "available") {
    return (
      <button
        className="update-pill"
        onClick={onShowPrompt}
        title="A new release is available"
      >
        <span className="update-star">★</span> v{state.latestVersion} available
      </button>
    );
  }

  return null;
}
