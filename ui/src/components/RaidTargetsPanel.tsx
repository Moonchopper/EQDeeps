import { Fragment, useEffect, useMemo, useState } from "react";
import { api, type QueryResult, type RaidTargetDto } from "../api";
import { fmtNum, fmtWhen } from "../format";
import { LookupLink } from "../lookup/LookupLink";
import { mobKey } from "../lookup/mobKey";
import { TableSearch, Highlight } from "../dashboards/tableTools";
import { fuzzyMatch, type FuzzyHit } from "../fuzzy";
import type { BestiaryTarget, Crumb } from "../trail";

/**
 * "Every death in the log" has no direct scope expression (ADR-022 Decision 4;
 * see the F31-3 brief's Recon §4a for the full argument). An empty scope on a
 * deaths query aggregates per FIGHT span (QueryEngine.ResolveScope's
 * fallback branch), which drops a kill with no recorded damage against it —
 * FightTracker only extends a fight already active for that victim, so an
 * instant kill nobody hit first, or one that predates this app watching,
 * never opens one and sits outside every unit an empty scope can build. The
 * engine's only scope that reads the raw record stream with no fight
 * involvement at all is `lastSeconds`, an absolute lookback anchored to the
 * newest record — which this view uses instead of the app-wide time frame
 * (it has none anyway: Raid targets lives in the World group, ADR-017
 * Decision 2, which hides the frame controls entirely).
 *
 * 50 years safely spans any real EverQuest log — the game has shipped logs
 * since 1999 — while staying far short of `DateTime.MinValue`, so the
 * engine's `latest.AddSeconds(-(lastSeconds - 1))` can never underflow.
 * `RaidTargetsWholeLogScopeTests` proves the underlying mechanism (with a
 * bound sized to that test's own few-second log, not this one).
 */
const WHOLE_LOG_LAST_SECONDS = 50 * 365 * 24 * 60 * 60;

/** One roster target, aggregated across every logged spelling that matched it (C4: sums under the shared key). */
interface TargetAgg {
  deaths: number;
  credited: number;
  firstAt: number;
  lastAt: number;
  /** Difficulty labels actually killed at, for lighting this target's rungs. */
  lit: Set<string>;
}

/**
 * Ranks a difficulty label the way the ladder wants it (Recon §3): Open
 * world first, then a bare tier with no mode, then Solo, then Group — a
 * label with no tier number before one with a number, tier ascending within
 * a mode — then a shape this parser does not recognise (still a rung, never
 * dropped, per the log-format doc's rule for the tiers themselves), and
 * "(unknown)" last of all.
 */
function rankDifficulty(label: string): [rank: number, tier: number] {
  if (label === "Open world") return [0, -1];
  if (label === "(unknown)") return [4, -1];

  const bare = /^(\d+) \(.+\)$/.exec(label);
  if (bare) return [1, Number(bare[1])];

  const moded = /^(Solo|Group)(?: (\d+) \(.+\))?$/.exec(label);
  if (moded) {
    const rank = moded[1] === "Solo" ? 2 : 3;
    return [rank, moded[2] !== undefined ? Number(moded[2]) : -1];
  }

  return [3.5, -1]; // unrecognised shape: after Group, before "(unknown)"
}

function compareDifficulty(a: string, b: string): number {
  const [ra, ta] = rankDifficulty(a);
  const [rb, tb] = rankDifficulty(b);
  return ra - rb || ta - tb;
}

/** Copied verbatim from PanelBody.tsx's firstAt/lastAt rendering (ADR-022 Decision 5): a wall-clock value, never a real instant. */
function fmtEpoch(value: number): string {
  if (value <= 0) return "—";
  return fmtWhen(new Date(value * 1000).toISOString().slice(0, 19));
}

/**
 * Raid targets (F31, ADR-022): a hand-authored roster of named raid mobs laid
 * over an ordinary deaths query grouped by player then difficulty, so the
 * targets this character has killed sit beside the ones still standing — the
 * same shape the Bestiary already has, someone's list and our measurements
 * joined on screen.
 *
 * <p><b>The join is client-side.</b> The query engine's `Values` filter is
 * Ordinal-exact against the raw logged victim string, whose case and leading
 * article are not predictable, so it cannot be trusted not to drop a
 * legitimate row. This view fetches every death in the log unfiltered and
 * matches it to a roster row under the same article-stripped, case-folded
 * key the Bestiary already uses (`mobKey`) — rows matched under a target's
 * name and under an alias sum into one target.</p>
 *
 * <p>No app-wide time frame: the World group has none (ADR-017 Decision 2),
 * and "have I ever killed this" is a lifetime question anyway — see
 * `WHOLE_LOG_LAST_SECONDS`.</p>
 */
export function RaidTargetsPanel({
  sessionId,
  onOpenMob,
}: {
  sessionId: string | null;
  /** To the Bestiary, on this mob, leaving this page behind as a crumb. */
  onOpenMob?: (target: Omit<BestiaryTarget, "seq">, from: Crumb) => void;
}) {
  const [roster, setRoster] = useState<RaidTargetDto[] | null>(null);
  const [result, setResult] = useState<QueryResult | null>(null);
  const [query, setQuery] = useState("");
  const [defeatedOnly, setDefeatedOnly] = useState(false);

  // The roster is static and server-wide — fetched once, not per session.
  useEffect(() => {
    let cancelled = false;
    api
      .raidTargets()
      .then((r) => !cancelled && setRoster(r.targets))
      .catch(() => !cancelled && setRoster([]));
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!sessionId) {
      setResult(null);
      return;
    }
    let cancelled = false;
    api
      .query(sessionId, {
        source: "deaths",
        scope: { lastSeconds: WHOLE_LOG_LAST_SECONDS },
        groupBy: ["player", "difficulty"],
        metrics: ["deaths", "credited", "firstAt", "lastAt"],
      })
      .then((r) => !cancelled && setResult(r))
      .catch(() => !cancelled && setResult(null));
    return () => {
      cancelled = true;
    };
  }, [sessionId]);

  // The join (C2, C4): every top-level row is one logged victim spelling: its
  // own metrics already sum every difficulty it was killed at (the query
  // engine aggregates every level of the tree), so only the difficulty
  // CHILDREN need walking, to know which rungs to light.
  const byTarget = useMemo(() => {
    const agg = new Map<string, TargetAgg>();
    const keyToName = new Map<string, string>();
    for (const t of roster ?? []) {
      keyToName.set(mobKey(t.name), t.name);
      for (const alias of t.aliases) keyToName.set(mobKey(alias), t.name);
    }
    for (const row of result?.rows ?? []) {
      const name = keyToName.get(mobKey(row.label));
      if (!name) continue; // not a raid target — e.g. "Cleric of Innoruuk" must light nothing
      const entry = agg.get(name) ?? { deaths: 0, credited: 0, firstAt: 0, lastAt: 0, lit: new Set<string>() };
      entry.deaths += row.metrics.deaths ?? 0;
      entry.credited += row.metrics.credited ?? 0;
      const first = row.metrics.firstAt ?? 0;
      const last = row.metrics.lastAt ?? 0;
      if (first > 0 && (entry.firstAt === 0 || first < entry.firstAt)) entry.firstAt = first;
      if (last > entry.lastAt) entry.lastAt = last;
      for (const child of row.children ?? []) entry.lit.add(child.key);
      agg.set(name, entry);
    }
    return agg;
  }, [roster, result]);

  // The ladder is one shared set of rungs, not per target (C5): the union of
  // difficulty labels actually seen on any roster target in this log, so a
  // target never killed shows the same rungs as one that fell, all unlit.
  const rungs = useMemo(() => {
    const labels = new Set<string>();
    for (const entry of byTarget.values()) {
      for (const l of entry.lit) labels.add(l);
    }
    return [...labels].sort(compareDifficulty);
  }, [byTarget]);

  const anyDefeated = useMemo(
    () => (roster ?? []).some((t) => (byTarget.get(t.name)?.deaths ?? 0) > 0),
    [roster, byTarget],
  );
  const defeatedCount = useMemo(
    () => (roster ?? []).filter((t) => (byTarget.get(t.name)?.deaths ?? 0) > 0).length,
    [roster, byTarget],
  );

  // Flat-list fuzzy filter (ItemFeedPanel's idiom, not filterTree — this is a
  // client-side list of a few dozen names, not a server-searched tree).
  const filteredRows = useMemo(() => {
    const q = query.trim();
    const all = roster ?? [];
    const withHits: { target: RaidTargetDto; hit: FuzzyHit | undefined }[] = q
      ? all
          .map((target) => ({
            target,
            hit: fuzzyMatch(target.name, q) ?? fuzzyMatch(target.zone, q) ?? fuzzyMatch(target.group, q) ?? undefined,
          }))
          .filter((r) => r.hit !== undefined)
      : all.map((target) => ({ target, hit: undefined }));
    return defeatedOnly
      ? withHits.filter((r) => (byTarget.get(r.target.name)?.deaths ?? 0) > 0)
      : withHits;
  }, [roster, query, defeatedOnly, byTarget]);

  // Grouped under the roster's own `group` column, in file order — the
  // order the first matching row of each group is encountered in, since the
  // roster itself is read and served in file order throughout.
  const groups = useMemo(() => {
    const order: string[] = [];
    const rows = new Map<string, typeof filteredRows>();
    for (const row of filteredRows) {
      if (!rows.has(row.target.group)) {
        rows.set(row.target.group, []);
        order.push(row.target.group);
      }
      rows.get(row.target.group)!.push(row);
    }
    return order.map((name) => ({ name, rows: rows.get(name)! }));
  }, [filteredRows]);

  const crumbHere = (): Crumb => ({ view: "raid-targets", label: "Raid targets" });

  if (!sessionId) {
    return <div className="empty">No log open. The roster appears once one is.</div>;
  }

  const loading = roster === null || result === null;

  return (
    <div className="dashboard-main raid-targets">
      <div className="panel">
        <div className="panel-title">
          <span className="panel-name">Raid targets</span>
          <span className="subtle">
            {loading ? "loading…" : `${fmtNum(defeatedCount)} of ${fmtNum((roster ?? []).length)} defeated`}
          </span>
        </div>
        {!loading && (
          <div className="raid-controls">
            <TableSearch
              value={query}
              onChange={setQuery}
              placeholder="Search targets, zones or groups…"
              shown={filteredRows.length}
              total={(roster ?? []).length}
            />
            <label className="raid-toggle">
              <input
                type="checkbox"
                checked={defeatedOnly}
                onChange={(e) => setDefeatedOnly(e.target.checked)}
              />
              defeated only
            </label>
          </div>
        )}
        <div className="table-scroll">
          {loading ? (
            <div className="empty-loading" />
          ) : groups.length === 0 ? (
            <div className="empty">{anyDefeated ? "Nothing matches." : "No roster kills in this log yet."}</div>
          ) : (
            // One table, one header, so a column is one x-position for the
            // whole roster — a group heading is a full-width row inside the
            // body rather than a table of its own, which is what let each
            // group size its own columns independently. A group with every
            // row filtered out (search, "defeated only") never reaches this
            // map at all: `groups` only ever lists a group that still has a
            // row (see the `groups` useMemo above), so there is no stranded
            // heading to guard against here.
            <table className="mob-table raid-table">
              <thead>
                <tr>
                  <th>Target</th>
                  <th>Zone</th>
                  <th className="num">Seen</th>
                  <th className="num">Credited</th>
                  <th>First kill</th>
                  <th>Last kill</th>
                  <th>Difficulty</th>
                </tr>
              </thead>
              <tbody>
                {groups.map((g) => (
                  <Fragment key={g.name}>
                    <tr className="raid-group-row">
                      <th className="raid-group-title" colSpan={7} scope="colgroup">
                        {g.name}
                      </th>
                    </tr>
                    {g.rows.map(({ target, hit }) => {
                      const agg = byTarget.get(target.name);
                      const deaths = agg?.deaths ?? 0;
                      const credited = agg?.credited ?? 0;
                      const defeated = deaths > 0;
                      return (
                        <tr key={target.name} className={"raid-row" + (defeated ? " raid-row-defeated" : "")}>
                          <td className="mob-name">
                            <button
                              className="raid-target-name"
                              onClick={() => onOpenMob?.({ name: target.name }, crumbHere())}
                              title={`Open ${target.name} in the Bestiary`}
                            >
                              <Highlight text={target.name} hit={hit} />
                            </button>
                            <LookupLink kind="npc" name={target.name} />
                          </td>
                          <td className="subtle">{target.zone}</td>
                          <td className="num strong">{deaths > 0 ? fmtNum(deaths) : "—"}</td>
                          <td className="num">{credited > 0 ? fmtNum(credited) : "—"}</td>
                          <td className="subtle">{fmtEpoch(agg?.firstAt ?? 0)}</td>
                          <td className="subtle">{fmtEpoch(agg?.lastAt ?? 0)}</td>
                          <td>
                            <div className="raid-ladder">
                              {rungs.map((r) => (
                                <span
                                  key={r}
                                  className={"raid-rung" + (agg?.lit.has(r) ? " on" : "")}
                                  title={r}
                                >
                                  {r}
                                </span>
                              ))}
                            </div>
                          </td>
                        </tr>
                      );
                    })}
                  </Fragment>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </div>
  );
}
