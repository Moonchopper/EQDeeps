import { Fragment, memo, useCallback, useRef } from "react";
import type { FightInfo } from "../api";
import { fmtClock, fmtDuration, fmtNum } from "../format";

interface Props {
  fights: FightInfo[];
  selected: number[];
  /** True while the frame is a live tail — nothing in the list is framed. */
  live: boolean;
  onSelect: (ids: number[]) => void;
  onReset: () => void;
  collapsed: boolean;
  onToggleCollapsed: () => void;
}

/**
 * Chronological fight list with pull-chain grouping: a "break" divider renders
 * between groups.
 *
 * This is a RANGE SELECTOR, not a filter. A click frames one fight;
 * shift-click extends from the last click to frame everything between, in
 * list order; ctrl/cmd-click adds or removes a single fight; a group header
 * frames the whole pull chain. Whatever is picked becomes the app's time
 * frame — the window between the first and last fight chosen, downtime
 * included — so every panel reports over it.
 */
export function FightList({
  fights,
  selected,
  live,
  onSelect,
  onReset,
  collapsed,
  onToggleCollapsed,
}: Props) {
  const selectedSet = new Set(selected);
  // Where a shift-click measures from. Kept as a ref so extending the range
  // repeatedly always reaches back to the same anchor.
  const anchorRef = useRef<number | null>(null);
  // `pick` is handed to memoised rows, so it has to keep its identity across
  // renders — and still see the current list and selection when clicked.
  // Refs carry the latest of each; the callback itself never changes.
  const latest = useRef({ fights, selected, onSelect });
  latest.current = { fights, selected, onSelect };

  const pick = useCallback((id: number, event: React.MouseEvent) => {
    const { fights, selected, onSelect } = latest.current;
    if (event.shiftKey && anchorRef.current !== null) {
      const a = fights.findIndex((f) => f.id === anchorRef.current);
      const b = fights.findIndex((f) => f.id === id);
      if (a >= 0 && b >= 0) {
        const [lo, hi] = a <= b ? [a, b] : [b, a];
        onSelect(fights.slice(lo, hi + 1).map((f) => f.id));
        return;
      }
    }

    if (event.ctrlKey || event.metaKey) {
      const next = new Set(selected);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      anchorRef.current = id;
      onSelect([...next]);
      return;
    }

    anchorRef.current = id;
    onSelect([id]);
  }, []);

  const selectGroup = (groupIndex: number) => {
    const group = fights.filter((f) => f.groupIndex === groupIndex);
    anchorRef.current = group[0]?.id ?? null;
    onSelect(group.map((f) => f.id));
  };

  const rows: JSX.Element[] = [];
  let lastGroup = -1;
  for (const fight of [...fights].reverse()) {
    if (fight.groupIndex !== lastGroup) {
      lastGroup = fight.groupIndex;
      rows.push(
        <button
          key={`g${fight.groupIndex}`}
          className="fight-group"
          onClick={() => selectGroup(fight.groupIndex)}
          title="Frame the whole pull chain"
        >
          — pull chain {fight.groupIndex + 1} —
        </button>,
      );
    }
    rows.push(
      <FightRow
        key={fight.id}
        fight={fight}
        selected={selectedSet.has(fight.id)}
        onPick={pick}
      />,
    );
  }

  // Collapsed, the pane is a spine you click to get back — the frame it set
  // is still in force, so it has to stay visible rather than disappear.
  if (collapsed) {
    return (
      <button
        className="panel fight-list collapsed"
        onClick={onToggleCollapsed}
        title="Show the fight list"
      >
        <span className="fight-spine">‹ Fights</span>
      </button>
    );
  }

  return (
    <div className="panel fight-list">
      <div className="panel-title">
        <span className="fight-title">
          <button className="fight-collapse" onClick={onToggleCollapsed} title="Hide the fight list">
            ›
          </button>
          Fights
        </span>
        <span className="fight-actions">
          <button
            className="mini-btn"
            onClick={() => fights.length > 0 && onSelect(fights.map((f) => f.id))}
            title="Frame the entire log, first fight to last"
            disabled={fights.length === 0}
          >
            select all
          </button>
          {/* Returning to live is the way out of a fixed range, which is what
              the old "follow live" checkbox amounted to once the frame became
              a single app-wide concept. */}
          <button
            className={"mini-btn" + (live ? " on" : "")}
            onClick={onReset}
            title="Back to the live view — the trailing window, following new records"
            disabled={live}
          >
            {live ? "live" : "back to live"}
          </button>
        </span>
      </div>
      <div className="fight-scroll">
        {rows.length > 0 ? (
          <Fragment>{rows}</Fragment>
        ) : (
          <div className="empty">No fights yet</div>
        )}
      </div>
      {rows.length > 0 && (
        <div className="fight-hint subtle">click to frame · shift-click for a range</div>
      )}
    </div>
  );
}

/**
 * One fight, memoised on what it shows. The hub replaces the whole fights
 * array on every push — several times a second in combat — and the list
 * re-rendered every row each time, which made it the single most expensive
 * thing on an idle Summary. Now only the row whose numbers moved (the open
 * fight) re-renders; a hundred closed ones are compared and skipped.
 */
const FightRow = memo(
  function FightRow({
    fight,
    selected,
    onPick,
  }: {
    fight: FightInfo;
    selected: boolean;
    onPick: (id: number, event: React.MouseEvent) => void;
  }) {
    return (
      <button
        className={"fight-row" + (selected ? " selected" : "") + (!fight.closed ? " active" : "")}
        onClick={(e) => onPick(fight.id, e)}
      >
        <span className="fight-name">
          {fight.dead ? "☠ " : fight.closed ? "" : "⚔ "}
          {fight.name}
          {fight.difficulty !== undefined && (
            <span className="fight-tier" title={`Instance difficulty ${fight.difficulty}`}>
              T{fight.difficulty}
            </span>
          )}
        </span>
        <span className="fight-meta">
          {fmtClock(fight.beginTime)} · {fmtDuration(fight.beginTime, fight.lastDamageTime)} ·{" "}
          {fmtNum(fight.damageTotal)}
          <Share fight={fight} />
        </span>
      </button>
    );
  },
  (a, b) =>
    a.selected === b.selected &&
    a.onPick === b.onPick &&
    a.fight.id === b.fight.id &&
    a.fight.name === b.fight.name &&
    a.fight.closed === b.fight.closed &&
    a.fight.dead === b.fight.dead &&
    a.fight.difficulty === b.fight.difficulty &&
    a.fight.beginTime === b.fight.beginTime &&
    a.fight.lastDamageTime === b.fight.lastDamageTime &&
    a.fight.damageTotal === b.fight.damageTotal &&
    a.fight.estimatedHealth === b.fight.estimatedHealth,
);

/**
 * How much of the mob this fight actually accounted for: damage dealt against
 * what the app has learned this mob costs to kill (F25).
 *
 * <p>It answers "did we kill that, or did we get carried" — a fight showing 20%
 * of a mob's health is one somebody else finished, and the fight list is the
 * only place that is visible. Nothing renders until the mob has been killed
 * enough times to have a number; a share computed from one prior kill would be
 * noise wearing a percent sign.</p>
 *
 * <p>Over 100% is normal and left alone rather than clamped: the estimate is a
 * median, the killing blow overshoots, and a tougher-than-average pull really
 * did cost more than the typical one. Clamping would hide exactly the fights
 * worth looking at.</p>
 */
function Share({ fight }: { fight: FightInfo }) {
  if (!fight.estimatedHealth || !fight.dead) return null;

  const pct = (fight.damageTotal / fight.estimatedHealth) * 100;
  return (
    <span
      className={"fight-share" + (pct < 60 ? " partial" : "")}
      title={`${fmtNum(fight.damageTotal)} dealt of about ${fmtNum(fight.estimatedHealth)} — this mob's learned health here`}
    >
      {" · "}
      {pct.toFixed(0)}% of hp
    </span>
  );
}
