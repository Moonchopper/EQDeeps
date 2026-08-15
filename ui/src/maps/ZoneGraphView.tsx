import { useEffect, useMemo, useState } from "react";
import { api, type ZoneGraph, type ZoneRouteStep } from "../api";

interface Point {
  x: number;
  y: number;
}

/**
 * Lays the world out with a small force simulation: edges pull, everything
 * pushes apart.
 *
 * <p>Deterministic on purpose — positions start on a circle in name order
 * rather than at random, so the same world produces the same picture every
 * time. A layout that reshuffled on each visit would make the map harder to
 * learn, and learning the shape is the point of drawing it.</p>
 */
function layout(graph: ZoneGraph, iterations = 400): Map<string, Point> {
  const nodes = graph.zones.map((z) => z.shortName);
  const index = new Map(nodes.map((n, i) => [n, i]));
  const n = nodes.length;
  const pos: Point[] = [];

  const radius = 400;
  for (let i = 0; i < n; i++) {
    const a = (i / Math.max(1, n)) * Math.PI * 2;
    pos.push({ x: Math.cos(a) * radius, y: Math.sin(a) * radius });
  }

  const edges = graph.edges
    .map((e) => [index.get(e.from), index.get(e.to)] as [number | undefined, number | undefined])
    .filter((e): e is [number, number] => e[0] !== undefined && e[1] !== undefined);

  const area = radius * radius * 4;
  const k = Math.sqrt(area / Math.max(1, n));
  let temp = radius / 4;

  const disp: Point[] = pos.map(() => ({ x: 0, y: 0 }));

  for (let step = 0; step < iterations; step++) {
    for (let i = 0; i < n; i++) {
      disp[i].x = 0;
      disp[i].y = 0;
    }

    // Repulsion. O(n²), which at 264 zones is 70k pairs per step — small
    // enough that a quadtree would cost more to read than it saves.
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        let dx = pos[i].x - pos[j].x;
        let dy = pos[i].y - pos[j].y;
        let d2 = dx * dx + dy * dy;

        if (d2 < 0.01) {
          // Two zones exactly on top of each other have no direction to
          // separate along; nudge them by index so it stays deterministic.
          dx = ((i % 7) - 3) * 0.1;
          dy = ((j % 7) - 3) * 0.1;
          d2 = dx * dx + dy * dy || 0.01;
        }

        const d = Math.sqrt(d2);
        const force = (k * k) / d;
        const fx = (dx / d) * force;
        const fy = (dy / d) * force;

        disp[i].x += fx;
        disp[i].y += fy;
        disp[j].x -= fx;
        disp[j].y -= fy;
      }
    }

    for (const [a, b] of edges) {
      const dx = pos[a].x - pos[b].x;
      const dy = pos[a].y - pos[b].y;
      const d = Math.sqrt(dx * dx + dy * dy) || 0.01;
      const force = (d * d) / k;
      const fx = (dx / d) * force;
      const fy = (dy / d) * force;

      disp[a].x -= fx;
      disp[a].y -= fy;
      disp[b].x += fx;
      disp[b].y += fy;
    }

    for (let i = 0; i < n; i++) {
      const d = Math.sqrt(disp[i].x * disp[i].x + disp[i].y * disp[i].y) || 0.01;
      const limit = Math.min(d, temp);
      pos[i].x += (disp[i].x / d) * limit;
      pos[i].y += (disp[i].y / d) * limit;
    }

    temp *= 0.98;
  }

  return new Map(nodes.map((name, i) => [name, pos[i]]));
}

interface Props {
  onOpenZone: (shortName: string) => void;
}

export function ZoneGraphView({ onOpenZone }: Props) {
  const [graph, setGraph] = useState<ZoneGraph | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [route, setRoute] = useState<ZoneRouteStep[] | null>(null);
  const [noRoute, setNoRoute] = useState(false);
  const [hover, setHover] = useState<string | null>(null);

  useEffect(() => {
    api
      .zoneGraph()
      .then(setGraph)
      .catch((e: Error) => setError(e.message));
  }, []);

  const positions = useMemo(() => (graph ? layout(graph) : new Map<string, Point>()), [graph]);

  const names = useMemo(() => {
    const m = new Map<string, string>();
    for (const z of graph?.zones ?? []) {
      m.set(z.shortName, z.displayName ?? z.shortName);
    }
    return m;
  }, [graph]);

  const onRoute = () => {
    if (!from || !to) {
      return;
    }

    api
      .zoneRoute(from, to)
      .then((r) => {
        setRoute(r.found ? r.route : null);
        setNoRoute(!r.found);
      })
      .catch(() => {
        setRoute(null);
        setNoRoute(true);
      });
  };

  const onPath = useMemo(() => {
    const set = new Set<string>();
    if (route) {
      for (let i = 1; i < route.length; i++) {
        const a = route[i - 1].shortName;
        const b = route[i].shortName;
        set.add(a < b ? `${a} ${b}` : `${b} ${a}`);
      }
    }
    return set;
  }, [route]);

  const onRouteNode = useMemo(
    () => new Set((route ?? []).map((s) => s.shortName)),
    [route],
  );

  if (error) {
    return <div className="map-empty">Could not build the world graph: {error}</div>;
  }

  if (!graph) {
    return (
      <div className="map-empty">
        Reading every map's exits… this takes a few seconds the first time, then
        it is remembered for the session.
      </div>
    );
  }

  const points = [...positions.values()];
  const minX = Math.min(...points.map((p) => p.x)) - 60;
  const maxX = Math.max(...points.map((p) => p.x)) + 60;
  const minY = Math.min(...points.map((p) => p.y)) - 40;
  const maxY = Math.max(...points.map((p) => p.y)) + 40;

  const sorted = [...graph.zones].sort((a, b) =>
    (a.displayName ?? a.shortName).localeCompare(b.displayName ?? b.shortName),
  );

  return (
    <section className="map-stage">
      <header className="map-header">
        <div>
          <h2>The world</h2>
          <span className="map-sub">
            {graph.zones.length} zones · {graph.edges.length} connections, from the maps' own
            labels
          </span>
        </div>

        <div className="map-controls">
          <select className="mini-select" value={from} onChange={(e) => setFrom(e.target.value)}>
            <option value="">From…</option>
            {sorted.map((z) => (
              <option key={z.shortName} value={z.shortName}>
                {z.displayName ?? z.shortName}
              </option>
            ))}
          </select>
          <select className="mini-select" value={to} onChange={(e) => setTo(e.target.value)}>
            <option value="">To…</option>
            {sorted.map((z) => (
              <option key={z.shortName} value={z.shortName}>
                {z.displayName ?? z.shortName}
              </option>
            ))}
          </select>
          <button className="mini-btn" onClick={onRoute} disabled={!from || !to}>
            route
          </button>
        </div>
      </header>

      <div className="map-body">
        <svg
          className="zone-graph"
          viewBox={`${minX} ${minY} ${maxX - minX} ${maxY - minY}`}
          preserveAspectRatio="xMidYMid meet"
        >
          {graph.edges.map((e) => {
            const a = positions.get(e.from);
            const b = positions.get(e.to);
            if (!a || !b) {
              return null;
            }

            const key = e.from < e.to ? `${e.from} ${e.to}` : `${e.to} ${e.from}`;
            const lit = onPath.has(key);

            return (
              <line
                key={key}
                x1={a.x}
                y1={a.y}
                x2={b.x}
                y2={b.y}
                className={"zone-edge" + (lit ? " on" : "")}
              />
            );
          })}

          {graph.zones.map((z) => {
            const p = positions.get(z.shortName);
            if (!p) {
              return null;
            }

            const lit = onRouteNode.has(z.shortName);
            const near = hover === z.shortName;

            return (
              <g
                key={z.shortName}
                transform={`translate(${p.x} ${p.y})`}
                className={"zone-node" + (lit ? " on" : "")}
                onMouseEnter={() => setHover(z.shortName)}
                onMouseLeave={() => setHover(null)}
                onClick={() => onOpenZone(z.shortName)}
              >
                <circle r={Math.min(10, 3 + z.degree * 0.5)} />
                {/* Only the hubs and whatever is being pointed at get a name:
                    264 labels at once is a smear, not a map. */}
                {(z.degree >= 7 || near || lit) && (
                  <text x={0} y={-9}>
                    {names.get(z.shortName)}
                  </text>
                )}
              </g>
            );
          })}
        </svg>
      </div>

      {noRoute && (
        <footer className="map-exits">
          <span className="map-exits-label">No route</span>
          <span className="map-empty-small">
            The maps' labels do not join those two. That is a gap in the
            annotation, not proof you cannot walk it.
          </span>
        </footer>
      )}

      {route && (
        <footer className="map-exits">
          <span className="map-exits-label">{route.length - 1} hops</span>
          {route.map((step, i) => (
            <button
              key={step.shortName}
              className="map-exit"
              onClick={() => onOpenZone(step.shortName)}
              title={step.via ?? undefined}
            >
              {i > 0 && <span className="map-arrow">→ </span>}
              {step.displayName ?? step.shortName}
            </button>
          ))}
        </footer>
      )}
    </section>
  );
}
