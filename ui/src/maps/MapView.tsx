import { useEffect, useMemo, useRef, useState } from "react";
import { api, type MapCatalog, type MapCatalogEntry, type ZoneMap } from "../api";
import { fuzzyMatch } from "../fuzzy";
import { MapCanvas } from "./MapCanvas";
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
}

export function MapView({ currentZone, install, hasLog = false }: Props) {
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
    if (!catalog?.found || !settingsLoaded || followed.current || !currentZone || steered) {
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
  const openPlace = (p: { name: string; maps: MapCatalogEntry[] }) => {
    const override = chosenFor(settings, p.name, install);
    const target =
      override && p.maps.some((m) => m.shortName === override) ? override : p.maps[0].shortName;

    setSelected(target);
    setSet(undefined);
    setSteered(true);
  };

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

        {mode === "zone" && (
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
                  onClick={() => openPlace(p)}
                  title={p.maps.map((m) => m.shortName).join(", ")}
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
      </aside>

      {mode === "world" ? (
        <ZoneGraphView
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

          <div className="map-body">
            {loading && <div className="map-loading">Reading the map…</div>}
            {map && !loading && (
              <MapCanvas
                map={map}
                layers={map.layers.map((l) => l.index).filter((i) => !hiddenLayers.includes(i))}
                trueColors={trueColors}
                highlight={highlight}
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
