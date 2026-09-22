import { useCallback, useEffect, useRef, useState } from "react";
import { api, type ReferenceStatus } from "../api";
import { fmtCount } from "../components/SlayerPanel";

/**
 * Reads <c>/api/reference/status</c> once, and keeps polling every 2s for as long as a Refresh is
 * running — the one place this loop is written, shared by the Slayer hunt panel's footer
 * (`HuntDetail`) and the Settings dialog's reference row, so it is not written twice (ADR-020
 * Decision 1's amendment, brief "ship the reference snapshot"). `refresh()` is this app's only path
 * to eqlbase.com: everything else the UI shows about the reference layer comes from the shipped
 * snapshot or the cache, read locally by the server.
 *
 * <p>`enabled` mirrors `useReferenceEnabled()` — when the player has switched "Look mobs up online"
 * off, this polls nothing and offers nothing, so the promise that off means off holds even though
 * `/api/reference/status` itself never reaches the network (it only ever reads local state).</p>
 */
export function useReferenceStatus(enabled: boolean): {
  status: ReferenceStatus | null;
  error: string | null;
  refresh: () => void;
} {
  const [status, setStatus] = useState<ReferenceStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<number | undefined>(undefined);

  const poll = useCallback(() => {
    window.clearTimeout(timer.current);
    api
      .referenceStatus()
      .then((s) => {
        setStatus(s);
        setError(null);
        if (s.refresh.running) {
          timer.current = window.setTimeout(poll, 2000);
        }
      })
      .catch(() => setError("Could not read the reference layer."));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!enabled) {
      window.clearTimeout(timer.current);
      return;
    }
    poll();
    return () => window.clearTimeout(timer.current);
  }, [enabled, poll]);

  const refresh = useCallback(() => {
    api
      .startRefresh()
      .then(poll)
      .catch(() => setError("Could not start the refresh."));
  }, [poll]);

  return { status, error, refresh };
}

function fmtSnapshotDate(iso: string): string {
  return new Date(iso).toLocaleDateString([], { day: "numeric", month: "short", year: "numeric" });
}

/** "today", "1 day ago", "3 days ago" — the resolution "refreshed …" needs; the tooltip carries the exact moment. */
function fmtAgo(iso: string): string {
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86400000);
  if (days <= 0) return "today";
  return days === 1 ? "1 day ago" : `${days} days ago`;
}

/**
 * The facts-and-button half of a reference-refresh line: "snapshot of 21 Sep 2026" or "refreshed 3
 * days ago" (absolute time in the tooltip), then either the Refresh button, a running "checking 12
 * of 80…", or a just-finished "checked 80 · 3 changed" beside a fresh button. An error from either
 * this call or the store's own last attempt is shown plainly. The caller supplies whatever precedes
 * this (a source link, a label) — see `HuntDetail`'s footer and `SettingsDialog`'s reference row for
 * the two different prefixes this is built to sit beside.
 */
export function ReferenceRefreshFacts({
  status,
  error,
  onRefresh,
  buttonLabel = "Refresh",
  snapshotSuffix,
}: {
  status: ReferenceStatus;
  error: string | null;
  onRefresh: () => void;
  buttonLabel?: string;
  /** Trails "snapshot of 21 Sep 2026" only — once a Refresh has actually happened it is no longer true, so it never follows "refreshed … ago". */
  snapshotSuffix?: string;
}) {
  const { refresh } = status;
  const fact = status.refreshedUtc ? (
    <span title={new Date(status.refreshedUtc).toLocaleString()}>refreshed {fmtAgo(status.refreshedUtc)}</span>
  ) : status.snapshotUtc ? (
    <span>
      snapshot of {fmtSnapshotDate(status.snapshotUtc)}
      {snapshotSuffix ? ` ${snapshotSuffix}` : ""}
    </span>
  ) : null;
  const problem = error ?? refresh.error;

  return (
    <span className="reference-refresh">
      {fact}
      {fact && " · "}
      {refresh.running ? (
        <span className="subtle">
          checking {fmtCount(refresh.filesChecked)} of {fmtCount(refresh.filesTotal)}…
        </span>
      ) : (
        <>
          <button className="mini-btn" onClick={onRefresh}>
            {buttonLabel}
          </button>
          {refresh.finishedUtc && (
            <span className="subtle">
              {" "}
              checked {fmtCount(refresh.filesChecked)} · {fmtCount(refresh.filesChanged)} changed
            </span>
          )}
        </>
      )}
      {problem && <span className="reference-refresh-error"> {problem}</span>}
    </span>
  );
}
