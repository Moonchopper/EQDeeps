import { useEffect, useMemo, useRef, useState } from "react";
import { IconExternalLink, IconMap2, IconMapPin } from "@tabler/icons-react";
import {
  api,
  type ItemRecord,
  type MobAttackEstimate,
  type MobAttackReport,
  type MobHealthEstimate,
  type MobHealthReport,
  type NpcBrowseRow,
  type NpcDetail,
  type NpcListing,
  type NpcPlace,
  type ReferenceStatus,
} from "../api";
import { fmtNum, fmtRate } from "../format";
import { LookupLink } from "../lookup/LookupLink";
import { mobKey, sameMob } from "../lookup/mobKey";
import { conOf, CON_WORD } from "../conColor";
import type { BestiaryTarget, Crumb, MapTarget } from "../trail";

/**
 * The Bestiary (F30, issue #51): every NPC EverQuest Legends has, searchable,
 * with what this server's own logs measured shown beside what the reference
 * site lists.
 *
 * <p><b>Why it is not built from the log.</b> A log-derived bestiary is a
 * record of what one player met — 580 names out of some five thousand, on the
 * owner's three weeks of play, with levels for three quarters of them and
 * nothing at all for the rest of the world. Reference data answers the other
 * 90%, and our own measurements answer what no site can: what a mob actually
 * took to kill, on this server, at this difficulty, and what it hit you for.
 * Showing both, side by side and labelled, is the honest version of the
 * feature — and where they disagree, that is worth seeing rather than hiding
 * (ADR-020).</p>
 *
 * <p><b>What it opens on.</b> The mobs this server's logs have killed, most
 * killed first: personal, instant, and fetched from nobody. The reference
 * index loads the moment the view opens — opening it <i>is</i> the ask that
 * ADR-020's fetch-on-demand rule waits for — and level bands beside the
 * search browse the ninety percent the logs never met. Nothing is fetched
 * while the Settings switch is off.</p>
 *
 * <p><b>The map is a hop away.</b> A listing stands in one zone, and the
 * name may stand in thirty; both are on the page, and either opens the Map
 * on that zone with the spawn points drawn, leaving this page behind as a
 * crumb (trail.ts).</p>
 */
export function BestiaryPanel({
  sessionId,
  mobs,
  attacks,
  enabled,
  target,
  onShowOnMap,
  onScreen,
}: {
  sessionId: string | null;
  /** What this server's logs measured (F25), for the comparison column. */
  mobs: MobHealthReport | null;
  /** What this character has been hit for (F26), for the other comparison. */
  attacks: MobAttackReport | null;
  /** The Settings switch: off means never speak to the reference site. */
  enabled: boolean;
  /** A mob another view asked to open here; re-fires on `seq`. */
  target: BestiaryTarget | null;
  /** To the Map, on a zone, with this page left behind as a crumb. */
  onShowOnMap: (target: Omit<MapTarget, "seq">, from: Crumb) => void;
  /** Which mob is open — the history's idea of this screen; null for the landing. */
  onScreen?: (mob: { name: string; id?: number } | null) => void;
}) {
  const [query, setQuery] = useState("");
  const [band, setBand] = useState<LevelBand | null>(null);
  const [status, setStatus] = useState<ReferenceStatus | null>(null);
  /** Whether the index has been asked for yet; the header reads differently before, during and after. */
  const [warming, setWarming] = useState(true);
  const [results, setResults] = useState<NpcBrowseRow[] | null>(null);
  const [total, setTotal] = useState(0);
  /** The name whose row is open, and which of its listings is being shown. */
  const [openName, setOpenName] = useState<NpcBrowseRow | null>(null);
  /**
   * Set when the open name is one the site does not list at all. The name is
   * still the subject — the log's own measurements are shown for it, since
   * this is the only place they are read now — with the site's half marked
   * absent rather than pretending to load. A click that did nothing was the
   * previous answer.
   */
  const [unlisted, setUnlisted] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [selected, setSelected] = useState<NpcListing | null>(null);
  const [detail, setDetail] = useState<NpcDetail | null>(null);
  const [detailUrl, setDetailUrl] = useState<string | null>(null);
  const [observed, setObserved] = useState<number[]>([]);
  const [loading, setLoading] = useState(false);
  const [items, setItems] = useState<Map<string, ItemRecord> | null>(null);
  /**
   * The level a con is read against. Starts as the last level the log
   * announced and is yours to change: on Legends a character carries several
   * (docs/domain/eq-legends-loadouts.md), and the log cannot say which is out.
   */
  const [conLevel, setConLevel] = useState<number | null>(null);
  const debounce = useRef<number | undefined>(undefined);
  const searchRef = useRef<HTMLInputElement | null>(null);
  /**
   * A mob asked for from outside is still on its way. While it is, what is
   * on screen — nothing yet, or the previous mob — is not a screen anyone
   * was on, and must not be reported as one: opening a mob from the Map
   * used to leave "the Bestiary landing" in the history between the Map
   * and the mob, so Back landed there. A ref, because the mount-time report
   * runs in the same commit as the ask arrives.
   */
  const opening = useRef(false);

  // Opening the view is the ask. Load the index now, so the header can say
  // how big the world is and a level band has something to browse — and so
  // the first search is not also the first fetch.
  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    setWarming(true);
    api
      .warmReference()
      .then((s) => !cancelled && setStatus(s))
      .catch(() => !cancelled && setStatus(null))
      .finally(() => !cancelled && setWarming(false));
    return () => {
      cancelled = true;
    };
  }, [enabled]);

  useEffect(() => {
    if (attacks?.characterLevel && conLevel === null) setConLevel(attacks.characterLevel);
  }, [attacks?.characterLevel, conLevel]);

  // The item registry, once, so the loot table can say which lines the logs
  // have actually seen drop.
  useEffect(() => {
    if (!sessionId) {
      setItems(null);
      return;
    }
    let cancelled = false;
    api
      .getItems(sessionId)
      .then((r) => {
        if (cancelled) return;
        const byName = new Map<string, ItemRecord>();
        for (const it of r.items) byName.set(it.name.trim().toLowerCase(), it);
        setItems(byName);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId]);

  // Typed, not on every keystroke: nine thousand names are matched server-side
  // and the answer is worth waiting a beat for. A band alone is a browse.
  useEffect(() => {
    if (!enabled) return;
    window.clearTimeout(debounce.current);
    const q = query.trim();
    if (q.length < 2 && !band) {
      setResults(null);
      setTotal(0);
      setSearchError(null);
      return;
    }

    debounce.current = window.setTimeout(() => {
      setLoading(true);
      api
        .searchNpcs(q.length >= 2 ? q : "", {
          limit: band && q.length < 2 ? 300 : 60,
          minLevel: band?.min,
          maxLevel: band?.max,
        })
        .then((r) => {
          setResults(r.npcs);
          setTotal(r.total);
          setSearchError(r.error ?? null);
        })
        .catch(() => setSearchError("the reference site could not be reached"))
        .finally(() => setLoading(false));
    }, 250);
    return () => window.clearTimeout(debounce.current);
  }, [query, band, enabled]);

  // The stat block, plus what our own /consider lines said about the name.
  useEffect(() => {
    if (!selected) {
      setDetail(null);
      setDetailUrl(null);
      setObserved([]);
      return;
    }
    if (selected.id !== UNLISTED) {
      setUnlisted(false);
    }
    let cancelled = false;
    if (selected.id === UNLISTED) {
      setDetail(null);
      setDetailUrl(null);
    } else {
      api
        .npcDetail(selected.id)
        .then((r) => {
          if (cancelled) return;
          setDetail(r?.detail ?? null);
          setDetailUrl(r?.url ?? null);
        })
        .catch(() => undefined);
    }
    if (sessionId) {
      api
        .lookupNpc(sessionId, selected.name)
        .then((r) => !cancelled && setObserved(r?.observedLevels ?? []))
        .catch(() => undefined);
    }
    return () => {
      cancelled = true;
    };
  }, [selected, sessionId]);

  /**
   * Open a name the way the log knows it: the row for the name, and among its
   * listings the one this session's /consider levels point at.
   */
  async function openByName(name: string, id?: number) {
    opening.current = true;
    setUnlisted(false);
    try {
      const r = await api.searchNpcs(name, { limit: 5 }).catch(() => null);
      // The index files "An imp protector" and "imp protector" as one name,
      // and puts the exact hit first; the article-blind compare here is for
      // the row's printed name, which may be either.
      const row = r?.npcs.find((n) => sameMob(n.name, name)) ?? r?.npcs[0] ?? null;
      if (row) setOpenName(row);
      if (id !== undefined && id !== UNLISTED) {
        const level =
          row?.levels.find((v) => v.id === id)?.level ?? row?.places.find((p) => p.id === id)?.levels[0];
        opening.current = false;
        setSelected({ id, name: row?.name ?? name, level, url: "" });
        return;
      }
      if (!row) {
        // Not listed — but the log has met it, and that half still shows.
        opening.current = false;
        setUnlisted(true);
        setSelected({ id: UNLISTED, name: name.trim(), url: "" });
        return;
      }
      let pick = row.levels[0] ?? null;
      if (sessionId) {
        const l = await api.lookupNpc(sessionId, name).catch(() => null);
        if (l) pick = row.levels.find((v) => v.level === l.listing.level) ?? l.listing;
      }
      opening.current = false;
      setSelected(pick);
    } finally {
      // A name the index no longer has leaves nothing to select; the screen
      // that is showing is then the screen, and reports resume.
      opening.current = false;
    }
  }

  // Another view asked for a mob (the Map's roster, a crumb back, or the
  // history). The seq is what makes asking twice work. An empty name is
  // the landing — history going back to before anything was picked.
  useEffect(() => {
    if (!target || !enabled) return;
    if (target.name === "") {
      setQuery("");
      setBand(null);
      setSelected(null);
      setOpenName(null);
      return;
    }
    // The search box shows the name too, so the list on the left is the
    // name's row and its neighbours rather than the landing — the page has
    // context, and clearing the box is the way back to the landing.
    setQuery(target.name);
    setBand(null);
    void openByName(target.name, target.id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [target?.seq, enabled]);

  // This screen, for the history: the mob that is open, or the landing —
  // but not while a mob asked for from outside is still on its way.
  useEffect(() => {
    if (opening.current) return;
    onScreen?.(
      selected ? { name: selected.name, id: selected.id === UNLISTED ? undefined : selected.id } : null,
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selected?.id, selected?.name]);

  /** What this server measured for a name, across every zone and tier it was killed in. */
  const measured = useMemo(() => {
    if (!selected || !mobs) return [];
    const key = mobKey(selected.name);
    return mobs.mobs.filter((m) => mobKey(m.mob) === key);
  }, [selected, mobs]);

  /**
   * What this name hit this character for, per place and defender level
   * (F26). Open world first, then rows that know whose level they are, then
   * by evidence: the site lists the open-world mob, and a comparison against
   * a tier that scales it is a different question.
   */
  const hits = useMemo(() => {
    if (!selected || !attacks) return [];
    const key = mobKey(selected.name);
    return attacks.mobs
      .filter((m) => mobKey(m.mob) === key)
      .sort(
        (a, b) =>
          Number(a.difficulty !== undefined) - Number(b.difficulty !== undefined) ||
          Number(a.defenderLevel === undefined) - Number(b.defenderLevel === undefined) ||
          b.swings - a.swings,
      );
  }, [selected, attacks]);

  /** The mobs this server has killed, one row per name, most killed first. */
  const met = useMemo(() => {
    if (!mobs) return [];
    const byName = new Map<string, { name: string; kills: number; zones: Set<string>; last: string }>();
    for (const m of mobs.mobs) {
      const key = m.mob.trim().toLowerCase();
      const row = byName.get(key) ?? { name: m.mob, kills: 0, zones: new Set<string>(), last: "" };
      row.kills += m.samples;
      row.zones.add(m.zone);
      if (m.lastKilled > row.last) row.last = m.lastKilled;
      byName.set(key, row);
    }
    return [...byName.values()].sort((a, b) => b.kills - a.kills || a.name.localeCompare(b.name));
  }, [mobs]);

  const source = status?.source ?? "the reference site";

  if (!enabled) {
    return (
      <div className="empty">
        Mob details are switched off. Settings → Reference sites → “Look mobs up online”
        turns them back on; nothing is fetched until it is.
      </div>
    );
  }

  const browsing = query.trim().length >= 2 || band !== null;

  // Which "Listed at" chip is lit. A listing opened from a place chip or the
  // map is one of the name's addresses rather than its per-level
  // representative, so it lights the chip for its level instead of none.
  const activeVariantId = !openName || !selected
    ? undefined
    : openName.levels.some((v) => v.id === selected.id)
      ? selected.id
      : openName.levels.find((v) => v.level === (detail?.level ?? selected.level))?.id;

  /** The crumb this page leaves behind when it opens the map. */
  const crumbHere = (): Crumb => ({
    view: "bestiary",
    label: selected?.name ?? "Bestiary",
    bestiary: selected ? { name: selected.name, id: selected.id } : undefined,
  });

  /** To the map, on a place, drawing this listing's spawn points if it has any there. */
  const showPlace = async (place: NpcPlace) => {
    if (!place.shortName || !selected) return;
    // The listing on this page may not be the one in that zone; the zone's
    // own listing knows where it stands, and its shard is one cached fetch.
    let points = detail?.zones.find((z) => z.shortName === place.shortName)?.locations ?? [];
    if (points.length === 0 && place.id !== selected.id) {
      const other = await api.npcDetail(place.id).catch(() => null);
      points = other?.detail.zones.find((z) => z.shortName === place.shortName)?.locations ?? [];
    }
    onShowOnMap(
      {
        place: place.name ?? place.shortName,
        shortName: place.shortName,
        spawn: points.length > 0 ? { mob: selected.name, points } : undefined,
      },
      crumbHere(),
    );
  };

  return (
    <div className="dashboard-main bestiary">
      <div className="panel bestiary-search">
        <div className="panel-title">
          <span className="panel-name">Bestiary</span>
          <span className="subtle">
            {status?.available
              ? `${fmtNum(status.names)} mobs · ${status.source}`
              : warming
                ? "loading the index…"
                : (status?.error ?? "the index could not be loaded")}
          </span>
        </div>
        <div className="bestiary-controls">
          <input
            ref={searchRef}
            className="bestiary-input"
            value={query}
            placeholder="Search every mob in Legends…"
            onChange={(e) => setQuery(e.target.value)}
            autoFocus
          />
          {/* Level bands browse the world the logs never met. A band on its
              own lists the whole band; with a query it narrows the search. */}
          <div className="bestiary-bands" role="group" aria-label="Browse by level">
            {LEVEL_BANDS.map((b) => (
              <button
                key={b.label}
                className={"bestiary-band" + (band?.label === b.label ? " on" : "")}
                onClick={() => setBand(band?.label === b.label ? null : b)}
                title={`Mobs listed at level ${b.label}`}
              >
                {b.label}
              </button>
            ))}
          </div>
        </div>
        <div className="table-scroll">
          {searchError && <div className="empty">{searchError}</div>}
          {browsing && results !== null && results.length === 0 && !loading && !searchError && (
            <div className="empty">Nothing by that name{band ? ` at level ${band.label}` : ""}.</div>
          )}
          {browsing && results !== null && results.length > 0 && total > results.length && (
            <div className="bestiary-more subtle">
              Showing {fmtNum(results.length)} of {fmtNum(total)} — type to narrow.
            </div>
          )}
          {browsing && (
            <ul className="bestiary-results">
              {(results ?? []).map((row) => (
                <li key={row.name}>
                  <button
                    className={"bestiary-row" + (openName?.name === row.name ? " on" : "")}
                    onClick={() => {
                      setOpenName(row);
                      setSelected(row.levels[0] ?? null);
                    }}
                    title={
                      row.listings > row.levels.length
                        ? `${row.listings} listings — the same mob in ${row.places.length} places`
                        : undefined
                    }
                  >
                    <span className="bestiary-name">{row.name}</span>
                    <span className="bestiary-level">{levelSpan(row)}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}

          {/* The landing: what this server's logs have actually killed. */}
          {!browsing && (
            <div className="bestiary-landing">
              {met.length > 0 ? (
                <>
                  <h4 className="bestiary-landing-title">
                    Mobs you’ve killed
                    <span className="subtle"> · {fmtNum(met.length)} names</span>
                  </h4>
                  <ul className="bestiary-results">
                    {met.slice(0, MET_SHOWN).map((m) => (
                      <li key={m.name}>
                        <button
                          className={"bestiary-row" + (selected && sameMob(selected.name, m.name) ? " on" : "")}
                          onClick={() => void openByName(m.name)}
                          title={`${m.kills} kill${m.kills === 1 ? "" : "s"} · ${[...m.zones].join(", ")}`}
                        >
                          <span className="bestiary-name">{m.name}</span>
                          <span className="bestiary-level">
                            {fmtNum(m.kills)} {m.kills === 1 ? "kill" : "kills"}
                          </span>
                        </button>
                      </li>
                    ))}
                  </ul>
                  {met.length > MET_SHOWN && (
                    <div className="bestiary-more subtle">
                      and {fmtNum(met.length - MET_SHOWN)} more — search for one, or pick a level.
                    </div>
                  )}
                </>
              ) : (
                <div className="empty bestiary-landing-empty">
                  {sessionId
                    ? "Nothing killed on this server yet. Search a name, or pick a level to browse."
                    : "Open a log and the mobs it has killed appear here. Or search a name, or pick a level."}
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      <div className="panel bestiary-detail">
        {!selected ? (
          <div className="bestiary-blank">
            <p className="bestiary-blank-lead">Pick a mob.</p>
            <p className="subtle">
              What {source} lists — level, health, what it hits for, where it stands, what it
              drops — beside what your own logs measured on this server: what it actually took to
              kill, and what it actually hit you for.
            </p>
          </div>
        ) : (
          <>
            <div className="panel-title bestiary-head">
              <span className="panel-name">
                <span className={conClass(conLevel, detail?.level ?? selected.level)}>{selected.name}</span>
                {unlisted ? (
                  <span className="subtle"> · not listed</span>
                ) : (
                  <LookupLink kind="npc" name={selected.name} />
                )}
              </span>
              <span className="bestiary-head-right">
                {/* Which level the colours are read against. Editable because
                    on Legends the log cannot know which of your levels is out. */}
                <label className="bestiary-con" title="The level the con colours are read against — yours to change">
                  <span className="subtle">cons</span>
                  <input
                    type="number"
                    min={1}
                    max={125}
                    value={conLevel ?? ""}
                    placeholder="lvl"
                    onChange={(e) => setConLevel(e.target.value === "" ? null : Number(e.target.value))}
                  />
                  {conLevel !== null && (detail?.level ?? selected.level) !== undefined && (
                    <b className={conClass(conLevel, detail?.level ?? selected.level)}>
                      {CON_WORD[conOf(conLevel, (detail?.level ?? selected.level)!)]}
                    </b>
                  )}
                </label>
                <a className="subtle" href={detailUrl || selected.url || status?.homeUrl} target="_blank" rel="noreferrer">
                  {status?.source ?? "reference"} <IconExternalLink size={11} stroke={2} aria-hidden />
                </a>
              </span>
            </div>
            <div className="table-scroll bestiary-body">
              {/* One name is listed at several levels; which one you are
                  reading matters, so the choice is on screen rather than
                  guessed at — coloured as each would con to you. */}
              {openName && openName.levels.length > 1 && (
                <div className="bestiary-variants">
                  <span className="subtle">Listed at</span>
                  {openName.levels.map((v) => (
                    <button
                      key={v.id}
                      className={
                        "bestiary-variant" +
                        (v.id === activeVariantId ? " on" : "") +
                        " " +
                        conClass(conLevel, v.level)
                      }
                      onClick={() => setSelected(v)}
                    >
                      L{v.level}
                    </button>
                  ))}
                </div>
              )}

              {/* The hero: theirs beside ours, for the two numbers that decide
                  a fight. Damage-to-kill is health plus whatever the killing
                  blow overshot by, so it reads a little high by nature — the
                  point is whether the two agree at all. */}
              <div className="bestiary-hero">
                <HealthCard listed={detail?.hp} measured={measured} />
                <HitsCard listed={detail} hits={hits} />
              </div>

              {unlisted ? (
                <p className="subtle bestiary-none">
                  {source} has no listing under this name. If it spells the mob differently, search
                  for it; what your own logs measured is below either way.
                </p>
              ) : (
              <div className="bestiary-stats">
                <Stat label="Level" value={levelRange(detail, selected)} />
                <Stat label="AC" value={detail?.ac ?? "—"} />
                <Stat label="Race" value={detail?.race ?? "—"} />
                <Stat label="Class" value={detail?.class ?? "—"} />
                <Stat label="Faction" value={detail?.faction ?? "—"} />
                <Stat label="Respawn" value={respawn(detail)} />
              </div>
              )}
              {detail?.specials && detail.specials.length > 0 && (
                <p className="bestiary-specials">{detail.specials.join(" · ")}</p>
              )}

              <section className="bestiary-section">
                <h4>What your logs measured</h4>
                {observed.length > 0 && (
                  <p className="subtle">You considered this at level {observed.join(", ")}.</p>
                )}
                {measured.length === 0 && hits.length === 0 && (
                  <p className="subtle bestiary-none">
                    {sessionId
                      ? "You haven’t fought one of these on this server — nothing measured yet."
                      : "Open a log to see what your own fights measured."}
                  </p>
                )}
                {/* Every row, since this is now the only place the server's
                    measured health is read (the Mobs view retired in
                    v0.15.1): each zone and tier the name was killed at, with
                    the band and the confidence grade the estimate carries
                    (F25) — a number pretending to be a measurement is what
                    the grade guards against. */}
                {measured.length > 0 && (
                  <table className="mob-table">
                    <thead>
                      <tr>
                        <th>Zone</th>
                        <th>Tier</th>
                        <th className="num">Damage to kill</th>
                        <th className="num">Range</th>
                        <th className="num">Kills</th>
                        <th>Confidence</th>
                        <th className="num">vs listed</th>
                      </tr>
                    </thead>
                    <tbody>
                      {measured.map((m) => (
                        <tr key={`${m.mob}|${m.zone}|${m.difficulty ?? "-"}`}>
                          <td className="subtle">{m.zone}</td>
                          <td className="subtle">{m.tierName ?? "open world"}</td>
                          <td className="num strong">{fmtNum(m.health)}</td>
                          <td className="num subtle">
                            {fmtNum(m.floor)}–{fmtNum(m.ceiling)}
                          </td>
                          <td className="num subtle">{m.samples}</td>
                          <td>
                            <span className={`mob-confidence ${m.confidence}`}>{m.confidence}</span>
                          </td>
                          <td className="num subtle">
                            {detail?.hp ? `×${(m.health / detail.hp).toFixed(2)}` : "—"}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
                {hits.length > 0 && (
                  <table className="mob-table bestiary-hits">
                    <thead>
                      <tr>
                        <th>Hit you in</th>
                        <th>Tier</th>
                        <th className="num">At level</th>
                        <th className="num">Swings</th>
                        <th className="num">Avg hit</th>
                        <th className="num">Max</th>
                        <th className="num">Landed</th>
                      </tr>
                    </thead>
                    <tbody>
                      {hits.slice(0, ROWS_SHOWN).map((h) => (
                        <tr key={`${h.mob}|${h.zone}|${h.difficulty ?? "-"}|${h.defenderLevel ?? "-"}`}>
                          <td className="subtle">{h.zone}</td>
                          <td className="subtle">{h.tierName ?? "open world"}</td>
                          <td className="num subtle">{h.defenderLevel ?? "—"}</td>
                          <td className="num subtle">{fmtNum(h.swings)}</td>
                          <td className="num strong">{h.meleeHits > 0 ? fmtNum(Math.round(h.avgHit)) : "—"}</td>
                          <td className="num">{h.maxHit > 0 ? fmtNum(h.maxHit) : "—"}</td>
                          <td className="num subtle">{h.swings > 0 ? fmtRate(h.hitRate) : "—"}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
                {hits.length > ROWS_SHOWN && (
                  <p className="subtle bestiary-more-rows">
                    and {hits.length - ROWS_SHOWN} more — every row, and each attack on its own, is on
                    the Incoming view.
                  </p>
                )}
              </section>

              {/* One listing stands in one place. The name may stand in
                  thirty, and the measured table above usually proves it — so
                  the two are said apart, and either opens the map. */}
              {(detail && detail.zones.length > 0) || (openName && openName.places.length > 0) ? (
                <section className="bestiary-section">
                  <h4>Where it stands</h4>
                  {detail && detail.zones.length > 0 && (
                    <ul className="bestiary-places">
                      {detail.zones.map((z) => (
                        <li key={z.shortName + z.longName} className="bestiary-place">
                          <button
                            className="bestiary-place-btn"
                            onClick={() =>
                              onShowOnMap(
                                {
                                  place: z.longName || z.shortName,
                                  shortName: z.shortName,
                                  spawn: z.locations.length > 0 ? { mob: selected.name, points: z.locations } : undefined,
                                },
                                crumbHere(),
                              )
                            }
                            title={`Show ${selected.name}'s spawn points on the map of ${z.longName || z.shortName}`}
                          >
                            <IconMapPin size={13} stroke={1.8} aria-hidden />
                            <span className="bestiary-place-name">{z.longName || z.shortName}</span>
                            <span className="subtle">
                              {z.spawnPoints} spawn point{z.spawnPoints === 1 ? "" : "s"}
                            </span>
                            <span className="bestiary-place-go subtle">map ›</span>
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                  {openName && openName.places.length > 1 && (
                    <>
                      <p className="subtle bestiary-elsewhere">
                        {openName.name} is listed {openName.listings} times in all — the same name
                        stands in {openName.places.length} places, each with its own entry.
                      </p>
                      <div className="bestiary-chips">
                        {openName.places.map((p) => {
                          const here = detail?.zones.some((z) => z.shortName === p.shortName);
                          return (
                            <span key={p.id} className={"bestiary-chip" + (here ? " on" : "")}>
                              <button
                                className="bestiary-chip-main"
                                onClick={() => setSelected({ id: p.id, name: openName.name, level: p.levels[0], url: "" })}
                                title={p.name ? `Read the ${p.name} listing` : "A zone this build has no map for"}
                              >
                                {p.name ?? "elsewhere"}
                                <span className="bestiary-chip-lvl">
                                  {p.levels.length > 0
                                    ? `L${p.levels[0]}${p.levels.length > 1 ? `–${p.levels[p.levels.length - 1]}` : ""}`
                                    : ""}
                                </span>
                                {p.era && <span className="bestiary-chip-era">{p.era}</span>}
                              </button>
                              {p.shortName && (
                                <button
                                  className="bestiary-chip-map"
                                  onClick={() => void showPlace(p)}
                                  title={`Show on the map of ${p.name}`}
                                >
                                  <IconMap2 size={12} stroke={1.8} aria-hidden />
                                </button>
                              )}
                            </span>
                          );
                        })}
                      </div>
                    </>
                  )}
                </section>
              ) : null}

              {detail && detail.loot.length > 0 && (
                <section className="bestiary-section">
                  <h4>
                    What it drops
                    <span className="subtle bestiary-h4-note"> · by chance</span>
                  </h4>
                  <table className="mob-table">
                    <thead>
                      <tr>
                        <th>Item</th>
                        <th className="num">Chance</th>
                      </tr>
                    </thead>
                    <tbody>
                      {[...detail.loot]
                        .sort((a, b) => b.dropPercent - a.dropPercent || a.item.localeCompare(b.item))
                        .map((line) => {
                          const seen = items?.get(line.item.trim().toLowerCase());
                          return (
                            <tr key={line.itemId}>
                              <td className="mob-name">
                                {line.item}
                                <LookupLink kind="item" name={line.item} id={line.itemId} />
                                {line.damage && <span className="subtle"> · {line.damage}</span>}
                                {/* The registry (F29) has seen this name in the
                                    logs — a claim about the logs, not the mob. */}
                                {seen && seen.looted > 0 && (
                                  <span className="bestiary-seen" title="Your logs have looted this">
                                    looted ×{seen.looted}
                                  </span>
                                )}
                              </td>
                              <td className="num subtle">{line.dropPercent.toFixed(1)}%</td>
                            </tr>
                          );
                        })}
                    </tbody>
                  </table>
                </section>
              )}

              <p className="bestiary-credit">
                Mob details from{" "}
                <a href={status?.homeUrl ?? detailUrl ?? "#"} target="_blank" rel="noreferrer">
                  {status?.source ?? "the reference site"}
                </a>
                , cached on this machine. Measurements are your own.
              </p>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

/** How many killed names the landing lists before it says "and N more". */
/**
 * The id a selection carries when the site lists nothing under the name: the
 * log's own half still shows, and nothing is fetched for it.
 */
const UNLISTED = -1;

const MET_SHOWN = 60;

/**
 * How many rows the hits table shows. A well-fought mob has a row per tier
 * and per defender level — a shin ghoul knight is seventeen — and this page
 * is the comparison, not the ledger; the Incoming view is. The health table
 * beside it is not capped: since the Mobs view retired it is the only place
 * that ledger is read.
 */
const ROWS_SHOWN = 8;

interface LevelBand {
  label: string;
  min: number;
  max?: number;
}

/** The bands the index is browsed by. Uneven on purpose: the world is. */
const LEVEL_BANDS: LevelBand[] = [
  { label: "1–9", min: 1, max: 9 },
  { label: "10–19", min: 10, max: 19 },
  { label: "20–29", min: 20, max: 29 },
  { label: "30–39", min: 30, max: 39 },
  { label: "40–49", min: 40, max: 49 },
  { label: "50–59", min: 50, max: 59 },
  { label: "60+", min: 60 },
];

/**
 * The listed health beside the measured damage-to-kill, and a verdict. The
 * measurement shown is the open-world row with the most kills behind it —
 * the site lists the open-world mob, and a tier that scales it is a
 * different question — falling back to the best-evidenced row of any tier;
 * the table below carries every row.
 */
function HealthCard({ listed, measured }: { listed?: number; measured: MobHealthEstimate[] }) {
  const best =
    measured.length > 0
      ? [...measured].sort(
          (a, b) =>
            Number(a.difficulty !== undefined) - Number(b.difficulty !== undefined) || b.samples - a.samples,
        )[0]
      : null;
  const ratio = best && listed ? best.health / listed : null;
  return (
    <div className="bestiary-card">
      <div className="bestiary-card-title">Health</div>
      <div className="bestiary-card-row">
        <div className="bestiary-card-cell">
          <span className="bestiary-card-value">{listed ? fmtNum(listed) : "—"}</span>
          <span className="bestiary-card-label">listed</span>
        </div>
        <div className="bestiary-card-cell">
          <span className={"bestiary-card-value" + (best ? " measured" : " none")}>
            {best ? fmtNum(best.health) : "—"}
          </span>
          <span className="bestiary-card-label">
            {best ? `to kill · ${best.samples} kill${best.samples === 1 ? "" : "s"}` : "you measured"}
          </span>
        </div>
        {ratio !== null && (
          <div className="bestiary-card-cell">
            <span className={"bestiary-card-value ratio " + ratioClass(ratio)}>×{ratio.toFixed(2)}</span>
            <span className="bestiary-card-label">vs listed</span>
          </div>
        )}
      </div>
      <div className="bestiary-card-note subtle">{healthVerdict(best, listed, ratio)}</div>
    </div>
  );
}

function healthVerdict(best: MobHealthEstimate | null, listed: number | undefined, ratio: number | null): string {
  if (!best) return "Kill one and what it took appears here.";
  if (!listed) return `Measured in ${best.zone}${best.tierName ? ` (${best.tierName})` : ""}; the site lists no health to compare.`;
  const where = `${best.zone}${best.tierName ? ` · ${best.tierName}` : ""}`;
  if (ratio! < 0.9) return `Below listed, in ${where} — a lower tier, or a listing for a different mob wearing the name.`;
  if (ratio! <= 1.35) return `About what the listing says, in ${where} — the excess is the killing blow's overkill.`;
  return `Well above listed, in ${where} — a harder tier, or a different mob wearing the same name.`;
}

function ratioClass(ratio: number): string {
  return ratio < 0.9 ? "low" : ratio <= 1.35 ? "ok" : "high";
}

/** The listed damage range beside what the mob actually hit this character for. */
function HitsCard({ listed, hits }: { listed: NpcDetail | null; hits: MobAttackEstimate[] }) {
  const best = hits[0] ?? null;
  return (
    <div className="bestiary-card">
      <div className="bestiary-card-title">Hits for</div>
      <div className="bestiary-card-row">
        <div className="bestiary-card-cell">
          <span className="bestiary-card-value">{damage(listed)}</span>
          <span className="bestiary-card-label">listed</span>
        </div>
        <div className="bestiary-card-cell">
          <span className={"bestiary-card-value" + (best ? " measured" : " none")}>
            {best && best.meleeHits > 0 ? `${fmtNum(Math.round(best.avgHit))} avg · ${fmtNum(best.maxHit)} max` : "—"}
          </span>
          <span className="bestiary-card-label">
            {best
              ? `hit you${best.defenderLevel ? ` at level ${best.defenderLevel}` : ""} · ${fmtNum(best.swings)} swings`
              : "hit you"}
          </span>
        </div>
        {best && best.swings > 0 && (
          <div className="bestiary-card-cell">
            <span className="bestiary-card-value">{fmtRate(best.hitRate)}</span>
            <span className="bestiary-card-label">landed</span>
          </div>
        )}
      </div>
      <div className="bestiary-card-note subtle">
        {best
          ? `In ${best.zone}${best.tierName ? ` · ${best.tierName}` : ""}, over ${best.fights} fight${best.fights === 1 ? "" : "s"}. Melee only; spells and shields are counted apart.`
          : "Stand in front of one and what it hits you for appears here."}
      </div>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="bestiary-stat">
      <span className="bestiary-stat-value">{value}</span>
      <span className="bestiary-stat-label">{label}</span>
    </div>
  );
}

/** The CSS class that paints a con colour, or nothing when either level is unknown. */
function conClass(playerLevel: number | null, mobLevel: number | undefined): string {
  if (playerLevel === null || mobLevel === undefined) return "";
  return "con-" + conOf(playerLevel, mobLevel);
}

/** "L42", or "L13–24" when the name covers a span. */
function levelSpan(row: NpcBrowseRow): string {
  if (row.minLevel === undefined) return "";
  return row.maxLevel !== undefined && row.maxLevel !== row.minLevel
    ? `L${row.minLevel}–${row.maxLevel}`
    : `L${row.minLevel}`;
}

function levelRange(detail: NpcDetail | null, listing: NpcListing): string {
  const low = detail?.level ?? listing.level;
  if (low === undefined) return "—";
  const high = detail?.maxLevel;
  return high !== undefined && high > low ? `${low}–${high}` : String(low);
}

function damage(detail: NpcDetail | null): string {
  if (!detail?.maxDamage) return "—";
  return `${detail.minDamage ?? 0}–${detail.maxDamage}`;
}

function respawn(detail: NpcDetail | null): string {
  const s = detail?.respawnSeconds;
  if (!s) return "—";
  return s >= 60 ? `${Math.round(s / 60)}m` : `${s}s`;
}
