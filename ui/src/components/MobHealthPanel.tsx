import { useMemo, useState } from "react";
import type { MobHealthEstimate, MobHealthReport } from "../api";
import { SERIES_COLORS, fmtNum, fmtWhen } from "../format";
import { TableSearch, meterStyle } from "../dashboards/tableTools";

interface Props {
  /** Null until the first fetch lands. */
  mobs: MobHealthReport | null;
  /** Shown when nothing has been learned yet, so the emptiness has a reason. */
  server: string;
}

/**
 * What this server's mobs are worth (F25), and what a difficulty tier actually
 * costs.
 *
 * <p>Two readings of one body of evidence, because they answer different
 * questions. The table answers "how big is this thing" for one mob at one
 * difficulty. The ladder answers "what does tier 3 buy me" — the same mob
 * across every tier it has been fought at, which is the question a player asks
 * before choosing which instance to make and which no per-row number can
 * answer.</p>
 *
 * <p>Every number here is damage-to-kill, which is health plus whatever the
 * killing blow overshot by. The band is shown beside it rather than behind a
 * tooltip for that reason: a single number would read as a measurement, and
 * this is an estimate with a spread.</p>
 */
export function MobHealthPanel({ mobs, server }: Props) {
  const [search, setSearch] = useState("");
  const [ladderOnly, setLadderOnly] = useState(false);

  const all = mobs?.mobs ?? [];

  /** Mobs fought at more than one difficulty — the ones a ladder can be drawn for. */
  const ladders = useMemo(() => byMob(all).filter((l) => l.rungs.length > 1), [all]);
  const ladderNames = useMemo(
    () => new Set(ladders.map((l) => l.mob.toLowerCase())),
    [ladders],
  );

  const rows = useMemo(() => {
    const query = search.trim().toLowerCase();
    return all.filter((m) => {
      if (ladderOnly && !ladderNames.has(m.mob.toLowerCase())) return false;
      if (query.length === 0) return true;
      return (
        m.mob.toLowerCase().includes(query) ||
        m.zone.toLowerCase().includes(query) ||
        (m.tierName?.toLowerCase().includes(query) ?? false)
      );
    });
  }, [all, search, ladderOnly, ladderNames]);

  const shownLadders = useMemo(() => {
    const query = search.trim().toLowerCase();
    if (query.length === 0) return ladders;
    return ladders.filter(
      (l) => l.mob.toLowerCase().includes(query) || l.zone.toLowerCase().includes(query),
    );
  }, [ladders, search]);

  if (mobs === null) {
    return (
      <div className="dashboard-main">
        <div className="panel">
          <div className="empty">Loading what this server's mobs are worth…</div>
        </div>
      </div>
    );
  }

  if (all.length === 0) {
    return (
      <div className="dashboard-main">
        <div className="panel mob-intro">
          <div className="panel-title">
            <span className="panel-name">Mob health</span>
          </div>
          <p>
            Nothing learned about <strong>{server}</strong> yet. Health here is not looked up
            anywhere — it is measured, from the damage a mob absorbs between the first hit and
            the line that says it died. Kill some things and they will appear.
          </p>
          <p className="subtle">
            Each mob is tracked per zone <em>and per instance difficulty</em>, because a tier 4
            instance rescales everything in it. The open world and a tier-0 instance share a
            bucket: the log writes them identically, and they are the same content.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="dashboard-main">
      <div className="panel">
        <div className="panel-title">
          <span className="panel-name">Mob health — {mobs.server}</span>
          <span className="subtle">
            {fmtNum(all.length)} mobs from {fmtNum(mobs.kills)} kills
          </span>
        </div>
        <div className="mob-controls">
          <TableSearch
            value={search}
            onChange={setSearch}
            placeholder="Filter by mob, zone or tier…"
            shown={rows.length}
            total={all.length}
          />
          {mobs.instanced && ladders.length > 0 && (
            <label className="mob-toggle" title="Only mobs fought at more than one difficulty">
              <input
                type="checkbox"
                checked={ladderOnly}
                onChange={(e) => setLadderOnly(e.target.checked)}
              />
              comparable across tiers
            </label>
          )}
        </div>
        <div className="mob-scroll">
          <table className="mob-table">
            <thead>
              <tr>
                <th>Mob</th>
                <th>Zone</th>
                {mobs.instanced && <th>Tier</th>}
                <th className="num">Health</th>
                <th className="num">Range</th>
                <th className="num">Kills</th>
                <th>Confidence</th>
                <th className="num">Last seen</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((m) => (
                <tr key={keyOf(m)}>
                  <td className="mob-name">{m.mob}</td>
                  <td className="subtle">{m.zone}</td>
                  {mobs.instanced && <td>{tierLabel(m)}</td>}
                  <td className="num strong">{fmtNum(m.health)}</td>
                  <td className="num subtle">
                    {fmtNum(m.floor)}–{fmtNum(m.ceiling)}
                  </td>
                  <td className="num" title={`${m.cleanSamples} used after the merged-fight filter`}>
                    {m.cleanSamples < m.samples
                      ? `${fmtNum(m.cleanSamples)} / ${fmtNum(m.samples)}`
                      : fmtNum(m.samples)}
                  </td>
                  <td>
                    <span className={`mob-confidence ${m.confidence}`}>{m.confidence}</span>
                  </td>
                  <td className="num subtle">{fmtWhen(m.lastKilled)}</td>
                </tr>
              ))}
              {rows.length === 0 && (
                <tr>
                  <td colSpan={mobs.instanced ? 8 : 7} className="empty">
                    Nothing matches.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      {shownLadders.length > 0 && (
        <div className="panel ladder-panel">
          <div className="panel-title">
            <span className="panel-name">What a tier costs</span>
            <span className="subtle">
              same mob, same zone, every difficulty it has been fought at
            </span>
          </div>
          <div className="mob-scroll">
            {shownLadders.map((ladder) => (
              <Ladder key={`${ladder.mob}|${ladder.zone}`} ladder={ladder} />
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

/** One mob in one zone, and every difficulty it has been fought at. */
interface MobLadder {
  mob: string;
  zone: string;
  rungs: MobHealthEstimate[];
}

/**
 * Bars are drawn against the biggest rung rather than a fixed scale, so the
 * shape of the climb is legible whether the mob is worth 500 or 12,000. The
 * multiplier beside each rung is against the lowest one, since "what does this
 * tier cost me over the cheapest thing I could have made" is the actual
 * question.
 */
function Ladder({ ladder }: { ladder: MobLadder }) {
  const max = Math.max(...ladder.rungs.map((r) => r.health));
  const base = ladder.rungs[0];

  return (
    <div className="mob-ladder">
      <div className="mob-ladder-head">
        <span className="mob-name">{ladder.mob}</span>
        <span className="subtle">{ladder.zone}</span>
      </div>
      {ladder.rungs.map((rung) => (
        <div className="mob-rung" key={keyOf(rung)} style={meterStyle(SERIES_COLORS[1], (rung.health / max) * 100)}>
          <span className="mob-rung-tier">{tierLabel(rung)}</span>
          <span className="mob-rung-health">{fmtNum(rung.health)}</span>
          <span className="mob-rung-mult subtle">
            {rung === base ? "baseline" : `×${(rung.health / base.health).toFixed(2)}`}
          </span>
          <span className={`mob-confidence ${rung.confidence}`}>{rung.confidence}</span>
        </div>
      ))}
    </div>
  );
}

/**
 * Groups estimates into ladders, ordered by difficulty with the open world
 * first — it is the bottom of the ladder even though it carries no number,
 * since a tier-0 instance and the open world are the same content.
 */
function byMob(estimates: MobHealthEstimate[]): MobLadder[] {
  const groups = new Map<string, MobLadder>();
  for (const estimate of estimates) {
    const key = `${estimate.mob.toLowerCase()}|${estimate.zone.toLowerCase()}`;
    let ladder = groups.get(key);
    if (!ladder) {
      ladder = { mob: estimate.mob, zone: estimate.zone, rungs: [] };
      groups.set(key, ladder);
    }
    ladder.rungs.push(estimate);
  }

  const ladders = [...groups.values()];
  for (const ladder of ladders) {
    ladder.rungs.sort((a, b) => (a.difficulty ?? 0) - (b.difficulty ?? 0));
  }

  return ladders.sort((a, b) => b.rungs.length - a.rungs.length || a.mob.localeCompare(b.mob));
}

function tierLabel(estimate: MobHealthEstimate): string {
  if (estimate.difficulty === undefined) return "open world";
  return estimate.tierName ? `${estimate.difficulty} · ${estimate.tierName}` : `${estimate.difficulty}`;
}

function keyOf(estimate: MobHealthEstimate): string {
  return `${estimate.mob}|${estimate.zone}|${estimate.difficulty ?? "-"}`;
}
