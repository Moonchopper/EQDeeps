import { Fragment } from "react";
import type { FightInfo } from "../api";
import { fmtClock, fmtDuration, fmtNum } from "../format";

interface Props {
  fights: FightInfo[];
  selected: number[];
  followLive: boolean;
  onSelect: (ids: number[]) => void;
  onFollowLive: (follow: boolean) => void;
}

/**
 * Chronological fight list with pull-chain grouping: a "break" divider renders
 * between groups. Click selects a fight; ctrl/cmd-click toggles; clicking a
 * group header selects the whole pull chain.
 */
export function FightList({ fights, selected, followLive, onSelect, onFollowLive }: Props) {
  const selectedSet = new Set(selected);

  const toggle = (id: number, additive: boolean) => {
    onFollowLive(false);
    if (!additive) {
      onSelect([id]);
      return;
    }
    const next = new Set(selectedSet);
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    onSelect([...next]);
  };

  const selectGroup = (groupIndex: number) => {
    onFollowLive(false);
    onSelect(fights.filter((f) => f.groupIndex === groupIndex).map((f) => f.id));
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
          title="Select the whole pull chain"
        >
          — pull chain {fight.groupIndex + 1} —
        </button>,
      );
    }
    rows.push(
      <button
        key={fight.id}
        className={
          "fight-row" +
          (selectedSet.has(fight.id) ? " selected" : "") +
          (!fight.closed ? " active" : "")
        }
        onClick={(e) => toggle(fight.id, e.ctrlKey || e.metaKey)}
      >
        <span className="fight-name">
          {fight.dead ? "☠ " : fight.closed ? "" : "⚔ "}
          {fight.name}
        </span>
        <span className="fight-meta">
          {fmtClock(fight.beginTime)} · {fmtDuration(fight.beginTime, fight.lastDamageTime)} ·{" "}
          {fmtNum(fight.damageTotal)}
        </span>
      </button>,
    );
  }

  const selectAll = () => {
    onFollowLive(false);
    onSelect(fights.map((f) => f.id));
  };

  return (
    <div className="panel fight-list">
      <div className="panel-title">
        <span>Fights</span>
        <span className="fight-actions">
          <button
            className="mini-btn"
            onClick={selectAll}
            title="Aggregate every fight in the log"
            disabled={fights.length === 0}
          >
            select all
          </button>
          <label className="follow-live">
            <input
              type="checkbox"
              checked={followLive}
              onChange={(e) => onFollowLive(e.target.checked)}
            />
            follow live
          </label>
        </span>
      </div>
      <div className="fight-scroll">
        {rows.length > 0 ? <Fragment>{rows}</Fragment> : <div className="empty">No fights yet</div>}
      </div>
    </div>
  );
}
