import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import { IconRefresh } from "@tabler/icons-react";
import {
  api,
  type SlayerKillAchievement,
  type SlayerKillComponent,
  type SlayerMetaAchievement,
  type SlayerReport,
} from "../api";
import { NAME_SORT, SortHeader, TableSearch, type SortState } from "../dashboards/tableTools";
import type { BestiaryTarget, MapTarget } from "../trail";
import { HuntDetail, type HuntSelection } from "./slayer/HuntDetail";

// Kill counts are read against the game's own achievement window, which
// prints them in full — so they are never rounded to K the way every damage
// number in the app is (fmtNum). "570 / 1,000", not "570 / 1.0K": a player
// thirty kills short of a Conquest wants to see thirty, not "5.0K / 5.0K".
// Exported so the hunt detail (HuntDetail.tsx) reads the same rule for the
// zone/faction numbers beside it — one count style on the page, not two.
export const fmtCount = (value: number): string => Math.round(value).toLocaleString("en-US");

// The open "Where to hunt" selection per session id, outliving the panel
// (see where huntSel is declared for why). Module state, not storage: it
// should not survive a restart.
const lastHuntSelection = new Map<string, HuntSelection>();

/**
 * The Slayer view (F35 slice 1, ADR-023): every "kill N of this creature
 * type" achievement, read from the player's own achievements export, nearest
 * to done first.
 *
 * <p><b>The export is the count, and the app never keeps its own</b> (ADR-023
 * Decision 1). The log never names a corpse's race, so any tally this app
 * built from the log would be a guess wearing the game's number. Every
 * `have`/`need`/`complete` on this page is exactly what
 * `/outputfile achievements` last wrote — stamped with the file's age — and
 * the only way to move the numbers is to run the command again and hit
 * Refresh. A later slice may show a log-derived estimate of what happened
 * <em>since</em> the export, but it will sit beside this number, never inside
 * it (the rule ADR-020 set for listed-vs-measured).</p>
 *
 * <p>Unlike the Bestiary and the Map, this view carries no place of its own —
 * a kill achievement is not a zone or a mob — so it self-reports nothing to
 * the history and needs no crumb trail. Its "Where to hunt" detail (slice 2,
 * ADR-023 Decisions 4-7) does open the Map and the Bestiary — every zone and
 * every mob it names is a door — but through plain callbacks rather than the
 * shared crumb trail (see App.tsx's Slayer render site for why).</p>
 */
export function SlayerPanel({
  sessionId,
  referenceEnabled,
  onShowOnMap,
  onOpenMob,
}: {
  sessionId: string | null;
  /** The client-side "Look mobs up online" switch (ADR-020); passed through to the hunt detail. */
  referenceEnabled: boolean;
  /** To the Map, from a zone door inside the hunt detail. */
  onShowOnMap: (target: Omit<MapTarget, "seq">) => void;
  /** To the Bestiary, from a mob chip inside the hunt detail. */
  onOpenMob: (target: Omit<BestiaryTarget, "seq">) => void;
}) {
  const [report, setReport] = useState<SlayerReport | null>(null);
  const [filter, setFilter] = useState<KillFilter>("open");
  const [tier, setTier] = useState("");
  const [search, setSearch] = useState("");
  const [sort, setSort] = useState<SortState | null>(null);
  const [openRows, setOpenRows] = useState<Set<string>>(new Set());
  const [openMeta, setOpenMeta] = useState<string | null>(null);
  const [highlightKey, setHighlightKey] = useState<string | null>(null);
  const [scrollTarget, setScrollTarget] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  // Which achievement (or, narrowed, which of its components) "Where to
  // hunt" is open for; null when the table has the full width to itself.
  //
  // Remembered across this panel's unmount, per session: the panel's doors
  // lead out to the Map and the Bestiary, and someone who goes to look at
  // Nektulos and comes Back should find the list of zones they left, not a
  // table they have to find their achievement in again. For the life of the
  // window only — which achievement you were reading is not a setting.
  const [huntSel, setHuntSelState] = useState<HuntSelection | null>(() =>
    sessionId ? (lastHuntSelection.get(sessionId) ?? null) : null,
  );
  const setHuntSel = (next: HuntSelection | null | ((prev: HuntSelection | null) => HuntSelection | null)) =>
    setHuntSelState((prev) => {
      const value = typeof next === "function" ? next(prev) : next;
      if (sessionId) {
        if (value) lastHuntSelection.set(sessionId, value);
        else lastHuntSelection.delete(sessionId);
      }
      return value;
    });
  const rowRefs = useRef<Map<string, HTMLTableRowElement>>(new Map());

  // Fetch on mount, on Refresh, and every 15s while the tab is actually
  // visible — this is a file on disk, not session state, so there is no
  // SignalR push to ride on and no point polling a tab nobody is looking at.
  useEffect(() => {
    if (!sessionId) {
      setReport(null);
      return;
    }
    let cancelled = false;
    const load = () =>
      api
        .slayer(sessionId)
        .then((r) => !cancelled && setReport(r))
        .catch(() => undefined);
    load();
    const timer = window.setInterval(() => {
      if (document.visibilityState === "visible") load();
    }, 15000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [sessionId]);

  const kills = report?.kills ?? [];

  const tiers = useMemo(() => [...new Set(kills.map((k) => k.tier))].sort(), [kills]);

  const filteredRows = useMemo(() => {
    const q = search.trim().toLowerCase();
    return kills.filter((a) => {
      if (filter === "open" && a.complete) return false;
      if (filter === "complete" && !a.complete) return false;
      if (tier !== "" && a.tier !== tier) return false;
      if (q.length === 0) return true;
      if (a.title.toLowerCase().includes(q)) return true;
      return a.components.some((c) => c.text.toLowerCase().includes(q));
    });
  }, [kills, filter, tier, search]);

  const sortedRows = useMemo(() => {
    const rows = [...filteredRows];
    if (!sort) {
      // No column picked: nearest-to-done first (open, highest fraction),
      // complete pushed to the bottom. This is the view's own ranking, not
      // the export's — the file has no order of its own worth keeping.
      rows.sort(defaultKillOrder);
      return rows;
    }
    const sign = sort.dir === "asc" ? 1 : -1;
    rows.sort((a, b) => {
      switch (sort.key) {
        case NAME_SORT:
          return sign * a.title.localeCompare(b.title);
        case "tier":
          return sign * a.tier.localeCompare(b.tier) || defaultKillOrder(a, b);
        case "remaining":
          return sign * ((a.remaining ?? Infinity) - (b.remaining ?? Infinity));
        default:
          return 0;
      }
    });
    return rows;
  }, [filteredRows, sort]);

  // A meta component's target may be filtered out of view (a different tier,
  // "complete only", a stale search box); jumping to it clears whatever is
  // hiding it first, then waits for the row to actually exist before
  // scrolling to it.
  useEffect(() => {
    if (!scrollTarget) return;
    const el = rowRefs.current.get(scrollTarget);
    if (!el) return;
    el.scrollIntoView({ behavior: "smooth", block: "center" });
    setHighlightKey(scrollTarget);
    setScrollTarget(null);
    const t = window.setTimeout(() => setHighlightKey(null), 2000);
    return () => window.clearTimeout(t);
  }, [scrollTarget, sortedRows]);

  function refresh() {
    if (!sessionId) return;
    api
      .slayer(sessionId)
      .then(setReport)
      .catch(() => undefined);
  }

  function toggleRow(key: string) {
    setOpenRows((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  // Selecting the same achievement/component again closes the detail — a
  // second door back out of it, alongside HuntDetail's own close button.
  function selectHunt(key: string, component: number | null) {
    setHuntSel((prev) => (prev && prev.key === key && prev.component === component ? null : { key, component }));
  }

  function jumpTo(targetKey: string) {
    setFilter("all");
    setTier("");
    setSearch("");
    setScrollTarget(targetKey);
  }

  async function copyCommand(command: string) {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard access can be refused outside a secure context or without
      // permission; the command is still on screen to copy by hand.
    }
  }

  if (!sessionId) {
    return (
      <div className="dashboard-main slayer">
        <div className="panel">
          <div className="empty">Open a log to see its Slayer achievements.</div>
        </div>
      </div>
    );
  }

  if (!report) {
    return (
      <div className="dashboard-main slayer">
        <div className="panel">
          <div className="empty">Loading your Slayer achievements…</div>
        </div>
      </div>
    );
  }

  // No install root — the export could never have been looked for. `problem`
  // is the sentence the player can act on; there is nothing this page can add
  // to it.
  if (report.problem) {
    return (
      <div className="dashboard-main slayer">
        <div className="panel">
          <div className="panel-title">
            <span className="panel-name">Slayer</span>
          </div>
          <div className="empty">{report.problem}</div>
        </div>
      </div>
    );
  }

  // An install root exists but the export has never been written. This is
  // the ordinary first-run state, not a failure, so it gets the command and a
  // copy button rather than an error.
  if (!report.found) {
    return (
      <div className="dashboard-main slayer">
        <div className="panel">
          <div className="panel-title">
            <span className="panel-name">Slayer</span>
          </div>
          <div className="mob-intro">
            <p>
              No export found yet. Slayer progress comes from the game's own achievements file,
              and this character has never written one.
            </p>
            <p>
              Run <code>{report.command}</code> in game, then Refresh.{" "}
              <button className="mini-btn" onClick={() => void copyCommand(report.command)}>
                {copied ? "copied" : "copy"}
              </button>
            </p>
            <p className="subtle">Looked for at {report.path ?? "the install root"}.</p>
          </div>
          <div className="mob-controls">
            <button className="mini-btn slayer-refresh" onClick={refresh} title="Look for the export again">
              <IconRefresh size={12} stroke={1.8} /> Refresh
            </button>
          </div>
        </div>
      </div>
    );
  }

  const doneCount = kills.filter((k) => k.complete).length;
  const openMetaAchievement = report.meta.find((m) => m.key === openMeta) ?? null;

  return (
    <div className="dashboard-main slayer">
      <div className="panel slayer-head-panel">
        <div className="panel-title">
          <span className="panel-name">Slayer</span>
          <span className="panel-controls slayer-head-controls">
            <span className="subtle">
              {doneCount} of {kills.length} complete
            </span>
            <span className="subtle" title={absoluteTime(report.exportedUtc)}>
              {ageWords(report.exportedUtc)}
            </span>
            {report.skippedLines > 0 && (
              <span
                className="subtle"
                title="Lines in the achievements export this build did not recognize"
              >
                {fmtCount(report.skippedLines)} skipped
              </span>
            )}
            <span className="slayer-cmd" title="The command that refreshes these numbers">
              <code>{report.command}</code>
              <button className="mini-btn" onClick={() => void copyCommand(report.command)}>
                {copied ? "copied" : "copy"}
              </button>
            </span>
            <button
              className="mini-btn slayer-refresh"
              onClick={refresh}
              title="Re-read the achievements export now"
            >
              <IconRefresh size={12} stroke={1.8} /> Refresh
            </button>
          </span>
        </div>

        {report.meta.length > 0 && (
          <>
            <div className="slayer-meta-strip">
              {report.meta.map((m) => (
                <button
                  key={m.key}
                  className={"slayer-meta-card" + (openMeta === m.key ? " on" : "")}
                  onClick={() => setOpenMeta(openMeta === m.key ? null : m.key)}
                  title={`${m.requiredDone} of ${m.requiredTotal} required components done`}
                >
                  <span className="slayer-meta-title">{m.title}</span>
                  <span className="slayer-meta-frac subtle">
                    {m.requiredDone}/{m.requiredTotal}
                  </span>
                  <Bar pct={metaPct(m)} complete={m.complete} />
                </button>
              ))}
            </div>
            {openMetaAchievement && (
              <ul className="slayer-meta-components">
                {openMetaAchievement.components.map((c, i) => (
                  <li key={i} className="slayer-meta-component">
                    <span className={"slayer-mark " + (c.complete ? "done" : "open")}>
                      {c.complete ? "done" : "open"}
                    </span>
                    {c.optional && <span className="subtle"> optional</span>}
                    {c.targetKey ? (
                      <button className="slayer-meta-link" onClick={() => jumpTo(c.targetKey!)}>
                        {c.title}
                      </button>
                    ) : (
                      // Most of these are creatures this game does not have
                      // yet (ADR-023 Decision 2: 59 of 179 references in the
                      // owner's own file match nothing). The reference still
                      // carries its own C/I state, so it is still a row —
                      // plain text, not a broken link, because that is
                      // normal here and must not read as an error.
                      <span className="slayer-meta-unresolved">{c.title}</span>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </div>

      <div className={"slayer-split" + (huntSel ? " split" : "")}>
        <div className="panel table-panel slayer-table-panel">
          <div className="table-search">
            <div className="tabs">
              {(["open", "complete", "all"] as const).map((f) => (
                <button
                  key={f}
                  className={"tab" + (filter === f ? " on" : "")}
                  onClick={() => setFilter(f)}
                >
                  {f === "open" ? "Open" : f === "complete" ? "Complete" : "All"}
                </button>
              ))}
            </div>
            {tiers.length > 1 && (
              <select className="panel-select" value={tier} onChange={(e) => setTier(e.target.value)}>
                <option value="">All tiers</option>
                {tiers.map((t) => (
                  <option key={t} value={t}>
                    {t}
                  </option>
                ))}
              </select>
            )}
            <TableSearch
              value={search}
              onChange={setSearch}
              placeholder="Filter by title or creature…"
              shown={sortedRows.length}
              total={kills.length}
            />
          </div>
          <div className="table-scroll">
            <table className="mob-table slayer-table">
              <thead>
                <tr>
                  <SortHeader label="Title" sortKey={NAME_SORT} sort={sort} onSort={setSort} />
                  <SortHeader label="Tier" sortKey="tier" sort={sort} onSort={setSort} />
                  <th>Creatures</th>
                  <th>Progress</th>
                  <SortHeader label="Remaining" sortKey="remaining" sort={sort} onSort={setSort} numeric />
                </tr>
              </thead>
              <tbody>
                {sortedRows.map((a) => {
                  const expandable = a.components.length > 1;
                  const expanded = expandable && openRows.has(a.key);
                  const selected = huntSel?.key === a.key && huntSel.component === null;
                  return (
                    <Fragment key={a.key}>
                      <tr
                        ref={(el) => {
                          if (el) rowRefs.current.set(a.key, el);
                          else rowRefs.current.delete(a.key);
                        }}
                        className={
                          "slayer-row selectable" +
                          (expandable ? " expandable" : "") +
                          (highlightKey === a.key ? " highlight" : "") +
                          (selected ? " selected" : "")
                        }
                        tabIndex={0}
                        onClick={() => selectHunt(a.key, null)}
                        onKeyDown={(e) => {
                          if (e.key === "Enter" || e.key === " ") {
                            e.preventDefault();
                            selectHunt(a.key, null);
                          }
                        }}
                        title="Where to hunt this"
                      >
                        <td className="mob-name">
                          {expandable && (
                            // Its own control, not the row's: a click here toggles which
                            // components are listed and must not also select the row — the
                            // two questions ("show me the components" and "hunt for this
                            // one") are different asks with the same row as their target.
                            <span
                              className="expander"
                              tabIndex={0}
                              onClick={(e) => {
                                e.stopPropagation();
                                toggleRow(a.key);
                              }}
                              onKeyDown={(e) => {
                                if (e.key === "Enter" || e.key === " ") {
                                  e.preventDefault();
                                  e.stopPropagation();
                                  toggleRow(a.key);
                                }
                              }}
                              title="Show each component on its own"
                            >
                              {expanded ? "▾" : "▸"}
                            </span>
                          )}
                          {a.title}
                        </td>
                        <td className="subtle">{a.tier}</td>
                        <td className="subtle slayer-creatures">
                          {/* A block of its own so the split view can clamp it
                              (a table cell cannot be line-clamped); the title
                              keeps the whole list one hover away. */}
                          <div className="slayer-creatures-text" title={a.components.map((c) => c.text).join("; ")}>
                            {a.components.map((c) => c.text).join("; ")}
                          </div>
                        </td>
                        <td className="slayer-progress">
                          <Bar pct={a.fraction * 100} complete={a.complete} />
                          <span className="slayer-progress-label">{killProgressLabel(a)}</span>
                        </td>
                        <td className="num subtle">{a.remaining != null ? fmtCount(a.remaining) : "—"}</td>
                      </tr>
                      {expanded &&
                        a.components.map((c, i) => {
                          const componentSelected = huntSel?.key === a.key && huntSel.component === i;
                          return (
                            <tr
                              key={i}
                              className={"child-row selectable" + (componentSelected ? " selected" : "")}
                              tabIndex={0}
                              onClick={() => selectHunt(a.key, i)}
                              onKeyDown={(e) => {
                                if (e.key === "Enter" || e.key === " ") {
                                  e.preventDefault();
                                  selectHunt(a.key, i);
                                }
                              }}
                              title="Where to hunt this"
                            >
                              <td className="mob-name">
                                <span className="expander-spacer" />
                                {c.text}
                                {c.optional && <span className="subtle"> optional</span>}
                              </td>
                              <td />
                              <td />
                              <td className="slayer-progress">
                                <Bar pct={componentFraction(c) * 100} complete={c.complete} />
                                <span className="slayer-progress-label">{componentLabel(c)}</span>
                              </td>
                              <td className="num subtle">
                                {c.have != null && c.need != null ? fmtCount(Math.max(0, c.need - c.have)) : "—"}
                              </td>
                            </tr>
                          );
                        })}
                    </Fragment>
                  );
                })}
                {sortedRows.length === 0 && (
                  <tr>
                    <td colSpan={5} className="empty">
                      Nothing matches.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>

        {huntSel && (
          <HuntDetail
            // A fresh key per selection remounts the detail rather than
            // patching it in place — every piece of its state (the fetched
            // report, the level cap, which zones have "other factions" open)
            // starts over for a new achievement or component instead of
            // carrying the previous selection's answers into this one.
            key={`${huntSel.key}|${huntSel.component ?? "-"}`}
            sessionId={sessionId}
            selection={huntSel}
            referenceEnabled={referenceEnabled}
            onClose={() => setHuntSel(null)}
            onShowOnMap={onShowOnMap}
            onOpenMob={onOpenMob}
          />
        )}
      </div>
    </div>
  );
}

type KillFilter = "open" | "complete" | "all";

/** Open first, nearest to done first within that, complete pushed last. */
function defaultKillOrder(a: SlayerKillAchievement, b: SlayerKillAchievement): number {
  if (a.complete !== b.complete) return a.complete ? 1 : -1;
  if (!a.complete) return b.fraction - a.fraction;
  return a.title.localeCompare(b.title);
}

function metaPct(m: SlayerMetaAchievement): number {
  return m.requiredTotal > 0 ? Math.min(100, (m.requiredDone / m.requiredTotal) * 100) : 0;
}

function componentFraction(c: SlayerKillComponent): number {
  if (c.complete) return 1;
  if (c.have != null && c.need != null && c.need > 0) return Math.min(1, c.have / c.need);
  return 0;
}

/**
 * The have/need text under a component's bar. A completed component carries
 * no count at all (eq-client-files.md, "the achievements export") — the
 * export simply drops it once it is done — so "complete" is the only honest
 * label once state says so, never a recomputed 100%.
 */
function componentLabel(c: SlayerKillComponent): string {
  if (c.complete) return "complete";
  if (c.have != null && c.need != null) return `${fmtCount(c.have)} / ${fmtCount(c.need)}`;
  return "—";
}

/**
 * The have/need text for an achievement's aggregate row. An achievement with
 * several components (up to fifteen, for the race-spread ones) has no single
 * pair of numbers in the export — only `fraction` and `remaining` are given —
 * so this sums what the export prints for whichever components are still
 * open. Completed components are skipped rather than guessed at, for the
 * same reason `componentLabel` never recomputes one: the export's own count
 * is the only one this app shows (ADR-023 Decision 1).
 *
 * That sum is honest but partial once ANY component has finished — a
 * finished component drops its count entirely, so "16 / 100" on a
 * fifteen-component achievement can look like it disagrees with the bar
 * beside it, which tracks `fraction` and accounts for every component
 * (`fraction` is a Core contract; it is not recomputed here, and the bar
 * must keep tracking it or it would contradict the row's own sort position).
 * The two numbers are not the same claim, so once components have started
 * finishing the label says so directly — a done-of-total tally beside the
 * still-open sum — rather than printing a bare fraction a reader would read
 * against the bar and find "wrong".
 */
function killProgressLabel(a: SlayerKillAchievement): string {
  if (a.complete) return "complete";

  let have = 0;
  let need = 0;
  let open = 0;
  for (const c of a.components) {
    if (c.have != null && c.need != null) {
      have += c.have;
      need += c.need;
      open++;
    }
  }
  const done = a.components.length - open;

  if (a.components.length === 1) {
    // One component IS the achievement, so there is only ever one number to
    // show — the ambiguity below cannot arise, and this must read exactly as
    // it always has.
    if (open > 0) return `${fmtCount(have)} / ${fmtCount(need)}`;
    return a.remaining != null ? `${fmtCount(a.remaining)} left` : "in progress";
  }

  if (open === 0) return a.remaining != null ? `${fmtCount(a.remaining)} left` : "in progress";
  if (done === 0) return `${fmtCount(have)} / ${fmtCount(need)}`; // nothing finished yet — the sum covers all of it
  return `${done}/${a.components.length} done · ${fmtCount(have)}/${fmtCount(need)} open`;
}

/**
 * "exported 3 hours ago". Coarse on purpose — the absolute time is a
 * mouse-hover away via `title` — and this text needs no ticking clock of its
 * own because the panel already re-fetches and re-renders on the same 15s
 * beat the header's age is read against.
 */
function ageWords(iso: string | null): string {
  if (!iso) return "export time unknown";
  const ms = Math.max(0, Date.now() - new Date(iso).getTime());
  const mins = Math.round(ms / 60000);
  if (mins < 1) return "exported moments ago";
  if (mins < 60) return `exported ${mins} minute${mins === 1 ? "" : "s"} ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `exported ${hours} hour${hours === 1 ? "" : "s"} ago`;
  const days = Math.round(hours / 24);
  return `exported ${days} day${days === 1 ? "" : "s"} ago`;
}

function absoluteTime(iso: string | null): string | undefined {
  return iso ? new Date(iso).toLocaleString() : undefined;
}

/**
 * A thin fill track, shared by the meta strip and the achievement table —
 * and, exported, by the hunt detail's atlas-progress bar (HuntDetail.tsx),
 * so "12 of 79" reuses the one bar shape rather than growing a second.
 */
export function Bar({ pct, complete }: { pct: number; complete?: boolean }) {
  const clamped = Math.max(0, Math.min(100, pct));
  return (
    <span className="slayer-bar-track">
      <span
        className={"slayer-bar-fill" + (complete ? " complete" : "")}
        style={{ width: `${clamped}%` }}
      />
    </span>
  );
}
