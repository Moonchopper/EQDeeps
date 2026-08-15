import { useEffect, useState } from "react";
import { api, type ContextTimeline, type ZoneMap } from "../api";
import { MapCanvas } from "./MapCanvas";

/**
 * The map, shrunk to sit in a dashboard beside a parse (F27).
 *
 * <p>Deliberately reduced: no zone list, no layer buttons, no set switch. The
 * question a panel answers is "where am I", and every control the full
 * destination has would be competing for space with the drawing. Anyone who
 * wants to explore has the Map rail entry.</p>
 *
 * <p>It follows the last zone the log named rather than the time frame. A panel
 * that re-drew as the user scrubbed a fight selection would be answering a
 * question nobody asked — the zone a fight happened in is already on the
 * context strip.</p>
 */
export function MapPanel({
  context,
  pinned,
}: {
  context: ContextTimeline | null;
  /** A map short name to hold on, instead of following the log. */
  pinned?: string;
}) {
  const [map, setMap] = useState<ZoneMap | null>(null);
  const [state, setState] = useState<"idle" | "loading" | "missing">("idle");

  const zoneName = context?.zones?.[context.zones.length - 1]?.label;

  useEffect(() => {
    let cancelled = false;

    const load = (shortName: string) => {
      setState("loading");
      api
        .zoneMap(shortName)
        .then((m) => {
          if (!cancelled) {
            setMap(m);
            setState("idle");
          }
        })
        .catch(() => {
          if (!cancelled) {
            setMap(null);
            setState("missing");
          }
        });
    };

    if (pinned) {
      load(pinned);
      return () => {
        cancelled = true;
      };
    }

    if (!zoneName) {
      setMap(null);
      setState("missing");
      return;
    }

    api
      .resolveZone(zoneName)
      .then((r) => {
        if (cancelled) {
          return;
        }
        if (r.shortNames.length === 0) {
          setMap(null);
          setState("missing");
          return;
        }
        load(r.shortNames[0]);
      })
      .catch(() => {
        if (!cancelled) setState("missing");
      });

    return () => {
      cancelled = true;
    };
  }, [pinned, zoneName]);

  if (state === "missing" || !map) {
    return (
      <div className="map-empty-small">
        {state === "loading"
          ? "Reading the map…"
          : zoneName
            ? `No map for ${zoneName}.`
            : // The zone timeline lands after the backfill, so "no zone" and
              // "not yet" are different things and looked identical for the
              // first several seconds of every log.
              context === null
              ? "Waiting for the log…"
              : "No zone line in this log yet — or pin a zone on this panel."}
      </div>
    );
  }

  return (
    <div className="map-panel">
      <MapCanvas map={map} layers={map.layers.map((l) => l.index)} />
    </div>
  );
}
