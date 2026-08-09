import { useEffect, useMemo, useState } from "react";
import { api, type IncomingHit, type MobAttackEstimate, type MobAttackReport } from "../api";
import { fmtClock, fmtNum, fmtRate } from "../format";
import { TableSearch } from "../dashboards/tableTools";
import { frameScope, type TimeFrame } from "../timeFrame";

interface Props {
  /** Null until the first fetch lands. */
  attacks: MobAttackReport | null;
  sessionId: string | null;
  /** The app-wide time frame — the feed reports over it like every other panel. */
  frame: TimeFrame;
  server: string;
}

/** Enough to read a death back several times over without being a log viewer. */
const FEED_LIMIT = 300;

/**
 * What is hitting you, and what it hits for (F26).
 *
 * <p>Two readings of the same stream, because they answer different questions
 * and neither substitutes for the other. The <b>feed</b> is the last few
 * hundred swings in the order they landed — "three parries, then a 900-point
 * crush, then nothing" is a story that no aggregation keeps, which is exactly
 * why this half is not a QuerySpec. The <b>profiles</b> are what the server has
 * learned across every log ever opened against it: how hard a thing hits, how
 * often it connects, and how far the hits spread.</p>
 *
 * <p>Profile rows are per <em>defender level</em>, not per mob. How hard
 * something hits is a fact about a pairing rather than about the mob, so a
 * level-40's numbers and a level-60's are two rows — and the panel opens on the
 * ones belonging to whoever is logged in.</p>
 */
export function IncomingPanel({ attacks, sessionId, frame, server }: Props) {
  const [ownerOnly, setOwnerOnly] = useState(true);
  const [feed, setFeed] = useState<IncomingHit[] | null>(null);
  const [feedTotal, setFeedTotal] = useState(0);
  const [search, setSearch] = useState("");
  const [mineOnly, setMineOnly] = useState(true);
  const [open, setOpen] = useState<string | null>(null);

  const scope = useMemo(() => frameScope(frame), [frame]);

  // The feed follows the time frame and the live tail both, so it refetches on
  // the same beat the charts do. Nothing is pushed for it: the server banks
  // profiles on its own tick and the raw stream is cheap to re-read.
  useEffect(() => {
    if (!sessionId) return;
    let cancelled = false;
    const load = () =>
      api
        .hits(sessionId, scope, { limit: FEED_LIMIT, ownerOnly })
        .then((result) => {
          if (cancelled) return;
          setFeed(result.hits);
          setFeedTotal(result.total);
        })
        .catch(() => undefined);
    load();
    const timer = window.setInterval(load, 2000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [sessionId, scope, ownerOnly]);

  const all = attacks?.mobs ?? [];
  const level = attacks?.characterLevel;

  const rows = useMemo(() => {
    const query = search.trim().toLowerCase();
    return all.filter((m) => {
      if (mineOnly && level !== undefined && m.defenderLevel !== level) return false;
      if (query.length === 0) return true;
      return (
        m.mob.toLowerCase().includes(query) ||
        m.zone.toLowerCase().includes(query) ||
        (m.tierName?.toLowerCase().includes(query) ?? false)
      );
    });
  }, [all, search, mineOnly, level]);

  if (attacks === null) {
    return (
      <div className="dashboard-main">
        <div className="panel">
          <div className="empty">Loading what this server's mobs hit for…</div>
        </div>
      </div>
    );
  }

  return (
    <div className="dashboard-main">
      <div className="panel hit-feed-panel">
        <div className="panel-title">
          <span className="panel-name">Recent hits</span>
          <span className="subtle">
            {feed === null
              ? "loading…"
              : feedTotal > feed.length
                ? `newest ${fmtNum(feed.length)} of ${fmtNum(feedTotal)} in the time frame`
                : `${fmtNum(feedTotal)} in the time frame`}
          </span>
        </div>
        <div className="mob-controls">
          <label className="mob-toggle" title="Only swings aimed at this log's own character">
            <input
              type="checkbox"
              checked={ownerOnly}
              onChange={(e) => setOwnerOnly(e.target.checked)}
            />
            just me
          </label>
        </div>
        <div className="mob-scroll hit-feed">
          {feed !== null && feed.length === 0 ? (
            <div className="empty">Nothing has hit you in this time frame.</div>
          ) : (
            <table className="mob-table hit-table">
              <thead>
                <tr>
                  <th>Time</th>
                  <th>Attacker</th>
                  {!ownerOnly && <th>Target</th>}
                  <th>Attack</th>
                  <th className="num">Damage</th>
                </tr>
              </thead>
              <tbody>
                {/* Newest first: the reason to open this panel is almost always
                    the thing that just happened, and scrolling to the bottom to
                    find it would be the wrong way round. */}
                {[...(feed ?? [])].reverse().map((hit, i) => (
                  <tr key={`${hit.at}|${i}`} className={hit.amount > 0 ? "" : "hit-avoided"}>
                    <td className="subtle">{fmtClock(hit.at)}</td>
                    <td className="mob-name">{hit.attacker}</td>
                    {!ownerOnly && (
                      <td className="subtle">
                        {hit.defender}
                        {hit.defenderOwner && hit.defenderOwner !== hit.defender && (
                          <span className="subtle"> ({hit.defenderOwner}'s)</span>
                        )}
                      </td>
                    )}
                    <td>
                      {hit.skill}
                      {hit.amount === 0 && (
                        <span className={`hit-outcome ${hit.outcome}`}> {hit.outcome}</span>
                      )}
                    </td>
                    <td className="num strong">{hit.amount > 0 ? fmtNum(hit.amount) : "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>

      <div className="panel">
        <div className="panel-title">
          <span className="panel-name">What they hit for — {attacks.server}</span>
          <span className="subtle">
            {all.length === 0
              ? "nothing learned yet"
              : `${fmtNum(all.length)} matchups from ${fmtNum(attacks.landed)} hits`}
          </span>
        </div>

        {all.length === 0 ? (
          <div className="mob-intro">
            <p>
              Nothing learned about what <strong>{server}</strong>'s mobs hit for yet. This is
              measured rather than looked up: every swing a mob throws at you is recorded, and
              the tally builds as you fight. Closed fights are banked a second or two after
              they end.
            </p>
            <p className="subtle">
              Rows are kept per zone, per instance difficulty <em>and per defender level</em> —
              because how hard something hits is a fact about the pairing, not about the mob.
              A level-40's numbers and a level-60's would average into something true of
              neither.
            </p>
          </div>
        ) : (
          <>
            <div className="mob-controls">
              <TableSearch
                value={search}
                onChange={setSearch}
                placeholder="Filter by mob, zone or tier…"
                shown={rows.length}
                total={all.length}
              />
              {level !== undefined && (
                <label
                  className="mob-toggle"
                  title={`Only what was measured against a level ${level} defender`}
                >
                  <input
                    type="checkbox"
                    checked={mineOnly}
                    onChange={(e) => setMineOnly(e.target.checked)}
                  />
                  my level ({level})
                </label>
              )}
              {level === undefined && (
                <span className="subtle" title="Type /who to fix it, or wait for a ding">
                  level unknown — showing every defender
                </span>
              )}
            </div>
            <div className="mob-scroll">
              <table className="mob-table">
                <thead>
                  <tr>
                    <th>Mob</th>
                    <th>Zone</th>
                    {attacks.instanced && <th>Tier</th>}
                    <th className="num">vs</th>
                    {/* Melee only, all four. A mob's damage shield and its
                        backstab average into a number describing neither, so
                        the spells sit in the breakdown instead. */}
                    <th className="num" title="Melee only — spells and shields are in the breakdown">
                      Avg swing
                    </th>
                    <th className="num">Range</th>
                    <th className="num">Max</th>
                    <th className="num">Lands</th>
                    <th className="num">Swings</th>
                    <th>Confidence</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((m) => {
                    const key = keyOf(m);
                    const expanded = open === key;
                    return [
                      <tr
                        key={key}
                        className="hit-row"
                        onClick={() => setOpen(expanded ? null : key)}
                        title="Show each attack on its own"
                      >
                        <td className="mob-name">
                          <span className="expander">{expanded ? "▾" : "▸"}</span>
                          {m.mob}
                        </td>
                        <td className="subtle">{m.zone}</td>
                        {attacks.instanced && <td>{tierLabel(m)}</td>}
                        <td className="num subtle">{m.defenderLevel ?? "?"}</td>
                        <td className="num strong">{fmtNum(m.avgHit)}</td>
                        <td className="num subtle">
                          {fmtNum(m.floor)}–{fmtNum(m.ceiling)}
                        </td>
                        <td className="num">{fmtNum(m.maxHit)}</td>
                        <td className="num" title={avoidanceTitle(m)}>
                          {m.swings > 0 ? fmtRate(m.hitRate) : "—"}
                        </td>
                        <td className="num subtle" title={`${m.fights} fights`}>
                          {fmtNum(m.swings)}
                        </td>
                        <td>
                          <span className={`mob-confidence ${m.confidence}`}>{m.confidence}</span>
                        </td>
                      </tr>,
                      ...(expanded ? [<SkillRows key={key + "|skills"} mob={m} tiers={attacks.instanced} />] : []),
                    ];
                  })}
                  {rows.length === 0 && (
                    <tr>
                      <td colSpan={attacks.instanced ? 10 : 9} className="empty">
                        Nothing matches.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

/**
 * One mob's attacks broken out. A single "avg hit" across a mob that both
 * crushes and breathes fire is an average of two different things, so the
 * breakdown is where the number becomes usable.
 */
function SkillRows({ mob, tiers }: { mob: MobAttackEstimate; tiers: boolean }) {
  return (
    <tr className="hit-skills">
      <td colSpan={tiers ? 10 : 9}>
        <table className="mob-table">
          <tbody>
            {mob.skills.map((s) => (
              <tr key={s.skill}>
                <td className="mob-name">
                  {s.skill}
                  {s.spell && <span className="subtle"> spell</span>}
                </td>
                <td className="num strong">{fmtNum(s.avgHit)}</td>
                <td className="num subtle">
                  {fmtNum(s.floor)}–{fmtNum(s.ceiling)}
                </td>
                <td className="num">{fmtNum(s.maxHit)}</td>
                {/* A spell has no attempt anyone can dodge, so it has no hit
                    rate — an em dash rather than a 100% that would read as
                    "unavoidable" when it means "not applicable". */}
                <td className="num">{s.spell ? "—" : fmtRate(s.hitRate)}</td>
                <td className="num subtle">{fmtNum(s.landed)} hits</td>
                <td className="num subtle">{fmtNum(s.total)} total</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="subtle hit-note">
          Measured against {mob.defenders.join(", ")}
          {mob.defenderLevel === undefined && " (level never established)"}. Of{" "}
          {fmtNum(mob.total)} points taken, {fmtNum(mob.meleeTotal)} came from{" "}
          {fmtNum(mob.meleeHits)} landed swings and {fmtNum(mob.spellTotal)} from spells and
          shields — which is why the columns above are melee only. Rates are over the swings
          the log accounted for: a swing you riposted is written as your counter-attack and
          never as an attempt, so it is in neither column.
        </p>
      </td>
    </tr>
  );
}

function avoidanceTitle(m: MobAttackEstimate): string {
  return [
    `miss ${fmtRate(m.missRate)}`,
    `dodge ${fmtRate(m.dodgeRate)}`,
    `parry ${fmtRate(m.parryRate)}`,
    `block ${fmtRate(m.blockRate)}`,
    `absorb ${fmtRate(m.absorbRate)}`,
  ].join(" · ");
}

function tierLabel(m: MobAttackEstimate): string {
  if (m.difficulty === undefined) return "open world";
  return m.tierName ? `${m.difficulty} (${m.tierName})` : String(m.difficulty);
}

function keyOf(m: MobAttackEstimate): string {
  return `${m.mob}|${m.zone}|${m.difficulty ?? "-"}|${m.defenderLevel ?? "-"}`;
}
