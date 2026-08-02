import type { VersionInfo } from "../api";

const DISMISS_KEY = "eqdeeps.dismissedUpdate";

/** True when this release hasn't been dismissed before (once per version). */
export function shouldAnnounce(version: VersionInfo): boolean {
  return (
    version.updateAvailable &&
    !!version.latestVersion &&
    localStorage.getItem(DISMISS_KEY) !== version.latestVersion
  );
}

export function markAnnounced(version: VersionInfo): void {
  if (version.latestVersion) {
    localStorage.setItem(DISMISS_KEY, version.latestVersion);
  }
}

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

/** One-time-per-version popup: what's new, and where to get it. */
export function UpdateNotice({ version, onDismiss }: { version: VersionInfo; onDismiss: () => void }) {
  const items = shortChangelog(version.releaseNotes ?? "");
  return (
    <div className="modal-backdrop" onClick={onDismiss}>
      <div className="modal update-notice" onClick={(e) => e.stopPropagation()}>
        <div className="modal-title">
          <span className="update-star">★</span> EQDeeps v{version.latestVersion} is available
        </div>
        <p className="update-sub">You're running v{version.version}. What's new:</p>
        {items.length > 0 ? (
          <ul className="update-changelog">
            {items.map((line, i) => (
              <li key={i}>{line}</li>
            ))}
          </ul>
        ) : (
          <p className="update-sub">See the release page for details.</p>
        )}
        <div className="modal-actions">
          <button className="mini-btn" onClick={onDismiss}>
            Later
          </button>
          {version.releaseUrl && (
            <a
              className="update-download"
              href={version.releaseUrl}
              target="_blank"
              rel="noreferrer"
              onClick={onDismiss}
            >
              Download ↗
            </a>
          )}
        </div>
      </div>
    </div>
  );
}
