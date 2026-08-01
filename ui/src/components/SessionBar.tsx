import { useState } from "react";
import type { DiscoveredLog, SessionInfo, VersionInfo } from "../api";
import type { BackfillEvent } from "../live";

interface Props {
  sessions: SessionInfo[];
  activeId: string | null;
  backfill: BackfillEvent | null;
  discovered: DiscoveredLog[];
  version: VersionInfo | null;
  petRollup: boolean;
  onTogglePetRollup: (on: boolean) => void;
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
  version,
  petRollup,
  onTogglePetRollup,
  onOpen,
  onRefreshDiscovered,
  onActivate,
  onClose,
  error,
}: Props) {
  const [path, setPath] = useState("");
  const openPaths = new Set(sessions.map((s) => s.path.toLowerCase()));
  const available = discovered.filter((d) => !openPaths.has(d.path.toLowerCase()));

  return (
    <header className="session-bar">
      <span className="brand">EQDeeps</span>
      <div className="session-tabs">
        {sessions.map((s) => (
          <span key={s.id} className={"session-tab" + (s.id === activeId ? " on" : "")}>
            <button className="session-name" onClick={() => onActivate(s.id)}>
              {s.character} <span className="subtle">@{s.server}</span>
            </button>
            <button className="session-close" title="Close" onClick={() => onClose(s.id)}>
              ×
            </button>
          </span>
        ))}
      </div>
      {available.length > 0 && (
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
      {version && (
        <span className="version">
          v{version.version}
          {version.updateAvailable && version.releaseUrl && (
            <>
              {" · "}
              <a href={version.releaseUrl} target="_blank" rel="noreferrer">
                v{version.latestVersion} available ↗
              </a>
            </>
          )}
        </span>
      )}
    </header>
  );
}
