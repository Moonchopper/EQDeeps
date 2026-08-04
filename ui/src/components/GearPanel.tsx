import { useEffect, useMemo, useState } from "react";
import {
  api,
  type GearItem,
  type GearReport,
  type GearSlotChange,
  type GearSnapshot,
  type QueryResult,
} from "../api";
import { fmtNum } from "../format";
import { PanelBody, type PanelContext } from "../dashboards/PanelBody";
import { defaultPanel, type PanelDef } from "../dashboards/model";
import { COMPARE_MODES, GearCompare, type CompareMode } from "./GearCompare";
import { describeSlot, summariseChange } from "../gearOverlay";
import type { ChartSettings } from "../timeControls";
import type { TimeFrame } from "../timeFrame";

interface Props {
  /** The shared panel context; each gear set re-scopes a copy of it. */
  ctx: PanelContext;
  /** Null until the first fetch lands. */
  gear: GearReport | null;
  character: string;
  chartDefaults: ChartSettings;
}

/**
 * How many sets get their numbers fetched. Each one is a query, and a long
 * history has no use for a 200-row table — but the panel says when it has
 * stopped rather than letting a truncated list look complete.
 */
const MAX_SETS_MEASURED = 20;

/**
 * A snapshot plus the stretch of time it was in force for: from its own
 * capture until the next snapshot, or until the end of the log.
 *
 * This is the unit the tab is organised around. "Gear set" is what a player
 * calls it, and the window is the only thing their damage can honestly be
 * attributed to — a snapshot on its own says nothing about performance.
 */
interface GearSet {
  snapshot: GearSnapshot;
  /** 1-based, oldest first — a stable handle for the table. */
  ordinal: number;
  begin: string;
  end: string;
  /** What changed to bring this set about; empty for the first one. */
  changed: GearSlotChange[];
  scoreDelta: number;
}

/** Damage over one set's window. */
interface SetStats {
  total: number;
  sdps: number;
  seconds: number;
  fights: number;
}

function fmtTime(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function fmtSpan(seconds: number): string {
  if (seconds >= 3600) return `${(seconds / 3600).toFixed(1)}h`;
  if (seconds >= 60) return `${Math.round(seconds / 60)}m`;
  return `${Math.round(seconds)}s`;
}

function ItemRow({ item }: { item: GearItem }) {
  return (
    <>
      <tr>
        <td className="gear-slot">{item.location}</td>
        <td>
          {item.baseName}
          {item.plus > 0 && <span className="gear-plus"> +{item.plus}</span>}
        </td>
      </tr>
      {item.augments.map((augment) => (
        <tr key={`${item.slotKey}/${augment.itemId}/${augment.name}`} className="gear-augment">
          <td />
          <td>{augment.name}</td>
        </tr>
      ))}
    </>
  );
}

/**
 * Charts for one set. Ordinary panels — the same ones the Summary builds — so
 * a gear set is read with exactly the tools every other window of time is.
 */
const SET_PANELS: PanelDef[] = [
  {
    ...defaultPanel(),
    id: "gear-dps",
    title: "Damage per second",
    viz: "line",
    source: "damage",
    scopeMode: "all",
    groupBy: ["player"],
    bucketSeconds: 1,
  },
  {
    ...defaultPanel(),
    id: "gear-ability",
    title: "Damage by ability",
    viz: "bar",
    source: "damage",
    scopeMode: "all",
    groupBy: ["spell"],
    primaryMetric: "total",
    metrics: ["total", "dps", "critRate"],
  },
];

/**
 * Gear as context for a parse: which sets the character has worn, how each one
 * actually played, and what changed between them.
 *
 * The comparison across sets is the point of the tab, and it is not a
 * controlled experiment — sets differ in mobs, group and hours played as well
 * as in gear. Every row therefore carries what it is made of, so a difference
 * in sDPS can be weighed rather than believed.
 */
export function GearPanel({ ctx, gear, character, chartDefaults }: Props) {
  const snapshots = gear?.snapshots ?? [];
  const changes = gear?.changes ?? [];
  const status = gear?.status;
  const { fights } = ctx;

  const [selectedAt, setSelectedAt] = useState<string | null>(null);
  const [stats, setStats] = useState<Record<string, SetStats>>({});
  const [mode, setMode] = useState<CompareMode>("distribution");
  const [commonTargets, setCommonTargets] = useState(false);
  // Which sets the comparison covers. Null means "not chosen yet" and resolves
  // to all of them, so the chart says something the moment the tab opens.
  const [compared, setCompared] = useState<Set<string> | null>(null);

  /** Every snapshot as a worn period, oldest first. */
  const sets: GearSet[] = useMemo(() => {
    if (snapshots.length === 0) return [];
    const logEnd =
      fights.length > 0
        ? fights[fights.length - 1].lastDamageTime
        : snapshots[snapshots.length - 1].capturedAt;

    return snapshots.map((snapshot, i) => {
      const change = changes.find((c) => c.at === snapshot.capturedAt);
      return {
        snapshot,
        ordinal: i + 1,
        begin: snapshot.capturedAt,
        // The newest set runs to the end of the log: it is still being worn.
        end: snapshots[i + 1]?.capturedAt ?? logEnd,
        changed: change?.slots ?? [],
        scoreDelta: change?.upgradeScoreDelta ?? 0,
      };
    });
  }, [snapshots, changes, fights]);

  /** Newest first: the set being worn now is the one in question. */
  const shown = useMemo(() => [...sets].reverse().slice(0, MAX_SETS_MEASURED), [sets]);

  // The default selection follows the app's time frame, so opening this tab
  // after picking a fight lands on the gear that fight was fought in.
  const frameAt =
    ctx.frame.kind === "range"
      ? ctx.frame.begin
      : fights.length > 0
        ? fights[fights.length - 1].lastDamageTime
        : null;
  const defaultSet = useMemo(() => {
    if (sets.length === 0) return null;
    if (frameAt === null) return sets[sets.length - 1];
    let match: GearSet | null = null;
    for (const set of sets) {
      if (set.begin > frameAt) break;
      match = set;
    }
    return match;
  }, [sets, frameAt]);

  const selected = sets.find((s) => s.begin === selectedAt) ?? defaultSet;

  // One damage query per shown set, keyed by the windows themselves so a set
  // that hasn't moved is not re-fetched because another one appeared.
  const measureKey = shown.map((s) => `${s.begin}~${s.end}`).join("|");
  useEffect(() => {
    if (shown.length === 0) {
      setStats({});
      return;
    }

    let cancelled = false;
    Promise.all(
      shown.map((set) =>
        api
          .query(ctx.sessionId, {
            source: "damage",
            scope: { timeRanges: [{ begin: set.begin, end: set.end }] },
            groupBy: ["player"],
            metrics: ["total", "sdps", "activeSeconds"],
          })
          .then((r: QueryResult) => {
            const row = r.rows.find((x) => x.label === character);
            return [
              set.begin,
              {
                total: row?.metrics.total ?? 0,
                sdps: row?.metrics.sdps ?? 0,
                seconds: row?.metrics.activeSeconds ?? 0,
                fights: fights.filter(
                  (f) => f.lastDamageTime >= set.begin && f.beginTime < set.end,
                ).length,
              },
            ] as const;
          })
          .catch(() => [set.begin, null] as const),
      ),
    ).then((entries) => {
      if (cancelled) return;
      const next: Record<string, SetStats> = {};
      for (const [key, value] of entries) {
        if (value) next[key] = value;
      }
      setStats(next);
    });

    return () => {
      cancelled = true;
    };
  }, [ctx.sessionId, measureKey, character, ctx.refreshKey]);

  /** The selected set's window, as a context the panels below report over. */
  const setCtx: PanelContext | null = selected
    ? {
        ...ctx,
        frame: {
          kind: "range",
          fightIds: [],
          begin: selected.begin,
          end: selected.end,
        } as TimeFrame,
        // A finished window has no reason to chase the wall clock.
        scrollNowMs: null,
      }
    : null;

  const best = Math.max(0, ...Object.values(stats).map((s) => s.sdps));

  const inCompare = (set: GearSet) => compared === null || compared.has(set.begin);
  const toggleCompare = (set: GearSet) =>
    setCompared((current) => {
      const next = new Set(current ?? shown.map((s) => s.begin));
      if (!next.delete(set.begin)) {
        next.add(set.begin);
      }
      return next;
    });

  const compareSets = shown
    .filter(inCompare)
    // Oldest first, so a series read left to right is read forwards in time.
    .slice()
    .reverse()
    .map((set) => ({
      key: set.begin,
      label: `#${set.ordinal}`,
      begin: set.begin,
      end: set.end,
    }));
  const modeBlurb = COMPARE_MODES.find((m) => m.value === mode)?.blurb ?? "";

  return (
    <div className="panel gear-panel">
      <div className="panel-title">
        <span>Gear</span>
        <span className="subtle">
          {sets.length === 0
            ? "no snapshots"
            : `${sets.length} ${sets.length === 1 ? "set" : "sets"}`}
        </span>
      </div>

      <div className="gear-scroll">
        {status !== undefined && !status.hasSnapshot && (
          <div className="gear-nudge">
            <div className="gear-nudge-lead">No gear snapshot yet.</div>
            <p>
              EverQuest never writes your equipped gear anywhere on its own, and a loadout
              swap leaves no trace in the log. Type <code>{status.command}</code> in game and
              this fills in.
            </p>
            <p className="subtle">
              Put it on a hotbutton or a social and it becomes one keypress. Re-run it
              whenever your gear changes — nothing else can tell.
            </p>
            <p className="subtle gear-path">Watching for: {status.expectedPath}</p>
          </div>
        )}

        {status !== undefined && status.hasSnapshot && status.fightsSince > 0 && (
          <div className="gear-stale">
            {status.fightsSince} {status.fightsSince === 1 ? "fight" : "fights"} since your
            last snapshot — run <code>{status.command}</code> again if your gear has moved.
          </div>
        )}

        {sets.length > 0 && (
          <div className="gear-section">
            <div className="gear-section-title">
              Sets
              <span className="subtle gear-section-note">
                newest first · click one to see how it played
              </span>
            </div>
            <table className="gear-sets">
              <thead>
                <tr>
                  <th title="Include in the comparison below" />
                  <th>Worn from</th>
                  <th>What changed</th>
                  <th className="num">Score</th>
                  <th className="num">Fights</th>
                  <th className="num">Time</th>
                  <th className="num">Total</th>
                  <th className="num">sDPS</th>
                </tr>
              </thead>
              <tbody>
                {shown.map((set) => {
                  const stat = stats[set.begin];
                  return (
                    <tr
                      key={set.begin}
                      className={
                        "gear-set-row" + (selected?.begin === set.begin ? " selected" : "")
                      }
                      onClick={() => setSelectedAt(set.begin)}
                    >
                      <td
                        className="gear-pick"
                        onClick={(e) => {
                          // Picking a set for the comparison is not the same
                          // act as opening it below.
                          e.stopPropagation();
                          toggleCompare(set);
                        }}
                      >
                        <input type="checkbox" readOnly checked={inCompare(set)} />
                      </td>
                      <td>
                        {fmtTime(set.begin)}
                        <span className="subtle"> · #{set.ordinal}</span>
                      </td>
                      <td className="gear-set-what">
                        {set.ordinal === 1 ? (
                          <span className="subtle">first snapshot</span>
                        ) : (
                          summariseChange(set.changed)
                        )}
                      </td>
                      <td className="num">
                        {set.snapshot.upgradeScore}
                        {set.scoreDelta !== 0 && (
                          <span className="gear-plus">
                            {" "}
                            {set.scoreDelta > 0 ? "+" : ""}
                            {set.scoreDelta}
                          </span>
                        )}
                      </td>
                      <td className="num">{stat ? stat.fights : "—"}</td>
                      <td className="num">{stat ? fmtSpan(stat.seconds) : "—"}</td>
                      <td className="num">{stat ? fmtNum(stat.total) : "—"}</td>
                      <td className="num gear-sdps">
                        {stat ? (
                          <>
                            {/* Bar behind the number: relative sDPS across sets
                                is the one comparison worth reading at a glance. */}
                            <span
                              className="gear-bar"
                              style={{ width: best > 0 ? `${(stat.sdps / best) * 100}%` : "0%" }}
                            />
                            <span className="gear-bar-value">{fmtNum(stat.sdps)}</span>
                          </>
                        ) : (
                          "—"
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {sets.length > MAX_SETS_MEASURED && (
              <p className="subtle gear-caveat">
                Showing the {MAX_SETS_MEASURED} most recent of {sets.length} sets.
              </p>
            )}
            <p className="subtle gear-caveat">
              Sets differ in mobs, group and hours played as well as in gear — the fight and
              time columns are there so an sDPS difference can be weighed, not just read.
            </p>
          </div>
        )}

        {sets.length > 1 && (
          <div className="gear-section">
            <div className="gear-section-title">
              Compare
              <span className="gear-compare-controls">
                {COMPARE_MODES.map((m) => (
                  <button
                    key={m.value}
                    type="button"
                    className={"mini-btn" + (mode === m.value ? " on" : "")}
                    onClick={() => setMode(m.value)}
                  >
                    {m.label}
                  </button>
                ))}
                <label className="gear-toggle" title="Restrict every set to mobs all of them fought">
                  <input
                    type="checkbox"
                    checked={commonTargets}
                    onChange={(e) => setCommonTargets(e.target.checked)}
                  />
                  like-for-like
                </label>
              </span>
            </div>
            <div className="gear-compare-wrap">
              <GearCompare
                sets={compareSets}
                fights={fights}
                mode={mode}
                commonTargets={commonTargets}
              />
            </div>
            {/* What the axis means, in the panel rather than in a doc — each
                mode is honest about something different and misreading which
                is which is the whole risk. */}
            <p className="subtle gear-caveat">{modeBlurb}</p>
          </div>
        )}

        {selected !== null && setCtx !== null && (
          <div className="gear-detail">
            <div className="gear-detail-main">
              <div className="gear-section-title">
                Set #{selected.ordinal} · {fmtTime(selected.begin)} → {fmtTime(selected.end)}
              </div>
              {SET_PANELS.map((panel) => (
                <div key={panel.id} className="panel chart-panel gear-chart">
                  <div className="panel-title">
                    <span className="panel-name">{panel.title}</span>
                  </div>
                  <PanelBody panel={panel} ctx={setCtx} settings={chartDefaults} />
                </div>
              ))}
            </div>

            <div className="gear-detail-side">
              {selected.changed.length > 0 && (
                <div className="gear-section">
                  <div className="gear-section-title">Changed into this set</div>
                  <ul className="gear-diff">
                    {selected.changed.map((slot) => (
                      <li key={slot.slotKey}>{describeSlot(slot)}</li>
                    ))}
                  </ul>
                </div>
              )}

              <div className="gear-section">
                <div className="gear-section-title">
                  Equipped
                  <span className="subtle gear-section-note">
                    score {selected.snapshot.upgradeScore}
                  </span>
                </div>
                <table className="gear-equipped">
                  <tbody>
                    {selected.snapshot.equipped.map((item) => (
                      <ItemRow key={item.slotKey} item={item} />
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
