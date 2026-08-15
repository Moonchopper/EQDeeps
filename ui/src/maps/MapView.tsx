import { useEffect, useMemo, useRef, useState } from "react";
import { api, type MapCatalog, type MapCatalogEntry, type ZoneMap } from "../api";
import { fuzzyMatch } from "../fuzzy";
import { MapCanvas } from "./MapCanvas";
import { ZoneGraphView } from "./ZoneGraphView";

/** How a name was arrived at, said plainly. Only the first two are verifiable. */
const SOURCE_NOTE: Record<string, string> = {
  name: "Name matches the client's own zone table.",
  graph: "Deduced from neighbouring maps, and confirmed by them naming it back.",
  curated: "Written down by hand — the name is checked, the pairing is not.",
};

interface Props {
  /** The zone the log says the character is in, if a log is open. */
  currentZone?: string;
}

export function MapView({ currentZone }: Props) {
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

  // The zone the log is in wins once, on arrival. After that the user is
  // steering: auto-following every zone line would yank the map out from under
  // someone reading it.
  const followed = useRef(false);

  useEffect(() => {
    api
      .mapCatalog()
      .then(setCatalog)
      .catch((e: Error) => setError(e.message));
  }, []);

  useEffect(() => {
    if (!catalog?.found || followed.current || !currentZone) {
      return;
    }

    followed.current = true;
    api
      .resolveZone(currentZone)
      .then((r) => {
        if (r.shortNames.length > 0) {
          setSelected(r.shortNames[0]);
        }
      })
      .catch(() => undefined);
  }, [catalog, currentZone]);

  // Fall back to the first zone so the view is never an empty frame.
  useEffect(() => {
    if (!selected && catalog?.zones.length) {
      setSelected(catalog.zones[0].shortName);
    }
  }, [catalog, selected]);

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

  const zones = useMemo(() => {
    if (!catalog) {
      return [];
    }

    return catalog.zones
      .map((z) => ({ zone: z, hit: fuzzyMatch(z.displayName ?? z.shortName, filter) }))
      .filter((x) => x.hit !== null)
      .sort((a, b) => b.hit!.score - a.hit!.score)
      .slice(0, 300)
      .map((x) => x.zone);
  }, [catalog, filter]);

  const entry: MapCatalogEntry | undefined = catalog?.zones.find(
    (z) => z.shortName === selected,
  );

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
          EQDeeps, so this needs the game on this machine.
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
              {zones.map((z) => (
                <button
                  key={z.shortName}
                  className={"map-zone" + (z.shortName === selected ? " on" : "")}
                  onClick={() => {
                    setSelected(z.shortName);
                    setSet(undefined);
                  }}
                  title={z.shortName}
                >
                  <span className="map-zone-name">{z.displayName ?? z.shortName}</span>
                  {/* An unnamed map is a file we can draw but cannot name. Say
                      so rather than showing a short name as if it were a place. */}
                  {!z.displayName && <span className="map-zone-unnamed">unnamed</span>}
                </button>
              ))}
              {zones.length === 0 && <div className="map-empty-small">No zone matches that.</div>}
            </div>
          </>
        )}
      </aside>

      {mode === "world" ? (
        <ZoneGraphView onOpenZone={(shortName) => { setMode("zone"); setSelected(shortName); }} />
      ) : (
        <section className="map-stage">
          <header className="map-header">
            <div>
              <h2>{entry?.displayName ?? selected}</h2>
              <span className="map-sub" title={entry?.nameSource ? SOURCE_NOTE[entry.nameSource] : undefined}>
                {selected}
                {entry?.nameSource && <em className="map-prov"> · {entry.nameSource}</em>}
              </span>
            </div>

            <div className="map-controls">
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
            </div>
          </header>

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
            {!map && !loading && <div className="map-empty-small">No map for that zone.</div>}
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
