import { useEffect, useState, type ReactNode } from "react";
import type { DiscoveredLog, SessionInfo } from "../api";
import { describeAge } from "./SessionBar";

interface PickerProps {
  discovered: DiscoveredLog[];
  sessions: SessionInfo[];
  onOpen: (path: string) => void;
  onActivate: (sessionId: string) => void;
  onForget: (path: string) => void;
  /** Whether the free-text path form is shown under the list. */
  withPathForm?: boolean;
}

/**
 * How a discovered log is grouped for people. The server labels each by
 * where it found it (`LogDiscovery`); this folds those into the four
 * distinctions a player actually makes — is the game writing it now, is it in
 * an install, did I open it before, or is it the demo.
 */
function groupOf(source: string): string {
  switch (source) {
    case "running EverQuest":
      return "Running now";
    case "registry":
    case "known install path":
      return "Installed";
    case "recent":
      return "Recent";
    default:
      return "Detected";
  }
}

const GROUP_ORDER = ["Running now", "Recent", "Installed", "Detected"];

/**
 * Every log EQDeeps can see, grouped by how it knows about it, plus a box for
 * one it cannot. Shared by the welcome screen and the Logs dialog so the two
 * cannot drift: there is one idea of "a log you could open", and this is it.
 *
 * A log that is already open is listed too, marked, and clicking it switches
 * to that session — hiding it, as the old header dropdown did, made "why isn't
 * my character in this list" a question with an invisible answer.
 */
export function LogPicker({
  discovered,
  sessions,
  onOpen,
  onActivate,
  onForget,
  withPathForm = true,
}: PickerProps) {
  const [path, setPath] = useState("");
  const openByPath = new Map(sessions.map((s) => [s.path.toLowerCase(), s.id]));
  const sample = discovered.find((d) => d.source === "sample");
  const real = discovered.filter((d) => d.source !== "sample");

  const groups = new Map<string, DiscoveredLog[]>();
  for (const d of real) {
    const g = groupOf(d.source);
    groups.set(g, [...(groups.get(g) ?? []), d]);
  }

  const row = (d: DiscoveredLog, extra?: ReactNode) => {
    const openId = openByPath.get(d.path.toLowerCase());
    return (
      <div key={d.path} className="discovered-item">
        <button
          className={"discovered-row" + (d.source === "sample" ? " sample-row" : "")}
          onClick={() => (openId ? onActivate(openId) : onOpen(d.path))}
          title={openId ? "Already open — switch to it" : d.path}
        >
          <span className="discovered-name">
            {d.source === "sample" && <span className="sample-badge">sample</span>}{" "}
            {d.character} <span className="subtle">@{d.server}</span>
            {openId && <span className="discovered-open">open</span>}
          </span>
          <span className="discovered-meta">
            {d.source === "sample"
              ? "two days of real gameplay bundled with EQDeeps — not your data"
              : `last written ${describeAge(d.lastWriteTime)}`}{" "}
            · {(d.sizeBytes / 1048576).toFixed(1)} MB
          </span>
          {extra}
        </button>
        {/* Only "recent" rows can be forgotten: the others come from scanning
            the install, so they would reappear at the next scan. */}
        {d.source === "recent" && (
          <button
            className="discovered-forget"
            title="Remove from this list (the log file is not deleted)"
            aria-label={`Remove ${d.character} from recent logs`}
            onClick={() => onForget(d.path)}
          >
            ✕
          </button>
        )}
      </div>
    );
  };

  return (
    <div className="log-picker">
      {GROUP_ORDER.filter((g) => groups.has(g)).map((g) => (
        <section key={g} className="log-group">
          <h3 className="log-group-heading">{g}</h3>
          <div className="discovered-list">
            {groups.get(g)!.map((d) => row(d, <span className="discovered-path">{d.path}</span>))}
          </div>
        </section>
      ))}
      {real.length === 0 && (
        <p className="subtle">
          No EverQuest logs found. Logging must be on in game (<code>/log</code>); if EverQuest is
          running, rescan, or paste the path to a log below.
        </p>
      )}
      {withPathForm && (
        <form
          className="log-open-form"
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
            aria-label="Path to a log file"
          />
          <button type="submit" disabled={!path.trim()}>
            Open log
          </button>
        </form>
      )}
      {sample && (
        <section className="log-group sample-callout">
          <h3 className="log-group-heading">Sample</h3>
          <div className="discovered-list">{row(sample)}</div>
        </section>
      )}
    </div>
  );
}

interface DialogProps extends PickerProps {
  onClose: () => void;
  onRescan: () => void;
}

/**
 * The Logs dialog: the picker in a modal, opened from the rail's utility
 * cluster or the `+` beside the session tabs. It replaces three header
 * controls — a dropdown that flattened every source into one list, a rescan
 * button, and a path box — that between them were most of the header's width
 * and only ever used at the start of a session.
 */
export function LogsDialog({ onClose, onRescan, ...picker }: DialogProps) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div
        className="modal logs-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="logs-title"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="modal-title settings-title">
          <span id="logs-title">Logs</span>
          <span className="logs-actions">
            <button className="mini-btn" onClick={onRescan} title="Re-scan for log files">
              ↻ rescan
            </button>
            <button className="mini-btn" onClick={onClose} aria-label="Close">
              close
            </button>
          </span>
        </div>
        <LogPicker {...picker} />
      </div>
    </div>
  );
}
