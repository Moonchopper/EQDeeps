import { Fragment, useEffect, useMemo, useState } from "react";
import { api, type IncomingHit, type MobAttackEstimate, type MobAttackReport } from "../api";
import { fmtClock, fmtNum, fmtRate, fmtWhen } from "../format";
import { TableSearch } from "../dashboards/tableTools";
import { frameScope, type TimeFrame } from "../timeFrame";
import { LookupLink } from "../lookup/LookupLink";

/** Mob rows rendered before the table asks you to filter or show all. */
const MOB_ROWS_SHOWN = 200;

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
 * level-40's numbers and a level-60's are two rows.</p>
 *
 * <p><b>On EQ Legends a character is several levels at once.</b> Class
 * loadouts level independently, and swapping between them is not logged at all
 * — so one log legitimately dings to 41, then to 11, then back up, and every
 * one of those is the same character. The level axis therefore doubles as a
 * loadout axis, which is right: a different loadout is a different class with
 * different mitigation, and its numbers belong in their own rows.</p>
 *
 * <p>What it is NOT is a single "my level". This panel used to default to one,
 * taken from the most recent ding, which on a three-loadout character hid two
 * thirds of the evidence and read as data simply missing. The level control is
 * a picker that shows everything by default, and anything it hides it says
 * out loud.</p>
 */
export function IncomingPanel({ attacks, sessionId, frame, server }: Props) {
  const [ownerOnly, setOwnerOnly] = useState(true);
  const [feed, setFeed] = useState<IncomingHit[] | null>(null);
  const [feedTotal, setFeedTotal] = useState(0);
  const [search, setSearch] = useState("");
  /** A level to narrow to, or "" for all of them. See {@link levelChoices}. */
  const [onlyLevel, setOnlyLevel] = useState<string>("");
  // The table is capped and says so. A server's worth of mobs is thousands
  // of rows — 2,645 on the owner's log, 55,000 elements — and rendering them
  // all made this view a two-second switch for a list nobody scrolls to the
  // bottom of; the rows come newest-first, so the cap keeps what is hitting
  // you now, and the filter or "show all" gets to the rest.
  const [showAll, setShowAll] = useState(false);
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

  const levels = useMemo(() => levelChoices(all), [all]);

  const rows = useMemo(() => {
    const query = search.trim().toLowerCase();
    return all.filter((m) => {
      if (onlyLevel !== "" && String(m.defenderLevel ?? "") !== onlyLevel) return false;
      if (query.length === 0) return true;
      return (
        m.mob.toLowerCase().includes(query) ||
        m.zone.toLowerCase().includes(query) ||
        (m.tierName?.toLowerCase().includes(query) ?? false)
      );
    });
  }, [all, search, onlyLevel]);

  /** Rows the level picker alone is hiding, so the panel can own up to it. */
  const hiddenByLevel = useMemo(
    () =>
      onlyLevel === ""
        ? 0
        : all.filter((m) => String(m.defenderLevel ?? "") !== onlyLevel).length,
    [all, onlyLevel],
  );

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
                    <td className="mob-name">
                      {hit.attacker}
                      <LookupLink kind="npc" name={hit.attacker} />
                    </td>
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
              {/* A picker rather than a "my level" toggle. On EQ Legends a
                  character is several levels at once — each class loadout
                  levels on its own — so there is no single number "mine" could
                  mean, and defaulting to one silently hid every row from the
                  other loadouts. */}
              {levels.length > 1 && (
                <label className="mob-toggle" title="Narrow to what one loadout was measured against">
                  level
                  <select
                    className="panel-select"
                    value={onlyLevel}
                    onChange={(e) => setOnlyLevel(e.target.value)}
                  >
                    <option value="">all ({all.length})</option>
                    {levels.map((l) => (
                      <option key={l.key} value={l.key}>
                        {l.label} ({l.count})
                      </option>
                    ))}
                  </select>
                </label>
              )}
              {hiddenByLevel > 0 && (
                <button className="mini-btn" onClick={() => setOnlyLevel("")}>
                  {fmtNum(hiddenByLevel)} at other levels hidden — show all
                </button>
              )}
              {level !== undefined && (
                <span className="subtle" title="The most recent level this log announced">
                  last ding: {level}
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
                    {/* Melee only, these four. A mob's damage shield and its
                        backstab average into a number describing neither, so
                        the spells are broken out per attack instead. */}
                    <th className="num" title="Melee only — expand a row to see spells and shields">
                      Avg swing
                    </th>
                    <th className="num">Range</th>
                    <th className="num">Max</th>
                    <th className="num">Lands</th>
                    <th className="num">Swings</th>
                    {/* These two count everything, so an expanded row's
                        attacks add up to the row above them. */}
                    <th className="num" title="Everything that landed, spells and shields included">
                      Hits
                    </th>
                    <th className="num">Total</th>
                    <th>Confidence</th>
                    {/* The list is ordered by this, so it has to be on screen
                        — a ranking whose key is invisible reads as no order at
                        all. */}
                    <th className="num">Last fought</th>
                  </tr>
                </thead>
                <tbody>
                  {(showAll ? rows : rows.slice(0, MOB_ROWS_SHOWN)).map((m) => {
                    const key = keyOf(m);
                    const expanded = open === key;
                    return (
                      <Fragment key={key}>
                        <tr
                          className="hit-row"
                          onClick={() => setOpen(expanded ? null : key)}
                          title="Show each attack on its own"
                        >
                          <td className="mob-name">
                            <span className="expander">{expanded ? "▾" : "▸"}</span>
                            {m.mob}
                            <LookupLink kind="npc" name={m.mob} />
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
                          <td className="num subtle">{fmtNum(m.landed)}</td>
                          <td className="num subtle">{fmtNum(m.total)}</td>
                          <td>
                            <span className={`mob-confidence ${m.confidence}`}>
                              {m.confidence}
                            </span>
                          </td>
                          <td className="num subtle" title={`first fought ${fmtWhen(m.firstSeen)}`}>
                            {fmtWhen(m.lastSeen)}
                          </td>
                        </tr>
                        {expanded && <SkillRows mob={m} tiers={attacks.instanced} />}
                      </Fragment>
                    );
                  })}
                  {rows.length === 0 && (
                    <tr>
                      <td colSpan={columnCount(attacks.instanced)} className="empty">
                        Nothing matches.
                      </td>
                    </tr>
                  )}
                  {!showAll && rows.length > MOB_ROWS_SHOWN && (
                    <tr>
                      <td colSpan={columnCount(attacks.instanced)} className="empty">
                        The {fmtNum(MOB_ROWS_SHOWN)} most recently fought of {fmtNum(rows.length)} — filter to
                        narrow, or{" "}
                        <button className="mini-btn" onClick={() => setShowAll(true)}>
                          show all {fmtNum(rows.length)}
                        </button>
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

/** One entry in the level picker. */
interface LevelChoice {
  /** The `defenderLevel` as a string, or "" for the unknown-level bucket. */
  key: string;
  label: string;
  count: number;
}

/**
 * The defender levels present, most recently fought first.
 *
 * <p>Ordered by recency rather than numerically because on EQ Legends these are
 * loadouts, not a progression: a character who plays a level-50 paladin and a
 * level-12 necromancer in one evening is both, and the one they were just
 * playing is the one they want. Sorting 12 above 50 would put the loadout they
 * have not touched since Tuesday at the top half the time.</p>
 */
function levelChoices(rows: MobAttackEstimate[]): LevelChoice[] {
  const seen = new Map<string, { label: string; count: number; last: string }>();
  for (const row of rows) {
    const key = String(row.defenderLevel ?? "");
    const found = seen.get(key);
    if (found) {
      found.count++;
      if (row.lastSeen > found.last) found.last = row.lastSeen;
    } else {
      seen.set(key, {
        label: row.defenderLevel === undefined ? "unknown" : String(row.defenderLevel),
        count: 1,
        last: row.lastSeen,
      });
    }
  }

  return [...seen.entries()]
    .map(([key, v]) => ({ key, label: v.label, count: v.count, last: v.last }))
    .sort((a, b) => (a.last < b.last ? 1 : a.last > b.last ? -1 : 0))
    .map(({ key, label, count }) => ({ key, label, count }));
}

/** Mob, Zone, [Tier,] vs, Avg swing, Range, Max, Lands, Swings, Hits, Total, Confidence, Last fought. */
function columnCount(instanced: boolean): number {
  return instanced ? 13 : 12;
}

/**
 * One mob's attacks broken out. A single "avg swing" across a mob that both
 * crushes and breathes fire is an average of two different things, so the
 * breakdown is where the number becomes usable.
 *
 * <p>These are rows of the SAME table as the matchup above them, not a nested
 * one. A nested table lays its columns out on its own content, so "68" landed
 * under "Zone" and "44.3%" under "Avg swing" — every figure sitting beneath a
 * header that meant something else. Sharing the parent's columns is what makes
 * a breakdown readable, and it is also what makes the arithmetic checkable:
 * the Hits and Total columns count everything, so the attacks add up to the
 * row they came from.</p>
 */
function SkillRows({ mob, tiers }: { mob: MobAttackEstimate; tiers: boolean }) {
  return (
    <>
      {mob.skills.map((s) => (
        <tr key={s.skill} className="child-row hit-skill">
          <td className="mob-name">
            <span className="expander-spacer" />
            {s.skill}
            {s.spell && <span className="subtle"> spell</span>}
          </td>
          {/* Zone, tier and defender level belong to the matchup, not to one
              of its attacks — blank rather than repeated down the group. */}
          <td />
          {tiers && <td />}
          <td />
          <td className="num strong">{fmtNum(s.avgHit)}</td>
          <td className="num subtle">
            {fmtNum(s.floor)}–{fmtNum(s.ceiling)}
          </td>
          <td className="num">{fmtNum(s.maxHit)}</td>
          {/* A spell has no attempt anyone can dodge, so it has neither a hit
              rate nor swings — em dashes rather than a 100% that would read as
              "unavoidable" when it means "not applicable". */}
          <td className="num">{s.spell ? "—" : fmtRate(s.hitRate)}</td>
          <td className="num subtle">{s.spell ? "—" : fmtNum(s.swings)}</td>
          <td className="num subtle">{fmtNum(s.landed)}</td>
          <td className="num subtle">{fmtNum(s.total)}</td>
          <td />
          <td />
        </tr>
      ))}
      <tr className="child-row hit-note-row">
        <td colSpan={columnCount(tiers)}>
          Measured against {mob.defenders.join(", ")}
          {mob.defenderLevel === undefined && " (level never established)"}. Of{" "}
          {fmtNum(mob.total)} points taken, {fmtNum(mob.meleeTotal)} came from{" "}
          {fmtNum(mob.meleeHits)} landed swings and {fmtNum(mob.spellTotal)} from spells and
          shields — which is why Avg swing, Range, Max and Lands are melee only. Rates are
          over the swings the log accounted for: a swing you riposted is written as your
          counter-attack and never as an attempt, so it is in neither column.
        </td>
      </tr>
    </>
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
