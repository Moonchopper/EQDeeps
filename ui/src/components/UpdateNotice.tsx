import { useState } from "react";
import type { DeferScope, UpdateState } from "../api";

/**
 * What the user told us. "always" rides along with an update so the checkbox
 * and the button are a single decision rather than two clicks.
 */
export type UpdateChoice =
  | { kind: "update"; always: boolean; now: boolean }
  | { kind: "defer"; scope: DeferScope };

/**
 * Compress GitHub's auto-generated release notes ("* Title by @user in URL"
 * under a "## What's Changed" heading) into a short plain-text bullet list;
 * hand-written notes fall back to their first non-heading lines.
 */
export function shortChangelog(notes: string): string[] {
  const lines = notes.split("\n").map((l) => l.trim());
  const bullets = lines
    .filter((l) => l.startsWith("* ") || l.startsWith("- "))
    .map((l) =>
      l
        .slice(2)
        .replace(/ by @\S+ in \S+$/, "")
        .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")
        .trim(),
    )
    .filter((l) => l.length > 0 && !l.startsWith("**Full Changelog"));
  const chosen = bullets.length > 0
    ? bullets
    : lines.filter((l) => l.length > 0 && !l.startsWith("#") && !l.startsWith("**Full Changelog"));
  return chosen.slice(0, 6);
}

/**
 * The consent dialog. Every way of saying "no" is on screen at once and says
 * plainly how long it lasts — the alternative (one "Later" button whose
 * meaning you have to guess) is what makes update prompts feel like nagging.
 *
 * A backdrop click maps to the mildest decline, "not right now", so dismissing
 * by reflex never silences anything permanently.
 */
export function UpdateNotice({
  state,
  onChoice,
}: {
  state: UpdateState;
  onChoice: (choice: UpdateChoice) => void;
}) {
  const [always, setAlways] = useState(false);
  const items = shortChangelog(state.releaseNotes ?? "");
  const decline = (scope: DeferScope) => () => onChoice({ kind: "defer", scope });

  return (
    <div className="modal-backdrop" onClick={decline("once")}>
      <div className="modal update-notice" onClick={(e) => e.stopPropagation()}>
        <div className="modal-title">
          <span className="update-star">★</span> EQDeeps v{state.latestVersion} is available
        </div>
        <p className="update-sub">You're running v{state.version}. What's new:</p>
        {items.length > 0 ? (
          <ul className="update-changelog">
            {items.map((line, i) => (
              <li key={i}>{line}</li>
            ))}
          </ul>
        ) : (
          <p className="update-sub">See the release page for details.</p>
        )}

        {state.canSelfInstall ? (
          <>
            <p className="update-when">
              {state.restartRequired
                ? "Already downloaded and waiting. Restart now to install it, or it installs the next time you close EQDeeps."
                : "Restart now and it installs straight away — the log is back from the cache in seconds — or update on exit and it installs the next time you close EQDeeps."}
            </p>
            <label className="update-always">
              <input
                type="checkbox"
                checked={always}
                onChange={(e) => setAlways(e.target.checked)}
              />
              Update automatically from now on
            </label>
            <div className="update-declines">
              <button className="link-btn" onClick={decline("once")}>
                Not right now
              </button>
              <button
                className="link-btn"
                onClick={decline("release")}
                title="Stay quiet until a release newer than this one ships"
              >
                Skip this version
              </button>
              <button
                className="link-btn"
                onClick={decline("currentVersion")}
                title={`Stop asking while you're running v${state.version}`}
              >
                Don't ask again for v{state.version}
              </button>
            </div>
            <div className="modal-actions">
              {/* Two ways to say yes, differing only in when the restart
                  happens. "Restart now" is the primary button: the prompt
                  only appears between fights (or when you asked for it), a
                  restart resumes from the log cache in seconds, and the
                  owner's own answer was "now" every time. "On exit" stays for
                  whoever would rather not blink at all. */}
              <button
                className="mini-btn"
                onClick={() => onChoice({ kind: "update", always, now: false })}
                title="Download now, install when you next close EQDeeps"
              >
                {state.restartRequired ? "Install on exit" : "Update on exit"}
              </button>
              <button
                className="update-download"
                onClick={() => onChoice({ kind: "update", always, now: true })}
                title="Install straight away — EQDeeps closes and reopens on the new version"
              >
                Update &amp; restart now
              </button>
            </div>
          </>
        ) : (
          // Portable and source builds have nothing to install into, so the
          // honest offer is the download page — the pre-auto-update behaviour.
          <div className="modal-actions">
            <button className="mini-btn" onClick={decline("once")}>
              Later
            </button>
            {state.releaseUrl && (
              <a
                className="update-download"
                href={state.releaseUrl}
                target="_blank"
                rel="noreferrer"
                onClick={decline("once")}
              >
                Download ↗
              </a>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
