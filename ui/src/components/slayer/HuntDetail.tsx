import { Fragment, useEffect, useRef, useState, type ReactNode } from "react";
import { IconMapPin, IconX } from "@tabler/icons-react";
import {
  api,
  type HuntFactionEffect,
  type HuntMob,
  type HuntZone,
  type SlayerHuntReport,
} from "../../api";
import type { BestiaryTarget, MapTarget } from "../../trail";
import { Bar, fmtCount } from "../SlayerPanel";

/** Which achievement, and optionally which of its components, the panel opened. */
export interface HuntSelection {
  key: string;
  /** A zero-based index into that achievement's `components[]`, in wire order; null for the whole achievement. */
  component: number | null;
}

/**
 * "Where to hunt" (F35 slice 2, ADR-023 Decisions 4-7): the master-detail
 * beside the Slayer table. Ranks the reference layer's zones against one
 * achievement's races and the player's own faction standings, and re-fetches
 * every 2s while the background atlas walk is still reading shards — so
 * zones land on screen as they are read rather than after all 79 finish.
 *
 * <p>Mounted with a `key` derived from the selection (see SlayerPanel), so a
 * new achievement or component starts every piece of state — the fetched
 * report, the level cap, which zones have "other factions" open — over from
 * nothing rather than carrying the previous selection's state across. A level
 * cap chosen for one achievement is not necessarily sane for the next one.</p>
 */
export function HuntDetail({
  sessionId,
  selection,
  referenceEnabled,
  onClose,
  onShowOnMap,
  onOpenMob,
}: {
  sessionId: string;
  selection: HuntSelection;
  /** The client-side "Look mobs up online" switch (ADR-020). False means: fetch nothing, poll nothing. */
  referenceEnabled: boolean;
  onClose: () => void;
  /** To the Map, on a zone door. */
  onShowOnMap: (target: Omit<MapTarget, "seq">) => void;
  /** To the Bestiary, on a mob chip. */
  onOpenMob: (target: Omit<BestiaryTarget, "seq">) => void;
}) {
  const [report, setReport] = useState<SlayerHuntReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [levelCapText, setLevelCapText] = useState("");
  const [levelCapDirty, setLevelCapDirty] = useState(false);
  const [committedCap, setCommittedCap] = useState<number | undefined>(undefined);
  const [openFactions, setOpenFactions] = useState<Set<string>>(new Set());
  const [copied, setCopied] = useState(false);
  const capDebounce = useRef<number | undefined>(undefined);
  const pollTimer = useRef<number | undefined>(undefined);
  const atlasStarted = useRef(false);
  const defaultApplied = useRef(false);

  // The typed cap settles for 300ms before it drives a fetch — the same
  // debounce the Bestiary's search box uses, and for the same reason: every
  // keystroke of a two- or three-digit level is not a request worth sending.
  useEffect(() => {
    window.clearTimeout(capDebounce.current);
    const t = levelCapText.trim();
    if (t === "") {
      capDebounce.current = window.setTimeout(() => setCommittedCap(undefined), 300);
      return () => window.clearTimeout(capDebounce.current);
    }
    const n = Number(t);
    if (!Number.isFinite(n) || n <= 0) return; // mid-typing garbage; wait for something sane
    capDebounce.current = window.setTimeout(() => setCommittedCap(Math.floor(n)), 300);
    return () => window.clearTimeout(capDebounce.current);
  }, [levelCapText]);

  // The fetch loop: ask once, and while the atlas is still running keep
  // asking every 2s — a hunt built before the walk finishes just has fewer
  // zones to rank (ADR-023 Decision 7), never zero for the wrong reason, so
  // re-fetching is what turns "fewer" back into "all of them" on screen.
  useEffect(() => {
    if (!referenceEnabled) {
      window.clearTimeout(pollTimer.current);
      return;
    }
    let cancelled = false;

    async function step() {
      try {
        // The POST *is* the ask (ADR-020 Decision 2, restated for the bulk
        // read as ADR-023 Decision 7): sent once, the moment this panel
        // opens, and never again for this selection — the GET below never
        // starts the walk itself, so without this nothing would ever read.
        if (!atlasStarted.current) {
          atlasStarted.current = true;
          await api.startAtlas().catch(() => undefined);
        }
        if (cancelled) return;
        const r = await api.slayerHunt(sessionId, selection.key, {
          component: selection.component ?? undefined,
          maxLevel: committedCap,
        });
        if (cancelled) return;
        setReport(r);
        setError(null);
        // The level cap defaults to the character's own level, but only once
        // per selection and only if the player has not already typed
        // something — an explicit "any level" (a cleared box) must stick.
        if (!defaultApplied.current && !levelCapDirty && levelCapText.trim() === "" && r.characterLevel != null) {
          defaultApplied.current = true;
          setLevelCapText(String(r.characterLevel));
          return; // the text change above re-runs this effect with the cap applied
        }
        if (r.atlas.running) {
          pollTimer.current = window.setTimeout(() => void step(), 2000);
        }
      } catch {
        if (!cancelled) setError("Could not read the reference layer.");
      }
    }
    void step();
    return () => {
      cancelled = true;
      window.clearTimeout(pollTimer.current);
    };
    // levelCapDirty/levelCapText are read for the one-time default, not
    // re-run on every keystroke — committedCap is the debounced value that
    // actually drives a new fetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, selection.key, selection.component, referenceEnabled, committedCap]);

  async function copyCommand(command: string) {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard access can be refused; the command is still on screen.
    }
  }

  function toggleFactions(shortName: string) {
    setOpenFactions((prev) => {
      const next = new Set(prev);
      if (next.has(shortName)) next.delete(shortName);
      else next.add(shortName);
      return next;
    });
  }

  function clearCap() {
    setLevelCapDirty(true);
    setLevelCapText("");
  }

  const header = (body: ReactNode) => (
    <div className="panel hunt-detail">
      <div className="panel-title">
        <span className="panel-name">Where to hunt</span>
        <button className="mini-btn" onClick={onClose} title="Close">
          <IconX size={12} stroke={1.8} /> close
        </button>
      </div>
      {body}
    </div>
  );

  if (!referenceEnabled) {
    return header(
      <div className="empty">
        Mob lookups are switched off. Settings → Reference sites → "Look mobs up online" turns
        them back on; nothing is fetched until it is.
      </div>,
    );
  }

  if (error) {
    return header(<div className="empty">{error}</div>);
  }

  if (!report) {
    return header(<div className="empty">Reading the reference layer…</div>);
  }

  if (!report.atlas.enabled) {
    return header(
      <div className="empty">
        This server was started with mob lookups disabled (--no-reference); zone data cannot be
        read.
      </div>,
    );
  }

  const knownTerms = report.terms.filter((t) => t.races.length > 0);
  const unknownTerms = report.terms.filter((t) => t.races.length === 0);
  const allTermsUnknown = report.terms.length > 0 && knownTerms.length === 0;
  const firstCostly = report.zones.findIndex((z) => !z.recommended);
  const atlasDone = !report.atlas.running;

  return header(
    <>
      <div className="hunt-detail-body">
        <p className="hunt-sub subtle">
          {report.creatures} · {fmtCount(report.remaining)} to go
        </p>
        {knownTerms.length > 0 && (
          <p className="hunt-terms subtle">
            counts as: {knownTerms.map((t) => `${t.term} → ${t.races.join("/")}`).join("; ")}
          </p>
        )}
        {unknownTerms.length > 0 && (
          <p className="hunt-terms subtle">
            no known location: {unknownTerms.map((t) => t.term).join(", ")}
          </p>
        )}

        <label className="hunt-cap">
          mobs up to level
          <input
            type="number"
            min={1}
            className="hunt-cap-input"
            value={levelCapText}
            placeholder="any"
            onChange={(e) => {
              setLevelCapDirty(true);
              setLevelCapText(e.target.value);
            }}
          />
          {levelCapText !== "" && (
            <button className="mini-btn" onClick={clearCap} title="No level cap">
              any level
            </button>
          )}
        </label>

        {!report.factions.found && (
          <p className="slayer-cmd hunt-faction-note">
            Run <code>{report.factions.command}</code> to see where this would leave you.{" "}
            <button className="mini-btn" onClick={() => void copyCommand(report.factions.command)}>
              {copied ? "copied" : "copy"}
            </button>
          </p>
        )}

        {report.atlas.running && (
          <div className="hunt-atlas">
            <span className="subtle">
              Reading zone data from {report.atlas.source} — {fmtCount(report.atlas.zonesRead)} of{" "}
              {fmtCount(report.atlas.zonesTotal)}
            </span>
            <Bar
              pct={
                report.atlas.zonesTotal > 0
                  ? (report.atlas.zonesRead / report.atlas.zonesTotal) * 100
                  : 0
              }
            />
          </div>
        )}
        {report.atlas.error && <p className="hunt-atlas-error">{report.atlas.error}</p>}
      </div>

      <div className="table-scroll hunt-zone-scroll">
        <ul className="hunt-zone-list">
          {report.zones.map((z, i) => (
            <Fragment key={z.shortName}>
              {i === firstCostly && (
                <li className="hunt-divider">Costs a faction you are building</li>
              )}
              <ZoneRow
                zone={z}
                onShowOnMap={onShowOnMap}
                onOpenMob={onOpenMob}
                open={openFactions.has(z.shortName)}
                onToggleFactions={() => toggleFactions(z.shortName)}
              />
            </Fragment>
          ))}
          {report.zones.length === 0 && atlasDone && (
            <li className="empty">
              {allTermsUnknown
                ? "None of this achievement's creature words match anything this app can look up yet."
                : "Nothing the reference lists counts toward this — yet."}
            </li>
          )}
        </ul>

        {report.citiesLeftOut > 0 && (
          <p className="subtle hunt-cities-note">
            {fmtCount(report.citiesLeftOut)} cities left out — never recommended
          </p>
        )}

        <p className="bestiary-credit">
          Zone data from{" "}
          <a href={report.atlas.homeUrl} target="_blank" rel="noreferrer">
            {report.atlas.source}
          </a>
          , cached on this machine.
        </p>
      </div>
    </>,
  );
}

function ZoneRow({
  zone,
  onShowOnMap,
  onOpenMob,
  open,
  onToggleFactions,
}: {
  zone: HuntZone;
  onShowOnMap: (target: Omit<MapTarget, "seq">) => void;
  onOpenMob: (target: Omit<BestiaryTarget, "seq">) => void;
  open: boolean;
  onToggleFactions: () => void;
}) {
  const protectedFx = zone.factions.filter((f) => f.protected);
  // Server-ordered already (SlayerHunting.cs FactionSortRank): protected
  // losses worst-first, then protected gains best-first, then everything
  // else by magnitude — filtering here preserves that order in both groups.
  const otherFx = zone.factions.filter((f) => !f.protected);

  return (
    <li className={"hunt-zone" + (zone.recommended ? "" : " costly")}>
      <div className="hunt-zone-row">
        <button
          className="bestiary-place-btn hunt-zone-btn"
          onClick={() => onShowOnMap({ place: zone.name, shortName: zone.shortName })}
          title={`Show ${zone.name} on the map`}
        >
          <IconMapPin size={13} stroke={1.8} aria-hidden />
          <span className="bestiary-place-name">{zone.name}</span>
          <span className="subtle hunt-zone-go">map ›</span>
        </button>
        <span className="hunt-zone-stats subtle">
          {zoneLevelText(zone)} · {fmtCount(zone.spawnPoints)} spawn point
          {zone.spawnPoints === 1 ? "" : "s"} ·{" "}
          {/* "up to" is load-bearing, not decoration: this is a supply ceiling computed from
              spawn points and respawn timers, never a promise of a real kill rate — a full
              group camping the same points, a slow pull, a bad night, all land under it
              (ADR-023 Decision 4). */}
          up to ~{fmtCount(zone.perHour)} an hour
        </span>
      </div>

      {zone.mobs.length > 0 && (
        <div className="bestiary-chips hunt-zone-mobs">
          {zone.mobs.map((m) => (
            <span key={m.id} className="bestiary-chip">
              <button
                className="bestiary-chip-main"
                onClick={() => onOpenMob({ name: m.name, id: m.id })}
                title={`Open ${m.name} in the Bestiary`}
              >
                {m.name}
                {mobLevelText(m) && <span className="bestiary-chip-lvl">{mobLevelText(m)}</span>}
              </button>
            </span>
          ))}
        </div>
      )}

      {zone.factions.length > 0 && (
        <div className="hunt-fx-list">
          {protectedFx.map((f) => (
            <FxRow key={f.faction} f={f} />
          ))}
          {otherFx.length > 0 && (
            <>
              <button className="mini-btn hunt-fx-toggle" onClick={onToggleFactions}>
                {open ? "hide" : "show"} other factions ({otherFx.length})
              </button>
              {open && otherFx.map((f) => <FxRow key={f.faction} f={f} muted />)}
            </>
          )}
        </div>
      )}
    </li>
  );
}

function FxRow({ f, muted }: { f: HuntFactionEffect; muted?: boolean }) {
  const sign = f.perKill < 0 ? "loss" : f.perKill > 0 ? "gain" : "";
  return (
    <div className={"hunt-fx" + (muted ? " muted" : sign ? ` ${sign}` : "")}>
      <span className="hunt-fx-name">{f.faction}</span>
      <span className="hunt-fx-value">{fmtSigned(f.perKill)} a kill</span>
      {f.standing != null && (
        <span className="subtle hunt-fx-standing">
          {fmtCount(f.standing)} → {fmtCount(f.projected ?? f.standing)}
        </span>
      )}
      {/* Shown, never used to excuse a loss — a completed "maximum faction" unlock does not mean
          the standing itself is safe (ADR-023 Decision 5; SlayerHunting.cs carries the same
          comment server-side). It is a fact about the grind, not a verdict on the zone. */}
      {f.unlockEarned && <span className="subtle hunt-fx-unlock">unlock earned</span>}
    </div>
  );
}

function zoneLevelText(z: HuntZone): string {
  if (z.minLevel != null && z.maxLevel != null) {
    return z.minLevel === z.maxLevel ? `L${z.minLevel}` : `L${z.minLevel}–${z.maxLevel}`;
  }
  if (z.minLevel != null) return `L${z.minLevel}+`;
  if (z.maxLevel != null) return `up to L${z.maxLevel}`;
  return "level unknown";
}

function mobLevelText(m: HuntMob): string {
  if (m.level == null) return "";
  if (m.maxLevel != null && m.maxLevel !== m.level) return `L${m.level}–${m.maxLevel}`;
  return `L${m.level}`;
}

function fmtSigned(n: number): string {
  const v = n.toFixed(2);
  return n >= 0 ? `+${v}` : v; // toFixed already carries the minus for negatives
}
