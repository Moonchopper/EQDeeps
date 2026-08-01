import { useState } from "react";
import type { SessionInfo } from "../api";
import type { BackfillEvent } from "../live";

interface Props {
  sessions: SessionInfo[];
  activeId: string | null;
  backfill: BackfillEvent | null;
  onOpen: (path: string) => void;
  onActivate: (id: string) => void;
  onClose: (id: string) => void;
  error: string | null;
}

/** Top bar: open a log by path, switch between monitored characters. */
export function SessionBar({ sessions, activeId, backfill, onOpen, onActivate, onClose, error }: Props) {
  const [path, setPath] = useState("");

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
      {backfill && !backfill.complete && backfill.totalBytes > 0 && (
        <span className="backfill">
          loading {Math.round((backfill.bytesProcessed / backfill.totalBytes) * 100)}%
        </span>
      )}
      {error && <span className="error">{error}</span>}
    </header>
  );
}
