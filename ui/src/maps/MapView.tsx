import { useEffect, useMemo, useRef, useState } from "react";
import { IconMapPin, IconX } from "@tabler/icons-react";
import {
  api,
  type MapCatalog,
  type MapCatalogEntry,
  type MobHealthReport,
  type NpcBrowseRow,
  type ZoneMap,
  type ZoneRoster,
} from "../api";
import { fmtNum } from "../format";
import { fuzzyMatch } from "../fuzzy";
import type { BestiaryTarget, Crumb, MapTarget, SpawnOverlay } from "../trail";
import { MapCanvas, type MapMarker } from "./MapCanvas";
import {
  chosenFor,
  eraFor,
  loadMapSettings,
  rememberEra,
  rememberMap,
  stripInstance,
  zoneKey,
  type MapSettings,
} from "./mapSettings";
import { ZoneGraphView } from "./ZoneGraphView";

/** How a name was arrived at, said plainly. Only the first two are verifiable. */
const SOURCE_NOTE: Record<string, string> = {
  name: "Name matches the client's own zone table.",
  graph: "Deduced from neighbouring maps, and confirmed by them naming it back.",
  curated: "Written down by hand — the name is checked, the pairing is not.",
};

/** How the era was arrived at. It inherits whatever doubt the name pairing has. */
const ERA_NOTE: Record<string, string> = {
  id: "Earliest expansion this place exists in, from the band its client zone id falls in.",
  curated: "Earliest expansion this place exists in, set by hand where the zone-id band is known to be wrong.",
};

interface Props {
  /** The zone the log says the character is in, if a log is open. */
  currentZone?: string;
  /**
   * The installation the open log is from — "EverQuest Legends", "EverQuest"
   * — if one is. Which drawing is right and how far the world is unlocked are
   * facts about the install, so every choice made here is remembered against
   * it; the shard in the file name is a finer cut than the world it plays in.
   */
  install?: string;
  /**
   * Whether a log is open at all — the signal for "a zone is coming, wait for
   * it" as opposed to "nobody is playing, draw anything".
   */
  hasLog?: boolean;
  /** What this server's logs have killed (F25), for "mobs you have killed here". */
  mobs?: MobHealthReport | null;
  /** The Settings switch for the reference site; off means no roster and no mob search. */
  referenceEnabled?: boolean;
  /** A zone another view asked to open here, with a mob's spawn points to draw; re-fires on `seq`. */
  target?: MapTarget | null;
  /** To the Bestiary, on a mob, with this zone left behind as a crumb. */
  onOpenMob?: (target: Omit<BestiaryTarget, "seq">, from: Crumb) => void;
}

/** Turns the site's spawn coordinates into the file's — see docs/domain/eq-map-format.md §3. */
function toMarkers(spawn: SpawnOverlay, lit = false): MapMarker[] {
  return spawn.points.map(([x, y]) => ({ x: -x, y: -y, label: spawn.mob, lit }));
}

export function MapView({
  currentZone,
  install,
  hasLog = false,
  mobs = null,
  referenceEnabled = true,
  target = null,
  onOpenMob,
}: Props) {
  const [catalog, setCatalog] = useState<MapCatalog | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [mode, setMode] = useState<"zone" | "world">("zone");

  const [selected, setSelected] = useState<string | null>(null);
  const [set, setSet] = useState<string | undefined>(undefined);
  const [map, setMap] = useState<ZoneMap | null>(null);
  const [loading, setLoading] = useState(false);

  const [filter, setFilter] = useState("");
  const [hiddenLayers, setHiddenLayers] = useState<number[]>([]);
  const [trueColors, setTrueColors] = useState(false);
  const [highlight, setHighlight] = useState<string | null>(null);

  const [settings, setSettings] = useState<MapSettings>({});
  const [settingsLoaded, setSettingsLoaded] = useState(false);
  const [rootDraft, setRootDraft] = useState("");
  const [rootError, setRootError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  /** True once the log's zone has no map and the user has not said otherwise. */
  const [unresolved, setUnresolved] = useState(false);

  /**
   * The user has said the map the log's zone opened on is the wrong one and
   * is off to pick another. Turns the "use for <zone>" button on while they
   * browse, and off again once they press it or give up.
   */
  const [correcting, setCorrecting] = useState(false);

  /**
   * The zone the World view should land on when it opens from here, and a
   * counter so asking again for the same zone frames it again.
   */
  const [worldFocus, setWorldFocus] = useState<{ zone: string; seq: number } | null>(null);

  /** To the world, landing on the zone on screen. Right-click and the header button both come here. */
  const showInWorld = () => {
    setWorldFocus((f) => (selected ? { zone: selected, seq: (f?.seq ?? 0) + 1 } : f));
    setMode("world");
  };

  // ---- mobs (F30 × F27) --------------------------------------------------
  // The rail's second tab: who stands in the zone on screen, and a search
  // over every mob for where it stands. Nothing here is fetched until the
  // tab is opened or a mob is asked for.
  const [railTab, setRailTab] = useState<"zones" | "mobs">("zones");
  const [mobQuery, setMobQuery] = useState("");
  const [mobResults, setMobResults] = useState<NpcBrowseRow[] | null>(null);
  const [roster, setRoster] = useState<ZoneRoster | null>(null);
  const [rosterFor, setRosterFor] = useState<string | null>(null);
  const [rosterLoading, setRosterLoading] = useState(false);
  /** A mob's spawn points drawn over the zone — from the Bestiary, or pinned from the roster. */
  const [spawn, setSpawn] = useState<SpawnOverlay | null>(null);
  /** The roster row under the pointer, drawn lit while it is. */
  const [hoverSpawn, setHoverSpawn] = useState<SpawnOverlay | null>(null);
  /** The mob-search row under the pointer: on the World view its zones light up. */
  const [litMob, setLitMob] = useState<NpcBrowseRow | null>(null);
  const mobDebounce = useRef<number | undefined>(undefined);

  /** Every map short name the pointed-at mob stands in, for the world graph to light. */
  const litZones = useMemo(() => {
    if (!litMob) return null;
    const set = new Set<string>();
    for (const p of litMob.places) {
      for (const m of p.maps) set.add(m);
      if (p.shortName) set.add(p.shortName);
    }
    return set;
  }, [litMob]);

  // The zone the log is in wins once, on arrival. After that the user is
  // steering: auto-following every zone line would yank the map out from under
  // someone reading it.
  //
  // State rather than a ref because the fallback below has to wait for it. A
  // ref settles silently and the fallback, already rendered, never reconsiders
  // — which is how opening the Map tab could land on an unrelated zone and
  // stay there.
  const followed = useRef(false);
  const [followDone, setFollowDone] = useState(false);

  /**
   * Another view has asked for a zone. A ref rather than state because the
   * follow below starts in the same commit as the ask arrives and resolves
   * asynchronously — by the time it would move the map, this has to already
   * be true, and a state update would not be visible to it yet.
   */
  const asked = useRef(false);

  /**
   * The user has chosen a zone themselves, so the log must not move the map
   * again.
   *
   * <p>The zone timeline is built after the backfill and can take half a minute
   * on a large log. Without this the follow arrives late and yanks the map off
   * whatever the user opened in the meantime — which reads as the app fighting
   * them, and is worst on exactly the logs where waiting is longest.</p>
   */
  const [steered, setSteered] = useState(false);

  /**
   * Backstop for the wait below. A live log that has not named a zone yet may
   * never do so, and refusing to draw anything forever is its own failure.
   */
  const [graceOver, setGraceOver] = useState(false);

  useEffect(() => {
    if (!hasLog) {
      return;
    }

    const timer = window.setTimeout(() => setGraceOver(true), 12_000);
    return () => window.clearTimeout(timer);
  }, [hasLog]);

  useEffect(() => {
    api
      .mapCatalog()
      .then((c) => {
        setCatalog(c);
        setRootDraft(c.userRoot ?? "");
      })
      .catch((e: Error) => setError(e.message));

    loadMapSettings()
      .then(setSettings)
      .catch(() => undefined)
      .finally(() => setSettingsLoaded(true));
  }, []);

  // Follow the log, once. The user's own choice for this zone wins over the
  // table, which is the whole point of recording one — the table is knowingly
  // incomplete and knowingly fallible, and this is the correction.
  useEffect(() => {
    if (!catalog?.found || !settingsLoaded || followed.current || !currentZone || steered || asked.current) {
      return;
    }

    followed.current = true;
    const known = (s: string) => catalog.zones.some((z) => z.shortName === s);
    const override = chosenFor(settings, currentZone, install);

    if (override && known(override)) {
      setSelected(override);
      setFollowDone(true);
      return;
    }

    api
      .resolveZone(currentZone)
      .then((r) => {
        // Another view may have asked for a zone while this was in flight;
        // the ask wins, or the map would land on the log's zone with another
        // mob's spawn points drawn over it.
        if (asked.current) {
          return;
        }

        const hit = r.shortNames.find(known);
        if (hit) {
          setSelected(hit);
        } else {
          // Not an error: the table does not claim to know every zone. Say so
          // and let them point at the right map (ADR-016).
          setUnresolved(true);
        }
      })
      .catch(() => undefined)
      .finally(() => setFollowDone(true));
  }, [catalog, currentZone, settings, settingsLoaded, install]);

  // Fall back to the first zone so the view is never an empty frame — but not
  // while a log is still telling us where the character is. Landing on an
  // arbitrary zone and staying there is worse than a moment of nothing, and
  // that is what happened: the catalogue arrives before the zone timeline, so
  // the fallback fired first and the follow had nothing left to correct.
  useEffect(() => {
    if (selected || !catalog?.zones.length) {
      return;
    }

    // Wait while a log is still working out where the character is. Landing on
    // an arbitrary zone and jumping off it seconds later is worse than a moment
    // of nothing, and the gap is real: the catalogue arrives long before the
    // zone timeline, which is built after the backfill.
    //
    // Note the wait is on `currentZone`, not on `contextLoaded`. The context
    // arrives non-null with an empty zone list and fills in afterwards, so
    // "loaded" fires too early to be the signal — which is exactly how this
    // was landing on an unrelated zone despite being gated.
    if (hasLog && !currentZone && !followDone && !graceOver) {
      return;
    }

    setSelected(catalog.zones[0].shortName);
  }, [catalog, selected, currentZone, followDone, hasLog, graceOver]);

  useEffect(() => {
    if (!selected) {
      return;
    }

    let cancelled = false;
    setLoading(true);
    setHiddenLayers([]);

    api
      .zoneMap(selected, set)
      .then((m) => {
        if (!cancelled) {
          setMap(m);
          setError(null);
        }
      })
      .catch((e: Error) => {
        if (!cancelled) {
          setMap(null);
          setError(e.message);
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [selected, set]);

  /**
   * One row per *place*, not per file. Several maps can carry the same display
   * name and the list showed them as identical rows — two "Toxxulia Forest"
   * entries with nothing to choose between them.
   *
   * <p>Merging them rather than labelling them apart is deliberate, because the
   * two cases behind a shared name are indistinguishable from here. `tox` and
   * `toxxulia` are one zone drawn twice, at 699 and 7738 segments; `freportw`
   * and `freeportwest` are genuinely different zones that share a name across a
   * revamp. Either way the player wants the place first and the drawing second,
   * so the name picks the place and a control picks the map.</p>
   *
   * <p>Unnamed maps cannot be grouped: there is no name to group them under,
   * and two files we cannot name are not evidence of being the same place.</p>
   */
  const places = useMemo(() => {
    if (!catalog) {
      return [];
    }

    const byKey = new Map<string, { key: string; name: string; maps: MapCatalogEntry[] }>();

    for (const zone of catalog.zones) {
      const key = zone.displayName ? `n:${zone.displayName}` : `s:${zone.shortName}`;
      let place = byKey.get(key);

      if (!place) {
        byKey.set(key, (place = { key, name: zone.displayName ?? zone.shortName, maps: [] }));
      }

      place.maps.push(zone);
    }

    return [...byKey.values()];
  }, [catalog]);

  const shown = useMemo(
    () =>
      places
        .map((place) => ({ place, hit: fuzzyMatch(place.name, filter) }))
        .filter((x) => x.hit !== null)
        .sort((a, b) => b.hit!.score - a.hit!.score)
        .slice(0, 300)
        .map((x) => x.place),
    [places, filter],
  );

  const place = places.find((p) => p.maps.some((m) => m.shortName === selected));

  const entry: MapCatalogEntry | undefined = catalog?.zones.find(
    (z) => z.shortName === selected,
  );

  /** Opens a place on whichever of its maps the user last chose. */
  const openPlace = (p: { name: string; maps: MapCatalogEntry[] }): string => {
    const override = chosenFor(settings, p.name, install);
    const target =
      override && p.maps.some((m) => m.shortName === override) ? override : p.maps[0].shortName;

    setSelected(target);
    setSet(undefined);
    setSteered(true);
    return target;
  };

  /**
   * The rail's idea of "go to a zone", in either mode. On the Zone view it
   * draws the zone; on the World view it frames and names it in the graph —
   * the same list, the same click, the same current zone (so the Mobs tab's
   * roster follows), and the world stays the world.
   */
  const pickPlace = (p: { name: string; maps: MapCatalogEntry[] }) => {
    const target = openPlace(p);
    if (mode === "world") {
      setWorldFocus((f) => ({ zone: target, seq: (f?.seq ?? 0) + 1 }));
    }
  };

  /** The same, by map short name — for a place chip on a mob search result. */
  const pickShortName = (shortName: string) => {
    setSelected(shortName);
    setSet(undefined);
    setSteered(true);
    if (mode === "world") {
      setWorldFocus((f) => ({ zone: shortName, seq: (f?.seq ?? 0) + 1 }));
    }
  };

  // Another view asked for a zone (the Bestiary's "show on map", or a crumb
  // back). By the map short name it named when the catalogue has it; else by
  // the place's name; else through the zone table — the same road the log's
  // own zone takes. Waits for the catalogue, since the ask usually arrives
  // with the view still mounting.
  useEffect(() => {
    if (!target || !catalog?.found) {
      return;
    }

    asked.current = true;
    followed.current = true;
    setFollowDone(true);
    setMode("zone");
    setSpawn(target.spawn ?? null);
    setSteered(true);
    setSet(undefined);

    if (target.shortName && catalog.zones.some((z) => z.shortName === target.shortName)) {
      setSelected(target.shortName);
      return;
    }

    const byName = places.find((p) => zoneKey(p.name) === zoneKey(target.place));
    if (byName) {
      const override = chosenFor(settings, byName.name, install);
      setSelected(
        override && byName.maps.some((m) => m.shortName === override) ? override : byName.maps[0].shortName,
      );
      return;
    }

    api
      .resolveZone(target.place)
      .then((r) => {
        const hit = r.shortNames.find((s) => catalog.zones.some((z) => z.shortName === s));
        if (hit) setSelected(hit);
      })
      .catch(() => undefined);
    // `places` and `settings` are read, not followed: this fires on the ask.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [target?.seq, catalog]);

  // The roster for the zone on screen, when the Mobs tab is open. One shard
  // per zone, cached by the server; a zone the site does not cover is a
  // quick "not known" rather than an error.
  useEffect(() => {
    if (!referenceEnabled || railTab !== "mobs" || !selected || rosterFor === selected) {
      return;
    }

    let cancelled = false;
    setRosterLoading(true);
    api
      .zoneRoster(selected)
      .then((r) => {
        if (cancelled) return;
        setRoster(r.roster);
        setRosterFor(selected);
      })
      .catch(() => !cancelled && setRoster(null))
      .finally(() => !cancelled && setRosterLoading(false));
    return () => {
      cancelled = true;
    };
  }, [referenceEnabled, railTab, selected, rosterFor]);

  // Where does a mob stand: the same search as the Bestiary's, and every row
  // already carries its zones — that is what the id scheme buys.
  useEffect(() => {
    if (!referenceEnabled) return;
    window.clearTimeout(mobDebounce.current);
    const q = mobQuery.trim();
    if (q.length < 2) {
      setMobResults(null);
      return;
    }
    mobDebounce.current = window.setTimeout(() => {
      api
        .searchNpcs(q, { limit: 40 })
        .then((r) => setMobResults(r.npcs))
        .catch(() => setMobResults([]));
    }, 250);
    return () => window.clearTimeout(mobDebounce.current);
  }, [mobQuery, referenceEnabled]);

  /** Kills this server's logs recorded in the place on screen, by name. */
  const killedHere = useMemo(() => {
    if (!mobs || !place) return new Map<string, number>();
    const key = zoneKey(place.name);
    const out = new Map<string, number>();
    for (const m of mobs.mobs) {
      if (zoneKey(stripInstance(m.zone)) !== key) continue;
      const k = m.mob.trim().toLowerCase();
      out.set(k, (out.get(k) ?? 0) + m.samples);
    }
    return out;
  }, [mobs, place]);

  /**
   * The roster, one row per name. The site lists a name once per level it
   * stands at, and the log's kills are per name — so "a shin ghoul knight
   * ×198" beside four level rows reads as 792. One row, its level span, its
   * points together, its kills once.
   */
  const rosterRows = useMemo(() => {
    if (!roster || rosterFor !== selected || !roster.known) return [];
    const byName = new Map<
      string,
      { name: string; id: number; minLevel?: number; maxLevel?: number; spawnPoints: number; locations: number[][]; kills: number }
    >();
    for (const n of roster.npcs) {
      const key = n.name.trim().toLowerCase();
      const row = byName.get(key) ?? {
        name: n.name,
        id: n.id,
        minLevel: n.level,
        maxLevel: n.maxLevel ?? n.level,
        spawnPoints: 0,
        locations: [],
        kills: killedHere.get(key) ?? 0,
      };
      if (n.level !== undefined) {
        row.minLevel = row.minLevel === undefined ? n.level : Math.min(row.minLevel, n.level);
        const top = n.maxLevel ?? n.level;
        row.maxLevel = row.maxLevel === undefined ? top : Math.max(row.maxLevel, top);
      }
      row.spawnPoints += n.spawnPoints;
      row.locations = row.locations.concat(n.locations);
      byName.set(key, row);
    }
    return [...byName.values()].sort(
      (a, b) => (a.minLevel ?? 999) - (b.minLevel ?? 999) || a.name.localeCompare(b.name),
    );
  }, [roster, rosterFor, selected, killedHere]);

  /** The crumb this zone leaves behind when a mob is opened from it. */
  const crumbHere = (): Crumb => ({
    view: "map",
    label: place?.name ?? entry?.displayName ?? selected ?? "Map",
    map: selected ? { place: place?.name ?? selected, shortName: selected, spawn: spawn ?? undefined } : undefined,
  });

  /** From a search result's place chip: open that zone, drawing the mob's points there. */
  const goToPlace = async (row: NpcBrowseRow, shortName: string, maps: string[], id: number) => {
    // The site's short name may be the other drawing of the place; open on
    // whichever of the place's maps this install has.
    const known = maps.find((m) => catalog?.zones.some((z) => z.shortName === m)) ?? shortName;
    const detail = await api.npcDetail(id).catch(() => null);
    const points = detail?.detail.zones.find((z) => z.shortName === shortName)?.locations ?? [];
    // On the World view the chip frames the zone in the world; the spawn
    // points are kept for when the Zone view is opened on it.
    pickShortName(known);
    setSpawn(points.length > 0 ? { mob: row.name, points } : null);
  };

  const markers = useMemo<MapMarker[]>(() => {
    const out: MapMarker[] = [];
    if (spawn) out.push(...toMarkers(spawn));
    if (hoverSpawn && hoverSpawn.mob !== spawn?.mob) out.push(...toMarkers(hoverSpawn, true));
    else if (hoverSpawn && spawn) {
      // Pointing at the pinned mob: light what is already drawn.
      return toMarkers(spawn, true);
    }
    return out;
  }, [spawn, hoverSpawn]);

  /** Binds a zone name to a map, or forgets the binding when null. */
  const bind = (zone: string, shortName: string | null) => {
    rememberMap(zone, shortName, install)
      .then((next) => {
        setSettings(next);
        setUnresolved(false);
        setCorrecting(false);
      })
      .catch(() => undefined);
  };

  /**
   * The place on screen is the one the log's zone name means. The place's own
   * "use for" control and the zone's are then one setting under one key, so
   * only the place's is shown.
   */
  const sameName = !!currentZone && !!place && zoneKey(place.name) === zoneKey(currentZone);

  /**
   * The zone name as a binding sees it — instance suffix off, since "The Ruins
   * of Old Guk 3 (Fused)" is remembered as, and reads better as, the Ruins of
   * Old Guk.
   */
  const zoneName = currentZone ? stripInstance(currentZone) : "";

  /** The map on screen is one the user bound the log's zone name to. */
  const boundHere = !!currentZone && !!selected && chosenFor(settings, currentZone, install) === selected;

  const applyRoot = (path: string | null) => {
    setBusy(true);
    setRootError(null);

    api
      .setMapRoot(path)
      .then((c) => {
        setCatalog(c);
        setRootDraft(c.userRoot ?? "");
        setSelected(null);
        followed.current = false;
      })
      .catch((e: Error) => setRootError(e.message))
      .finally(() => setBusy(false));
  };

  /** Follow an exit label — "to Butcherblock Mountains (Boat)". */
  const travel = (label: string) => {
    const destination = label
      .replace(/^(to|from)\s+/i, "")
      .replace(/\s*\(.*$/, "")
      .trim();

    api
      .resolveZone(destination)
      .then((r) => {
        const target = r.shortNames.find((s) =>
          catalog?.zones.some((z) => z.shortName === s),
        );
        if (target) {
          setSelected(target);
          setSet(undefined);
          setSteered(true);
        }
      })
      .catch(() => undefined);
  };

  const exits = useMemo(() => {
    if (!map) {
      return [];
    }

    const seen = new Set<string>();
    const out: string[] = [];

    for (const layer of map.layers) {
      for (const label of layer.labels) {
        if (/^(to|from)\s/i.test(label.text) && !seen.has(label.text)) {
          seen.add(label.text);
          out.push(label.text);
        }
      }
    }

    return out.sort((a, b) => a.localeCompare(b));
  }, [map]);

  if (error && !catalog) {
    return <div className="map-empty">Could not read the map catalogue: {error}</div>;
  }

  if (!catalog) {
    return <div className="map-empty">Looking for your EverQuest maps…</div>;
  }

  // Nothing found is a normal outcome on a machine without the game, and the
  // useful thing to say is where it looked.
  if (!catalog.found) {
    return (
      <div className="map-empty">
        <h3>No EverQuest maps found</h3>
        <p>
          Maps are read from your EverQuest install rather than shipped with
          EQDeeps, so this needs the game's <code>maps</code> folder — or a copy
          of one — on this machine.
        </p>
        {catalog.roots.length > 0 ? (
          <>
            <p>Looked in:</p>
            <ul className="map-roots">
              {catalog.roots.map((r) => (
                <li key={r}>{r}</li>
              ))}
            </ul>
          </>
        ) : (
          <p>No EverQuest install was found on this machine.</p>
        )}

        <p>Point at one instead:</p>
        <div className="map-root-set">
          <input
            className="map-filter"
            placeholder="D:\EverQuest\maps"
            value={rootDraft}
            spellCheck={false}
            onChange={(e) => setRootDraft(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && applyRoot(rootDraft.trim() || null)}
          />
          <button
            className="mini-btn"
            disabled={busy || rootDraft.trim().length === 0}
            onClick={() => applyRoot(rootDraft.trim())}
          >
            {busy ? "checking…" : "use this folder"}
          </button>
        </div>
        {rootError && <p className="map-root-error">{rootError}</p>}
      </div>
    );
  }

  return (
    <div className="map-main">
      <aside className="map-rail">
        <div className="map-mode">
          <button
            className={"mini-btn" + (mode === "zone" ? " on" : "")}
            onClick={() => setMode("zone")}
          >
            Zone
          </button>
          <button
            className={"mini-btn" + (mode === "world" ? " on" : "")}
            onClick={() => setMode("world")}
            title="Every zone and how they join up"
          >
            World
          </button>
        </div>

        {/* The rail is the same in both modes — the same two lists, the same
            clicks — so the World is not a different app with an empty left
            edge. On the World a zone click frames rather than draws, and a
            mob under the pointer lights the zones it stands in. */}
        <>
            {/* Two lists share the rail: the zones, and the mobs. The mobs tab
                is the Bestiary's door into this view — who stands here, and
                where does that one stand — and it only appears when the
                reference site is switched on, since it has nothing to say
                otherwise. */}
            {referenceEnabled && (
              <div className="map-rail-tabs" role="tablist">
                <button
                  role="tab"
                  aria-selected={railTab === "zones"}
                  className={"map-rail-tab" + (railTab === "zones" ? " on" : "")}
                  onClick={() => setRailTab("zones")}
                >
                  Zones
                </button>
                <button
                  role="tab"
                  aria-selected={railTab === "mobs"}
                  className={"map-rail-tab" + (railTab === "mobs" ? " on" : "")}
                  onClick={() => setRailTab("mobs")}
                  title="Who stands in this zone, and where any mob stands"
                >
                  Mobs
                </button>
              </div>
            )}

            {(railTab === "zones" || !referenceEnabled) && (
              <>
                <input
                  className="map-filter"
                  placeholder={`Search ${catalog.zones.length} zones…`}
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                />
                <div className="map-zone-list">
                  {shown.map((p) => (
                    <button
                      key={p.key}
                      className={"map-zone" + (place?.key === p.key ? " on" : "")}
                      onClick={() => pickPlace(p)}
                      title={
                        (mode === "world" ? "Frame in the world: " : "") +
                        p.maps.map((m) => m.shortName).join(", ")
                      }
                    >
                      <span className="map-zone-name">{p.name}</span>
                      {/* More than one map claims this name; the header lets you
                          pick which. Said here so the choice is discoverable
                          before you land on the zone. */}
                      {p.maps.length > 1 && (
                        <span className="map-zone-variants">{p.maps.length} maps</span>
                      )}
                      {/* An unnamed map is a file we can draw but cannot name. Say
                          so rather than showing a short name as if it were a place. */}
                      {!p.maps[0].displayName && <span className="map-zone-unnamed">unnamed</span>}
                    </button>
                  ))}
                  {shown.length === 0 && <div className="map-empty-small">No zone matches that.</div>}
                </div>
              </>
            )}

            {railTab === "mobs" && referenceEnabled && (
              <>
                <input
                  className="map-filter"
                  placeholder="Where does a mob stand?"
                  value={mobQuery}
                  onChange={(e) => setMobQuery(e.target.value)}
                />
                <div className="map-zone-list map-mob-list">
                  {mobQuery.trim().length >= 2 ? (
                    <>
                      {mobResults === null && <div className="map-empty-small">Searching…</div>}
                      {mobResults !== null && mobResults.length === 0 && (
                        <div className="map-empty-small">Nothing by that name.</div>
                      )}
                      {(mobResults ?? []).map((row) => (
                        <div
                          key={row.name}
                          className={"map-mob" + (litMob?.name === row.name ? " lit" : "")}
                          onMouseEnter={() => setLitMob(row)}
                          onMouseLeave={() => setLitMob((m) => (m?.name === row.name ? null : m))}
                        >
                          <button
                            className="map-mob-name"
                            onClick={() => onOpenMob?.({ name: row.name }, crumbHere())}
                            title={`Open ${row.name} in the Bestiary`}
                          >
                            <span>{row.name}</span>
                            <span className="map-mob-lvl">
                              {row.minLevel !== undefined
                                ? `L${row.minLevel}${row.maxLevel !== undefined && row.maxLevel !== row.minLevel ? `–${row.maxLevel}` : ""}`
                                : ""}
                            </span>
                          </button>
                          {/* Its zones, each a click to go there with the
                              spawn points drawn. A place with no name is one
                              this build has no map row for. */}
                          <div className="map-mob-places">
                            {row.places.map((p) =>
                              p.shortName ? (
                                <button
                                  key={p.id}
                                  className={"map-mob-place" + (p.shortName === selected || p.maps.includes(selected ?? "") ? " on" : "")}
                                  onClick={() => void goToPlace(row, p.shortName!, p.maps, p.id)}
                                  title={`${p.name} — L${p.levels.join(", ")}${p.era ? ` · ${p.era}` : ""}`}
                                >
                                  {p.name}
                                </button>
                              ) : (
                                <span key={p.id} className="map-mob-place none" title="A zone this build has no map for">
                                  elsewhere
                                </span>
                              ),
                            )}
                          </div>
                        </div>
                      ))}
                    </>
                  ) : (
                    <>
                      <div className="map-mob-heading">
                        {place?.name ?? entry?.displayName ?? selected}
                        {roster && rosterFor === selected && roster.known && (
                          <span className="subtle"> · {rosterRows.length} names</span>
                        )}
                      </div>
                      {rosterLoading && <div className="map-empty-small">Reading the roster…</div>}
                      {!rosterLoading && roster && rosterFor === selected && !roster.known && killedHere.size === 0 && (
                        <div className="map-empty-small">
                          The reference site lists nothing for this zone, and your logs have killed nothing here.
                        </div>
                      )}
                      {!rosterLoading && roster && rosterFor === selected && roster.known && roster.npcs.length === 0 && (
                        <div className="map-empty-small">Nothing listed here.</div>
                      )}
                      {/* The roster: level, name, and — where the logs have
                          them — this server's kills. Pointing at a row lights
                          its spawn points; the pin keeps them; the name opens
                          the mob. */}
                      {rosterRows.map((n) => {
                        const pinned = spawn?.mob === n.name;
                        const lvl =
                          n.minLevel === undefined
                            ? ""
                            : n.maxLevel !== undefined && n.maxLevel !== n.minLevel
                              ? `L${n.minLevel}–${n.maxLevel}`
                              : `L${n.minLevel}`;
                        return (
                          <div
                            key={n.id}
                            className={"map-mob map-roster-row" + (pinned ? " pinned" : "") + (n.kills > 0 ? " killed" : "")}
                            onMouseEnter={() => n.locations.length > 0 && setHoverSpawn({ mob: n.name, points: n.locations })}
                            onMouseLeave={() => setHoverSpawn(null)}
                          >
                            <button
                              className="map-mob-name"
                              onClick={() => onOpenMob?.({ name: n.name, id: n.id }, crumbHere())}
                              title={`Open ${n.name} in the Bestiary`}
                            >
                              <span className="map-mob-lvl">{lvl}</span>
                              <span className="map-mob-text">{n.name}</span>
                              {n.kills > 0 && (
                                <span className="map-mob-kills" title={`Your logs killed ${n.kills} here`}>
                                  ×{fmtNum(n.kills)}
                                </span>
                              )}
                            </button>
                            {n.locations.length > 0 && (
                              <button
                                className={"map-mob-pin" + (pinned ? " on" : "")}
                                onClick={() => setSpawn(pinned ? null : { mob: n.name, points: n.locations })}
                                title={pinned ? "Stop drawing its spawn points" : `Draw its ${n.spawnPoints} spawn point${n.spawnPoints === 1 ? "" : "s"}`}
                              >
                                <IconMapPin size={13} stroke={1.8} aria-hidden />
                              </button>
                            )}
                          </div>
                        );
                      })}
                      {/* Kills the logs have here that the roster does not
                          list — or everything, when there is no roster. */}
                      {[...killedHere.entries()]
                        .filter(([k]) => !rosterRows.some((n) => n.name.trim().toLowerCase() === k))
                        .sort((a, b) => b[1] - a[1])
                        .map(([k, kills]) => {
                          const name = mobs?.mobs.find((m) => m.mob.trim().toLowerCase() === k)?.mob ?? k;
                          return (
                            <div key={"killed:" + k} className="map-mob map-roster-row killed">
                              <button
                                className="map-mob-name"
                                onClick={() => onOpenMob?.({ name }, crumbHere())}
                                title={`Open ${name} in the Bestiary — your logs killed ${kills} here`}
                              >
                                <span className="map-mob-lvl subtle">you</span>
                                <span className="map-mob-text">{name}</span>
                                <span className="map-mob-kills">×{fmtNum(kills)}</span>
                              </button>
                            </div>
                          );
                        })}
                    </>
                  )}
                </div>
              </>
            )}
        </>
      </aside>

      {mode === "world" ? (
        <ZoneGraphView
          focus={worldFocus?.zone}
          focusSeq={worldFocus?.seq}
          onBack={() => setMode("zone")}
          // A graph node is a place, and a place opens on whichever of its
          // drawings the user last chose — the same rule as the zone list.
          onOpenZone={(shortName) => {
            setMode("zone");
            const p = places.find((x) => x.maps.some((m) => m.shortName === shortName));
            if (p) {
              openPlace(p);
            } else {
              setSelected(shortName);
            }
          }}
          currentZone={currentZone}
          currentMap={currentZone ? chosenFor(settings, currentZone, install) : undefined}
          lit={litZones}
          litLabel={litMob?.name}
          era={eraFor(settings, install)}
          onEraChange={(era) => {
            rememberEra(era, install)
              .then(setSettings)
              .catch(() => undefined);
          }}
        />
      ) : (
        <section className="map-stage">
          <header className="map-header">
            <div>
              <h2>{entry?.displayName ?? selected}</h2>
              <span className="map-sub">
                {selected}
                {entry?.nameSource && (
                  <em className="map-prov" title={SOURCE_NOTE[entry.nameSource]}>
                    {" "}· {entry.nameSource}
                  </em>
                )}
                {/* The era code as the table writes it, beside the name's
                    provenance: both are claims about this row, and both are
                    only as good as how they were arrived at. */}
                {entry?.era && (
                  <em className="map-prov" title={ERA_NOTE[entry.eraSource ?? "id"]}>
                    {" "}· {entry.era}
                  </em>
                )}
              </span>
            </div>

            <div className="map-controls">
              {/* Whose spawn points are drawn. The name opens the mob; the ✕
                  clears the drawing. */}
              {spawn && (
                <span className="map-spawn-chip">
                  <IconMapPin size={12} stroke={2} aria-hidden />
                  <button
                    className="map-spawn-name"
                    onClick={() => onOpenMob?.({ name: spawn.mob }, crumbHere())}
                    title={`Open ${spawn.mob} in the Bestiary`}
                  >
                    {spawn.mob}
                  </button>
                  <span className="subtle">
                    {spawn.points.length} spawn point{spawn.points.length === 1 ? "" : "s"}
                  </span>
                  <button className="map-spawn-clear" onClick={() => setSpawn(null)} title="Stop drawing these">
                    <IconX size={12} stroke={2} aria-hidden />
                  </button>
                </span>
              )}

              {/* Out to the world, landing on this zone. Right-click on the
                  map does the same; the button is there so it can be found. */}
              {selected && (
                <button
                  className="mini-btn"
                  onClick={showInWorld}
                  title="Show this zone in the world map. Right-click on the map does the same; right-click there comes back."
                >
                  world
                </button>
              )}

              {/* Which map file to draw this place from. Only appears when
                  something is actually being chosen between. Switching here
                  is just looking; the button beside it is what makes the
                  choice stick — which drawing is right depends on the install
                  (EverQuest Legends has the old Freeport, live the new), and
                  a silent write on every look was invisible and surprising. */}
              {place && place.maps.length > 1 && (
                <select
                  className="mini-select"
                  value={selected ?? ""}
                  onChange={(e) => {
                    setSelected(e.target.value);
                    setSet(undefined);
                    setSteered(true);
                  }}
                  title={`More than one map file claims "${place.name}"`}
                >
                  {place.maps.map((m) => (
                    <option key={m.shortName} value={m.shortName}>
                      {m.shortName}
                    </option>
                  ))}
                </select>
              )}

              {place && place.maps.length > 1 && selected && chosenFor(settings, place.name, install) !== selected && (
                <button
                  className="mini-btn"
                  onClick={() => bind(place.name, selected)}
                  title={`Open "${place.name}" on ${selected} from now on${install ? ` on ${install}` : ""} — the drawing this install's world uses`}
                >
                  use for “{place.name}”
                </button>
              )}

              {place && place.maps.length > 1 && selected && chosenFor(settings, place.name, install) === selected && (
                <button
                  className="mini-btn on"
                  onClick={() => bind(place.name, null)}
                  title={`"${place.name}" opens on ${selected}${install ? ` on ${install}` : ""}. Click to forget and go back to the first drawing.`}
                >
                  remembered ✕
                </button>
              )}

              {entry && entry.sets.length > 1 && (
                <select
                  className="mini-select"
                  value={map?.set ?? entry.sets[0]}
                  onChange={(e) => setSet(e.target.value)}
                  title="Which drawing of this zone to show"
                >
                  {entry.sets.map((s) => (
                    <option key={s} value={s}>
                      {s}
                    </option>
                  ))}
                </select>
              )}

              {map && map.layers.length > 1 && (
                <div className="map-layers">
                  {map.layers.map((l) => (
                    <button
                      key={l.index}
                      className={"mini-btn" + (hiddenLayers.includes(l.index) ? "" : " on")}
                      onClick={() =>
                        setHiddenLayers((h) =>
                          h.includes(l.index) ? h.filter((i) => i !== l.index) : [...h, l.index],
                        )
                      }
                      title={`${l.segments.toLocaleString()} lines, ${l.labels.length} labels`}
                    >
                      L{l.index}
                    </button>
                  ))}
                </div>
              )}

              <button
                className={"mini-btn" + (trueColors ? " on" : "")}
                onClick={() => setTrueColors((t) => !t)}
                title="Show the file's own colours, which were chosen for a light background"
              >
                true colour
              </button>

              {/* The table can be wrong or silent about the zone the log names.
                  This is how the person who can see both the map and the game
                  corrects it — but only offered while a correction is actually
                  in progress: with no map for the zone, or after "wrong map?".
                  Offered on every map, it read as "use East Freeport for the
                  Ruins of Old Guk" to someone merely browsing. */}
              {currentZone && selected && !sameName && !boundHere && (unresolved || correcting) && (
                <button
                  className="mini-btn"
                  onClick={() => bind(currentZone, selected)}
                  title={`Remember this map as the one for "${zoneName}"`}
                >
                  use for “{zoneName}”
                </button>
              )}

              {/* On a map the user bound the log's zone to: the undo. Unless
                  the place's own control above already stands for it — same
                  name, same key. */}
              {currentZone && boundHere && !(sameName && place && place.maps.length > 1) && (
                <button
                  className="mini-btn on"
                  onClick={() => bind(currentZone, null)}
                  title={`"${zoneName}" opens on this map because you said so. Click to forget and go back to the shipped table.`}
                >
                  remembered for “{zoneName}” ✕
                </button>
              )}

              {/* On the map the table opened for the log's zone: the way to
                  say it is the wrong one, which turns "use for" on elsewhere. */}
              {currentZone && sameName && !boundHere && !correcting && (
                <button
                  className="mini-btn"
                  onClick={() => setCorrecting(true)}
                  title={`The table opened this map for "${zoneName}". If that is wrong, pick the right one and it will be remembered.`}
                >
                  wrong map?
                </button>
              )}
            </div>
          </header>

          {unresolved && currentZone && (
            <div className="map-notice">
              The log says you are in <strong>{zoneName}</strong>, and no map
              is known for that name. Pick one on the left, then press{" "}
              <em>use for “{zoneName}”</em> and it will be remembered.
            </div>
          )}

          {correcting && !unresolved && currentZone && (
            <div className="map-notice">
              Pick the right map for <strong>{zoneName}</strong> on the left,
              then press <em>use for “{zoneName}”</em> and it will open there
              from now on.{" "}
              <button className="mini-btn" onClick={() => setCorrecting(false)}>
                never mind
              </button>
            </div>
          )}

          <div
            className="map-body"
            onContextMenu={(e) => {
              e.preventDefault();
              showInWorld();
            }}
          >
            {loading && <div className="map-loading">Reading the map…</div>}
            {map && !loading && (
              <MapCanvas
                map={map}
                layers={map.layers.map((l) => l.index).filter((i) => !hiddenLayers.includes(i))}
                trueColors={trueColors}
                highlight={highlight}
                markers={markers}
                onTravel={travel}
              />
            )}
            {!map && !loading && (
              <div className="map-empty-small">
                {!selected && hasLog
                  ? "Waiting for the log to say where you are…"
                  : "No map for that zone."}
              </div>
            )}
          </div>

          {exits.length > 0 && (
            <footer className="map-exits">
              <span className="map-exits-label">Exits</span>
              {exits.map((e) => (
                <button
                  key={e}
                  className="map-exit"
                  onMouseEnter={() => setHighlight(e)}
                  onMouseLeave={() => setHighlight(null)}
                  onClick={() => travel(e)}
                >
                  {e.replace(/^(to|from)\s+/i, "")}
                </button>
              ))}
            </footer>
          )}
        </section>
      )}
    </div>
  );
}
