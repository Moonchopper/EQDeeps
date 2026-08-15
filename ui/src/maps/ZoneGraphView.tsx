import { useEffect, useMemo, useRef, useState } from "react";
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
  const degree = new Map(graph.zones.map((z) => [z.shortName, z.degree]));
  const index = new Map(nodes.map((n, i) => [n, i]));
  const n = nodes.length;
  const pos: Point[] = [];

  // The starting circle scales with the node count so that the *spacing*
  // between zones comes out the same in every component. A fixed radius gives
  // a five-zone pocket the same area as a two-hundred-zone continent, and once
  // those are packed side by side the pocket takes half the frame.
  const radius = Math.max(60, 34 * Math.sqrt(n));
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

    // Gravity. Without it nothing bounds repulsion, and a zone with few
    // connections drifts until it is off in a corner on its own — which then
    // sets the viewBox and squashes the entire world into a speck in the
    // middle. Edges alone cannot hold a sparse graph together.
    for (let i = 0; i < n; i++) {
      const pull = 0.06 * (1 + Math.min(4, degree.get(nodes[i]) ?? 0));
      disp[i].x -= pos[i].x * pull;
      disp[i].y -= pos[i].y * pull;
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

/**
 * Splits the graph into connected components, lays each out on its own, and
 * packs them into rows.
 *
 * <p>The world is not one piece. Beyond the mainland there are pockets of two
 * or three zones that connect to each other and to nothing else, and running
 * one simulation over the lot pushes those pockets to the far corners — where
 * they set the viewBox and squash the mainland into the middle third. Laying
 * them out separately and placing them costs a little code and buys back most
 * of the frame.</p>
 */
function packedLayout(graph: ZoneGraph): Map<string, Point> {
  const adjacency = new Map<string, string[]>();
  for (const z of graph.zones) {
    adjacency.set(z.shortName, []);
  }
  for (const e of graph.edges) {
    adjacency.get(e.from)?.push(e.to);
    adjacency.get(e.to)?.push(e.from);
  }

  const seen = new Set<string>();
  const components: string[][] = [];

  for (const z of graph.zones) {
    if (seen.has(z.shortName)) {
      continue;
    }

    const members: string[] = [];
    const queue = [z.shortName];
    seen.add(z.shortName);

    while (queue.length) {
      const at = queue.pop()!;
      members.push(at);
      for (const next of adjacency.get(at) ?? []) {
        if (!seen.has(next)) {
          seen.add(next);
          queue.push(next);
        }
      }
    }

    components.push(members);
  }

  components.sort((a, b) => b.length - a.length);

  const out = new Map<string, Point>();
  let cursorX = 0;
  let cursorY = 0;
  let rowHeight = 0;
  let rowWidth = 0;
  const maxRowWidth = Math.max(600, Math.sqrt(graph.zones.length) * 90);

  for (const members of components) {
    const keep = new Set(members);
    const sub: ZoneGraph = {
      zones: graph.zones.filter((z) => keep.has(z.shortName)),
      edges: graph.edges.filter((e) => keep.has(e.from) && keep.has(e.to)),
    };

    // A pair or a triple does not need 400 iterations to find its shape.
    const placed = layout(sub, members.length > 8 ? 400 : 80);
    const pts = [...placed.values()];
    const minX = Math.min(...pts.map((p) => p.x));
    const maxX = Math.max(...pts.map((p) => p.x));
    const minY = Math.min(...pts.map((p) => p.y));
    const maxY = Math.max(...pts.map((p) => p.y));
    const w = maxX - minX + 70;
    const h = maxY - minY + 70;

    if (rowWidth > 0 && rowWidth + w > maxRowWidth) {
      cursorX = 0;
      cursorY += rowHeight;
      rowHeight = 0;
      rowWidth = 0;
    }

    for (const [name, p] of placed) {
      out.set(name, { x: p.x - minX + cursorX, y: p.y - minY + cursorY });
    }

    cursorX += w;
    rowWidth += w;
    rowHeight = Math.max(rowHeight, h);
  }

  return out;
}

interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}

/**
 * Deep enough to read a crowded corner, not so deep you end up in the gap
 * between two zones with nothing on screen. The world is ~5000 units across
 * and 40× put the viewport inside a single edge.
 */
const MAX_ZOOM = 12;
const MIN_ZOOM = 0.6;

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

  const wrapRef = useRef<HTMLDivElement | null>(null);
  const dragRef = useRef<{ x: number; y: number } | null>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });

  /** The viewBox, or null to sit at whatever currently fits. */
  const [view, setView] = useState<Box | null>(null);

  useEffect(() => {
    const wrap = wrapRef.current;
    if (!wrap) {
      return;
    }

    const observer = new ResizeObserver(() =>
      setSize({ w: wrap.clientWidth, h: wrap.clientHeight }),
    );
    observer.observe(wrap);
    setSize({ w: wrap.clientWidth, h: wrap.clientHeight });

    return () => observer.disconnect();
  }, [graph]);

  useEffect(() => {
    api
      .zoneGraph()
      .then(setGraph)
      .catch((e: Error) => setError(e.message));
  }, []);

  /**
   * Only zones with a labelled exit are drawn. A zone nothing connects to has
   * nothing to show in a picture whose entire subject is connections, and
   * drawing it puts a lone dot somewhere the layout has no reason to place.
   * The count is reported rather than quietly dropped.
   */
  const drawn = useMemo(() => {
    if (!graph) {
      return null;
    }

    const zones = graph.zones.filter((z) => z.degree > 0);
    const keep = new Set(zones.map((z) => z.shortName));

    return {
      graph: { zones, edges: graph.edges.filter((e) => keep.has(e.from) && keep.has(e.to)) },
      omitted: graph.zones.length - zones.length,
    };
  }, [graph]);

  const positions = useMemo(
    () => (drawn ? packedLayout(drawn.graph) : new Map<string, Point>()),
    [drawn],
  );

  /**
   * The whole world, framed to the container's shape.
   *
   * <p>Matched to the container's aspect ratio on purpose. With a viewBox of a
   * different shape, SVG letterboxes it, and then screen pixels no longer map
   * linearly onto view coordinates — which makes zooming about the pointer
   * quietly wrong, drifting a little further off with every notch.</p>
   */
  const fitted = useMemo<Box>(() => {
    const pts = [...positions.values()];
    if (pts.length === 0 || size.w === 0 || size.h === 0) {
      return { x: 0, y: 0, w: 1000, h: 1000 };
    }

    const minX = Math.min(...pts.map((p) => p.x)) - 60;
    const maxX = Math.max(...pts.map((p) => p.x)) + 60;
    const minY = Math.min(...pts.map((p) => p.y)) - 40;
    const maxY = Math.max(...pts.map((p) => p.y)) + 40;

    const aspect = size.h / size.w;
    const contentW = Math.max(1, maxX - minX);
    const contentH = Math.max(1, maxY - minY);
    const w = contentH / contentW > aspect ? contentH / aspect : contentW;
    const h = w * aspect;

    return {
      x: (minX + maxX) / 2 - w / 2,
      y: (minY + maxY) / 2 - h / 2,
      w,
      h,
    };
  }, [positions, size]);

  // A different world, or a resized frame, invalidates wherever we were.
  useEffect(() => setView(null), [positions, size.w, size.h]);

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

  if (!graph || !drawn) {
    return (
      <div className="map-empty">
        Reading every map's exits… this takes a few seconds the first time, then
        it is remembered for the session.
      </div>
    );
  }

  const box = view ?? fitted;

  // World units per screen pixel. Everything that should stay a constant size
  // on screen — dots, labels, stroke widths — is multiplied by this, so zooming
  // in shows *more* of the world rather than a bigger drawing of less of it.
  const unit = size.w > 0 ? box.w / size.w : 1;
  const nameSize = 11 * unit;

  // Labels thin out by connectedness rather than by chance, and the threshold
  // relaxes as you zoom in: at a distance only the hubs are named, and by the
  // time a handful of zones fill the frame everything has a name. Drawing all
  // 247 at once is a smear, and drawing none is the complaint that prompted
  // this.
  const zoom = fitted.w / box.w;
  const labelAbove = zoom >= 6 ? 0 : zoom >= 3 ? 1 : zoom >= 1.6 ? 3 : 7;

  const sorted = [...drawn.graph.zones].sort((a, b) =>
    (a.displayName ?? a.shortName).localeCompare(b.displayName ?? b.shortName),
  );

  return (
    <section className="map-stage">
      <header className="map-header">
        <div>
          <h2>The world</h2>
          <span className="map-sub">
            {drawn.graph.zones.length} zones · {drawn.graph.edges.length} connections, from the
            maps' own labels
            {drawn.omitted > 0 && ` · ${drawn.omitted} with no labelled exit not drawn`}
            {" · scroll to zoom, drag to pan"}
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
          <button
            className="mini-btn"
            onClick={() => setView(null)}
            disabled={view === null}
            title="Frame the whole world again"
          >
            {zoom > 1.05 ? `fit (${zoom.toFixed(1)}×)` : "fit"}
          </button>
        </div>
      </header>

      <div className="map-body" ref={wrapRef}>
        <svg
          className="zone-graph"
          viewBox={`${box.x} ${box.y} ${box.w} ${box.h}`}
          preserveAspectRatio="xMidYMid meet"
          style={{ cursor: dragRef.current ? "grabbing" : "grab" }}
          onWheel={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const fx = (e.clientX - rect.left) / rect.width;
            const fy = (e.clientY - rect.top) / rect.height;

            // The world point under the cursor has to stay under it.
            const wx = box.x + fx * box.w;
            const wy = box.y + fy * box.h;

            const factor = Math.exp(-e.deltaY * 0.0015);
            const wanted = box.w / factor;
            const w = Math.min(fitted.w / MIN_ZOOM, Math.max(fitted.w / MAX_ZOOM, wanted));
            const h = w * (box.h / box.w);

            setView({ x: wx - fx * w, y: wy - fy * h, w, h });
          }}
          onPointerDown={(e) => {
            e.currentTarget.setPointerCapture(e.pointerId);
            dragRef.current = { x: e.clientX, y: e.clientY };
          }}
          onPointerMove={(e) => {
            const drag = dragRef.current;
            if (!drag) {
              return;
            }

            setView({
              ...box,
              x: box.x - (e.clientX - drag.x) * unit,
              y: box.y - (e.clientY - drag.y) * unit,
            });
            dragRef.current = { x: e.clientX, y: e.clientY };
          }}
          onPointerUp={() => (dragRef.current = null)}
          onPointerLeave={() => {
            dragRef.current = null;
            setHover(null);
          }}
        >
          {drawn.graph.edges.map((e) => {
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
                // Inline, not the strokeWidth attribute: a CSS rule beats a
                // presentation attribute, so the stylesheet's width would win
                // and every line would thicken into a ribbon as you zoom in.
                style={{ strokeWidth: (lit ? 3 : 1) * unit }}
              />
            );
          })}

          {drawn.graph.zones.map((z) => {
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
                {/* Sizes are in view units scaled by `unit`, so a dot stays the
                    same size on screen however far in you are — zooming shows
                    more of the world, not a bigger picture of less of it. */}
                <circle r={Math.min(7, 2.5 + z.degree * 0.4) * unit} />
                <title>
                  {names.get(z.shortName)} — {z.degree}{" "}
                  {z.degree === 1 ? "connection" : "connections"}
                </title>
                {(z.degree >= labelAbove || near || lit) && (
                  <text
                    x={0}
                    y={-nameSize * 0.7}
                    style={{ fontSize: nameSize, strokeWidth: nameSize / 4 }}
                  >
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
