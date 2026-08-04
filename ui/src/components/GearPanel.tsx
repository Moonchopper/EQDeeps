import { useEffect, useMemo, useState } from "react";
import {
  api,
  type FightInfo,
  type GearChange,
  type GearItem,
  type GearReport,
  type GearSnapshot,
  type QueryResult,
} from "../api";
import { fmtNum } from "../format";
import { describeChange } from "../gearOverlay";
import type { TimeFrame } from "../timeFrame";

interface Props {
  sessionId: string;
  /** Null until the first fetch lands. */
  gear: GearReport | null;
  /** For counting how much combat each side of a change actually covers. */
  fights: FightInfo[];
  frame: TimeFrame;
  character: string;
  refreshKey: number;
}

/** The instant the current frame is asking about. */
function frameInstant(frame: TimeFrame, fights: FightInfo[]): string | null {
  if (frame.kind === "range") {
    return frame.begin;
  }

  // A live frame is about now, which in log terms is the newest thing seen.
  return fights.length > 0 ? fights[fights.length - 1].lastDamageTime : null;
}

/**
 * The snapshot in force at an instant — the same forward-only rule the server
 * applies, restated here so the panel can label a frame without a round trip.
 */
function effectiveAt(snapshots: GearSnapshot[], at: string | null): GearSnapshot | null {
  if (at === null) {
    return snapshots.length > 0 ? snapshots[snapshots.length - 1] : null;
  }

  let effective: GearSnapshot | null = null;
  for (const snapshot of snapshots) {
    if (snapshot.capturedAt > at) {
      break;
    }
    effective = snapshot;
  }

  return effective;
}

function fmtTime(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function fightsBetween(fights: FightInfo[], begin: string, end: string): FightInfo[] {
  return fights.filter((f) => f.lastDamageTime >= begin && f.beginTime < end);
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

/** Damage over one window, for the two sides of a gear change. */
interface Side {
  total: number;
  sdps: number;
  fights: number;
  seconds: number;
}

/**
 * Gear as context for a parse: what the character was wearing, when it changed,
 * and what their damage looked like either side of a change.
 *
 * The comparison is offered because it is the question everyone asks, and
 * labelled honestly because it is not a controlled experiment: two windows of
 * play differ in mobs, group, and duration as well as in gear. The panel shows
 * what each side actually covers so the number can be weighed rather than
 * believed.
 */
export function GearPanel({ sessionId, gear, fights, frame, character, refreshKey }: Props) {
  const snapshots = gear?.snapshots ?? [];
  const changes = gear?.changes ?? [];
  const status = gear?.status;

  const [selected, setSelected] = useState<string | null>(null);
  const [sides, setSides] = useState<{ before: Side; after: Side } | null>(null);

  const at = frameInstant(frame, fights);
  const effective = effectiveAt(snapshots, at);

  // The newest change is the interesting one by default.
  const change: GearChange | null = useMemo(() => {
    if (changes.length === 0) return null;
    return changes.find((c) => c.at === selected) ?? changes[changes.length - 1];
  }, [changes, selected]);

  // The windows each snapshot was actually in force for: from the change until
  // the next one, and from the previous change back to the one before it.
  const windows = useMemo(() => {
    if (change === null || fights.length === 0) return null;

    const index = changes.indexOf(change);
    const next = changes[index + 1]?.at ?? fights[fights.length - 1].lastDamageTime;
    const start = changes[index - 1]?.at ?? snapshots[0]?.capturedAt ?? change.previousAt;
    return {
      before: { begin: start, end: change.at },
      after: { begin: change.at, end: next },
    };
  }, [change, changes, fights, snapshots]);

  useEffect(() => {
    if (windows === null) {
      setSides(null);
      return;
    }

    let cancelled = false;
    const ask = (begin: string, end: string): Promise<QueryResult> =>
      api.query(sessionId, {
        source: "damage",
        scope: { timeRanges: [{ begin, end }] },
        groupBy: ["player"],
        metrics: ["total", "sdps", "activeSeconds"],
      });

    const side = (result: QueryResult, begin: string, end: string): Side => {
      const row = result.rows.find((r) => r.label === character);
      return {
        total: row?.metrics.total ?? 0,
        sdps: row?.metrics.sdps ?? 0,
        seconds: row?.metrics.activeSeconds ?? 0,
        fights: fightsBetween(fights, begin, end).length,
      };
    };

    Promise.all([
      ask(windows.before.begin, windows.before.end),
      ask(windows.after.begin, windows.after.end),
    ])
      .then(([before, after]) => {
        if (cancelled) return;
        setSides({
          before: side(before, windows.before.begin, windows.before.end),
          after: side(after, windows.after.begin, windows.after.end),
        });
      })
      .catch(() => !cancelled && setSides(null));

    return () => {
      cancelled = true;
    };
  }, [sessionId, JSON.stringify(windows), character, refreshKey]);

  return (
    <div className="panel gear-panel">
      <div className="panel-title">
        <span>Gear</span>
        {effective !== null && (
          <span className="subtle">
            {fmtTime(effective.capturedAt)} · score {effective.upgradeScore}
          </span>
        )}
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

        {snapshots.length > 0 && effective === null && (
          <div className="gear-unknown">
            Gear unknown for this time frame — it is older than the first snapshot
            {snapshots[0] !== undefined && ` (${fmtTime(snapshots[0].capturedAt)})`}.
          </div>
        )}

        {changes.length > 0 && (
          <div className="gear-section">
            <div className="gear-section-title">Changes</div>
            <div className="gear-changes">
              {[...changes].reverse().map((c) => (
                <button
                  key={c.at}
                  type="button"
                  className={`gear-change${c.at === change?.at ? " selected" : ""}`}
                  onClick={() => setSelected(c.at)}
                >
                  <span className="gear-change-when">{fmtTime(c.at)}</span>
                  <span className="gear-change-what">{describeChange(c)}</span>
                  {c.upgradeScoreDelta !== 0 && (
                    <span className="gear-plus">
                      {c.upgradeScoreDelta > 0 ? "+" : ""}
                      {c.upgradeScoreDelta}
                    </span>
                  )}
                </button>
              ))}
            </div>
          </div>
        )}

        {change !== null && sides !== null && (
          <div className="gear-section">
            <div className="gear-section-title">
              Damage either side of {fmtTime(change.at)}
            </div>
            <table className="gear-compare">
              <thead>
                <tr>
                  <th />
                  <th className="num">Before</th>
                  <th className="num">After</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <td>Total</td>
                  <td className="num">{fmtNum(sides.before.total)}</td>
                  <td className="num">{fmtNum(sides.after.total)}</td>
                </tr>
                <tr>
                  <td>sDPS</td>
                  <td className="num">{fmtNum(sides.before.sdps)}</td>
                  <td className="num">{fmtNum(sides.after.sdps)}</td>
                </tr>
                <tr className="subtle">
                  <td>Fights</td>
                  <td className="num">{sides.before.fights}</td>
                  <td className="num">{sides.after.fights}</td>
                </tr>
              </tbody>
            </table>
            {/* The caveat is part of the number, not a footnote to it. */}
            <p className="subtle gear-caveat">
              Different mobs, group and duration on each side — this is context, not a
              controlled comparison. The change itself happened somewhere between{" "}
              {fmtTime(change.previousAt)} and {fmtTime(change.at)}.
            </p>
          </div>
        )}

        {effective !== null && (
          <div className="gear-section">
            <div className="gear-section-title">
              Equipped{at !== null && ` at ${fmtTime(at)}`}
            </div>
            <table className="gear-equipped">
              <tbody>
                {effective.equipped.map((item) => (
                  <ItemRow key={item.slotKey} item={item} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
