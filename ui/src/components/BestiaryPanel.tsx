import { useEffect, useMemo, useRef, useState } from "react";
import { IconExternalLink } from "@tabler/icons-react";
import {
  api,
  type MobHealthReport,
  type NpcDetail,
  type NpcListing,
  type ReferenceStatus,
} from "../api";
import { fmtNum } from "../format";
import { LookupLink } from "../lookup/LookupLink";

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
 * took to kill, on this server, at this difficulty. Showing both, side by
 * side and labelled, is the honest version of the feature — and where they
 * disagree, that is worth seeing rather than hiding (ADR-020).</p>
 *
 * <p>Nothing here is fetched until this view is opened and something is
 * typed, which is what makes the Settings switch meaningful.</p>
 */
export function BestiaryPanel({
  sessionId,
  mobs,
  enabled,
}: {
  sessionId: string | null;
  /** What this server's logs measured (F25), for the comparison column. */
  mobs: MobHealthReport | null;
  /** The Settings switch: off means never speak to the reference site. */
  enabled: boolean;
}) {
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<ReferenceStatus | null>(null);
  const [results, setResults] = useState<NpcListing[] | null>(null);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [selected, setSelected] = useState<NpcListing | null>(null);
  const [detail, setDetail] = useState<NpcDetail | null>(null);
  const [observed, setObserved] = useState<number[]>([]);
  const [loading, setLoading] = useState(false);
  const debounce = useRef<number | undefined>(undefined);

  useEffect(() => {
    if (!enabled) return;
    api.referenceStatus().then(setStatus).catch(() => undefined);
  }, [enabled]);

  // Typed, not on every keystroke: nine thousand names are matched server-side
  // and the answer is worth waiting a beat for.
  useEffect(() => {
    if (!enabled) return;
    window.clearTimeout(debounce.current);
    const q = query.trim();
    if (q.length < 2) {
      setResults(null);
      setSearchError(null);
      return;
    }

    debounce.current = window.setTimeout(() => {
      setLoading(true);
      api
        .searchNpcs(q)
        .then((r) => {
          setResults(r.npcs);
          setSearchError(r.error ?? null);
          // The first search is what loads the index, so the header's count
          // is only true once one has run.
          if (!status?.available) api.referenceStatus().then(setStatus).catch(() => undefined);
        })
        .catch(() => setSearchError("the reference site could not be reached"))
        .finally(() => setLoading(false));
    }, 250);
    return () => window.clearTimeout(debounce.current);
  }, [query, enabled, status?.available]);

  // The stat block, plus what our own /consider lines said about the name.
  useEffect(() => {
    if (!selected) {
      setDetail(null);
      setObserved([]);
      return;
    }
    let cancelled = false;
    api
      .npcDetail(selected.id)
      .then((r) => !cancelled && setDetail(r?.detail ?? null))
      .catch(() => undefined);
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

  /** What this server measured for a name, across every zone and tier it was killed in. */
  const measured = useMemo(() => {
    if (!selected || !mobs) return [];
    const key = selected.name.trim().toLowerCase();
    return mobs.mobs.filter((m) => m.mob.trim().toLowerCase() === key);
  }, [selected, mobs]);

  if (!enabled) {
    return (
      <div className="empty">
        Mob details are switched off. Settings → Reference sites → “Look mobs up online”
        turns them back on; nothing is fetched until it is.
      </div>
    );
  }

  return (
    <div className="dashboard-main bestiary">
      <div className="panel bestiary-search">
        <div className="panel-title">
          <span className="panel-name">Bestiary</span>
          <span className="subtle">
            {status?.available
              ? `${fmtNum(status.names)} mobs · ${status.source}`
              : (status?.error ?? "loading…")}
          </span>
        </div>
        <div className="table-scroll">
          <input
            className="bestiary-input"
            value={query}
            placeholder="Search every mob in Legends…"
            onChange={(e) => setQuery(e.target.value)}
            autoFocus
          />
          {searchError && <div className="empty">{searchError}</div>}
          {results !== null && results.length === 0 && !loading && !searchError && (
            <div className="empty">Nothing by that name.</div>
          )}
          {results === null && !searchError && (
            <div className="empty">Type a couple of letters — “ghoul”, “Nagafen”.</div>
          )}
          <ul className="bestiary-results">
            {(results ?? []).map((npc) => (
              <li key={npc.id}>
                <button
                  className={"bestiary-row" + (selected?.id === npc.id ? " on" : "")}
                  onClick={() => setSelected(npc)}
                >
                  <span className="bestiary-name">{npc.name}</span>
                  {npc.level !== undefined && <span className="bestiary-level">L{npc.level}</span>}
                </button>
              </li>
            ))}
          </ul>
        </div>
      </div>

      <div className="panel bestiary-detail">
        {!selected ? (
          <div className="empty">Pick a mob to see what it is, and what you measured.</div>
        ) : (
          <>
            <div className="panel-title">
              <span className="panel-name">
                {selected.name}
                <LookupLink kind="npc" name={selected.name} />
              </span>
              <a className="subtle" href={selected.url} target="_blank" rel="noreferrer">
                {status?.source ?? "reference"} <IconExternalLink size={11} stroke={2} aria-hidden />
              </a>
            </div>
            <div className="table-scroll">
              <div className="bestiary-stats">
                <Stat label="Level" value={levelRange(detail, selected)} />
                <Stat label="Health" value={detail?.hp ? fmtNum(detail.hp) : "—"} />
                <Stat label="AC" value={detail?.ac ?? "—"} />
                <Stat label="Hits for" value={damage(detail)} />
                <Stat label="Race" value={detail?.race ?? "—"} />
                <Stat label="Class" value={detail?.class ?? "—"} />
                <Stat label="Faction" value={detail?.faction ?? "—"} />
                <Stat label="Respawn" value={respawn(detail)} />
              </div>
              {detail?.specials && detail.specials.length > 0 && (
                <p className="bestiary-specials">{detail.specials.join(" · ")}</p>
              )}

              {/* Ours, next to theirs. Damage-to-kill is health plus whatever
                  the killing blow overshot by, so it reads a little high by
                  nature — the point is whether the two agree at all. */}
              {(measured.length > 0 || observed.length > 0) && (
                <section className="bestiary-section">
                  <h4>What your logs measured</h4>
                  {observed.length > 0 && (
                    <p className="subtle">
                      You considered this at level {observed.join(", ")}.
                    </p>
                  )}
                  <table className="mob-table">
                    <thead>
                      <tr>
                        <th>Zone</th>
                        <th>Tier</th>
                        <th className="num">Damage to kill</th>
                        <th className="num">Kills</th>
                        <th className="num">vs listed</th>
                      </tr>
                    </thead>
                    <tbody>
                      {measured.map((m) => (
                        <tr key={`${m.mob}|${m.zone}|${m.difficulty ?? "-"}`}>
                          <td className="subtle">{m.zone}</td>
                          <td className="subtle">{m.tierName ?? "open world"}</td>
                          <td className="num strong">{fmtNum(m.health)}</td>
                          <td className="num subtle">{m.samples}</td>
                          <td className="num subtle">
                            {detail?.hp ? `×${(m.health / detail.hp).toFixed(2)}` : "—"}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </section>
              )}

              {detail && detail.zones.length > 0 && (
                <section className="bestiary-section">
                  <h4>Where it is</h4>
                  <ul className="bestiary-list">
                    {detail.zones.map((z) => (
                      <li key={z.shortName + z.longName}>
                        {z.longName || z.shortName}
                        <span className="subtle">
                          {" "}
                          · {z.spawnPoints} spawn point{z.spawnPoints === 1 ? "" : "s"}
                        </span>
                      </li>
                    ))}
                  </ul>
                </section>
              )}

              {detail && detail.loot.length > 0 && (
                <section className="bestiary-section">
                  <h4>What it drops</h4>
                  <table className="mob-table">
                    <thead>
                      <tr>
                        <th>Item</th>
                        <th className="num">Chance</th>
                      </tr>
                    </thead>
                    <tbody>
                      {detail.loot.map((line) => (
                        <tr key={line.itemId}>
                          <td className="mob-name">
                            {line.item}
                            <LookupLink kind="item" name={line.item} id={line.itemId} />
                            {line.damage && <span className="subtle"> · {line.damage}</span>}
                          </td>
                          <td className="num subtle">{line.dropPercent.toFixed(1)}%</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </section>
              )}

              <p className="bestiary-credit">
                Mob details from{" "}
                <a href={status?.homeUrl ?? selected.url} target="_blank" rel="noreferrer">
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

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="bestiary-stat">
      <span className="bestiary-stat-value">{value}</span>
      <span className="bestiary-stat-label">{label}</span>
    </div>
  );
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
