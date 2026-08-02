import { useState, type CSSProperties } from "react";
import type { DiscoveredLog, FightInfo, SessionInfo, UpdateMode, UpdateState } from "../api";
import type { BackfillEvent } from "../live";
import { UpdateSettings } from "./UpdateSettings";
import { TimeControls, type ChartSettings } from "../timeControls";
import { frameLabel, isDefaultState, type TimeFrame } from "../timeFrame";
import { LABEL_SIZE_CHOICES } from "../fightOverlay";

interface Props {
  sessions: SessionInfo[];
  activeId: string | null;
  backfill: BackfillEvent | null;
  discovered: DiscoveredLog[];
  update: UpdateState | null;
  onShowUpdatePrompt: () => void;
  onApplyUpdate: () => void;
  onSetUpdateMode: (mode: UpdateMode) => void;
  onCheckForUpdate: () => void;
  /** Transient result of a manual check, e.g. "up to date". */
  checkNote: string | null;
  petRollup: boolean;
  onTogglePetRollup: (on: boolean) => void;
  /** App-wide window/span. Owned here, pushed down to every chart. */
  chartDefaults: ChartSettings;
  onChartDefaults: (next: ChartSettings) => void;
  /** The one time frame, for the readout beside the controls. */
  frame: TimeFrame;
  fights: FightInfo[];
  onResetDefaults: () => void;
  /** Mob-name size on the fight bands; 0 hides them. */
  fightLabelPx: number;
  onFightLabelPx: (px: number) => void;
  onOpen: (path: string) => void;
  onRefreshDiscovered: () => void;
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
  activeId,
  backfill,
  discovered,
  update,
  onShowUpdatePrompt,
  onApplyUpdate,
  onSetUpdateMode,
  onCheckForUpdate,
  checkNote,
  petRollup,
  onTogglePetRollup,
  chartDefaults,
  onChartDefaults,
  frame,
  fights,
  onResetDefaults,
  fightLabelPx,
  onFightLabelPx,
  onOpen,
  onRefreshDiscovered,
  onActivate,
  onClose,
  error,
}: Props) {
  const [path, setPath] = useState("");
  const openPaths = new Set(sessions.map((s) => s.path.toLowerCase()));
  // The demo log (source "sample") is kept apart from the player's real logs:
  // its own labeled dropdown entry, and a badge on its session tab.
  const samplePaths = new Set(
    discovered.filter((d) => d.source === "sample").map((d) => d.path.toLowerCase()),
  );
  const available = discovered.filter(
    (d) => !openPaths.has(d.path.toLowerCase()) && d.source !== "sample",
  );
  const sample = discovered.find(
    (d) => d.source === "sample" && !openPaths.has(d.path.toLowerCase()),
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
      {(available.length > 0 || sample) && (
        <select
          className="detected-select"
          value=""
          onChange={(e) => {
            if (e.target.value) {
              onOpen(e.target.value);
            }
          }}
          title="Log files found from the running game and standard install locations"
        >
          <option value="">Detected logs ({available.length})…</option>
          {available.map((d) => (
            <option key={d.path} value={d.path}>
              {d.character} @{d.server} — {describeAge(d.lastWriteTime)} ({d.source})
            </option>
          ))}
          {sample && (
            <option value={sample.path}>
              {sample.character} — bundled demo data, not yours
            </option>
          )}
        </select>
      )}
      <button className="detect-refresh" title="Re-scan for log files" onClick={onRefreshDiscovered}>
        ↻
      </button>
      <form
        className="open-form"
        onSubmit={(e) => {
          e.preventDefault();
          if (path.trim()) {
            onOpen(path.trim());
            setPath("");
          }
        }}
      >
        <input
          value={path}
          onChange={(e) => setPath(e.target.value)}
          placeholder="C:\EverQuest\Logs\eqlog_Name_server.txt"
          spellCheck={false}
        />
        <button type="submit">Open log</button>
      </form>
      <label
        className="toggle"
        title="Merge each pet's damage and healing into its owner's rows everywhere — a query-time switch, nothing reparses"
      >
        <input
          type="checkbox"
          checked={petRollup}
          onChange={(e) => onTogglePetRollup(e.target.checked)}
        />
        pets → owners
      </label>
      {backfill && !backfill.complete && backfill.totalBytes > 0 && (
        <span className="backfill">
          loading {Math.round((backfill.bytesProcessed / backfill.totalBytes) * 100)}%
        </span>
      )}
      {error && <span className="error">{error}</span>}
      {/* The parent window/span for every chart in the app. It sits up here
          rather than on a panel precisely because it belongs to none of them:
          changing it pushes down and clears any per-panel deviation. */}
      <span className="global-time-controls" title="Rolling window and viewport for every chart">
        <span className="frame-readout" title="What every panel is currently reporting over">
          {frameLabel(frame, fights)}
        </span>
        <TimeControls settings={chartDefaults} bucketSeconds={1} onChange={onChartDefaults} />
        <label className="time-controls" title="Size of the mob names on the fight bands">
          names
          <select
            value={fightLabelPx}
            onChange={(e) => onFightLabelPx(Number(e.target.value))}
          >
            {LABEL_SIZE_CHOICES.map((c) => (
              <option key={c.value} value={c.value}>
                {c.label}
              </option>
            ))}
          </select>
        </label>
        <button
          className="mini-btn"
          onClick={onResetDefaults}
          disabled={isDefaultState(frame, chartDefaults, fightLabelPx)}
          title="Back to the opening state: live, 10 s window, 2 m span"
        >
          reset
        </button>
      </span>
      {update && (
        <span className="version">
          <UpdateSettings
            state={update}
            onSetMode={onSetUpdateMode}
            onCheckNow={onCheckForUpdate}
          />
          {/* On-demand check. Deliberately its own control rather than buried
              in the menu: it is the way back for anyone who chose "don't ask
              again", and it overrides auto mode to ask before installing. */}
          <button
            className="check-btn"
            onClick={onCheckForUpdate}
            disabled={update.stage === "checking"}
            title="Check for updates now"
            aria-label="Check for updates now"
          >
            <span className={update.stage === "checking" ? "check-icon spinning" : "check-icon"}>
              ⟳
            </span>
          </button>
          {checkNote && <span className="check-note">{checkNote}</span>}
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
