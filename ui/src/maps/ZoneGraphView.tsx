import { useEffect, useMemo, useRef, useState } from "react";
import { api, type ZoneGraph, type ZoneGraphNode, type ZoneRouteStep } from "../api";
import { fuzzyMatch, type FuzzyHit } from "../fuzzy";

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
      eras: graph.eras,
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
 * A zone name as SVG text runs, the matched letters marked so a fuzzy hit
 * shows its reasoning — the same idea as the tables' Highlight, but tspans,
 * because a <mark> cannot live inside <text>. Runs rather than one tspan per
 * letter: SVG lays tspans out inline, but hundreds of single-letter spans
 * across every visible label add up on a graph that redraws as you pan.
 */
function nameRuns(text: string, hit: FuzzyHit | undefined): JSX.Element[] | string {
  if (!hit || hit.positions.length === 0) {
    return text;
  }

  const matched = new Set(hit.positions);
  const out: JSX.Element[] = [];
  let run = "";
  let runIsMatch = matched.has(0);

  const flush = () => {
    if (run.length > 0) {
      out.push(
        runIsMatch ? (
          <tspan key={out.length} className="hit">
            {run}
          </tspan>
        ) : (
          <tspan key={out.length}>{run}</tspan>
        ),
      );
    }
    run = "";
  };

  for (let i = 0; i < text.length; i++) {
    const isMatch = matched.has(i);
    if (isMatch !== runIsMatch) {
      flush();
      runIsMatch = isMatch;
    }
    run += text[i];
  }
  flush();
  return out;
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
  /**
   * The expansion the player says their server has reached, as a `ZoneEra.id`,
   * or undefined for the whole world. Zones from later expansions are hidden
   * and never routed through; zones whose era is unknown stay (issue #57).
   */
  era?: string;
  onEraChange: (era: string | null) => void;
}

export function ZoneGraphView({ onOpenZone, era, onEraChange }: Props) {
  const [graph, setGraph] = useState<ZoneGraph | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [route, setRoute] = useState<ZoneRouteStep[] | null>(null);
  const [noRoute, setNoRoute] = useState(false);
  const [hover, setHover] = useState<string | null>(null);
  const [search, setSearch] = useState("");

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

  /** Position of each era in release order, from the server's own list. */
  const eraOrdinal = useMemo(
    () => new Map((graph?.eras ?? []).map((e, i) => [e.id, i])),
    [graph],
  );
  const eraById = useMemo(() => new Map((graph?.eras ?? []).map((e) => [e.id, e])), [graph]);

  /**
   * The chosen era as an ordinal, or undefined for no filter — which is also
   * what an era code this build does not know collapses to. Not filtering is
   * the safe reading of a setting we cannot interpret.
   */
  const eraLimit = era ? eraOrdinal.get(era) : undefined;

  /**
   * Only zones with a labelled exit are drawn. A zone nothing connects to has
   * nothing to show in a picture whose entire subject is connections, and
   * drawing it puts a lone dot somewhere the layout has no reason to place.
   * The count is reported rather than quietly dropped.
   *
   * <p>The era filter removes zones outright rather than dimming them: the
   * classic world is a fraction of the whole, and laid out on its own it
   * reads as the continents it is, where dimmed it would still be squeezed
   * into the corners by two hundred zones that are not there. Degree is
   * recomputed on what is left, so a hub is a hub *in this era* — the
   * Commonlands matter more in a world with no Plane of Knowledge to skip
   * them. A zone with no era is always
   * in: the table could not place it, and hiding a place the player can walk
   * into is the worse mistake.</p>
   */
  const drawn = useMemo(() => {
    if (!graph) {
      return null;
    }

    const withinEra = (z: ZoneGraphNode) =>
      eraLimit === undefined || !z.era || (eraOrdinal.get(z.era) ?? -1) <= eraLimit;

    const beyond = new Set(graph.zones.filter((z) => !withinEra(z)).map((z) => z.shortName));
    const edges = graph.edges.filter((e) => !beyond.has(e.from) && !beyond.has(e.to));

    const degree = new Map<string, number>();
    for (const e of edges) {
      degree.set(e.from, (degree.get(e.from) ?? 0) + 1);
      degree.set(e.to, (degree.get(e.to) ?? 0) + 1);
    }

    const zones = graph.zones
      .filter((z) => !beyond.has(z.shortName) && (degree.get(z.shortName) ?? 0) > 0)
      .map((z) => ({ ...z, degree: degree.get(z.shortName) ?? 0 }));

    return {
      graph: { zones, edges, eras: graph.eras },
      hidden: beyond.size,
      unknown: eraLimit === undefined ? 0 : zones.filter((z) => !z.era).length,
      omitted: graph.zones.length - beyond.size - zones.length,
    };
  }, [graph, eraLimit, eraOrdinal]);

  // A different era is a different world: whatever was picked or routed in
  // the old one may not exist in this one.
  useEffect(() => {
    if (!drawn) {
      return;
    }

    const keep = new Set(drawn.graph.zones.map((z) => z.shortName));
    setFrom((f) => (f && !keep.has(f) ? "" : f));
    setTo((t) => (t && !keep.has(t) ? "" : t));
    setRoute(null);
    setNoRoute(false);
  }, [drawn]);

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

  /**
   * Which drawn zones the search box finds, and where in the name it found
   * them. Fuzzy, like every other search box here — "gfay" and "cauld" are how
   * players actually say these — and matched against the short name as well
   * as the display name, since the file name is what a lot of people know.
   * A hit only carries letter positions when the *display* name matched;
   * a short-name hit lights the zone and marks nothing, rather than marking
   * letters in a name that is not the one on screen.
   *
   * <p>Null when nothing is being searched, which is a different state from
   * "everything matched": with no query, nothing is dimmed and the labels
   * follow the rank rule; with a query, only hits are named and everything
   * else steps back.</p>
   */
  const found = useMemo(() => {
    if (!drawn || search.trim().length === 0) {
      return null;
    }

    const hits = new Map<string, FuzzyHit>();
    for (const z of drawn.graph.zones) {
      const byDisplay = fuzzyMatch(z.displayName ?? z.shortName, search);
      if (byDisplay) {
        hits.set(z.shortName, byDisplay);
      } else if (fuzzyMatch(z.shortName, search)) {
        hits.set(z.shortName, { score: 0, positions: [] });
      }
    }
    return hits;
  }, [drawn, search]);

  const onRoute = () => {
    if (!from || !to) {
      return;
    }

    api
      .zoneRoute(from, to, era)
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
  //
  // The cut is a *rank*, not a number of connections. An absolute threshold —
  // "seven or more" at a distance — was tuned to the full world, and under an
  // era filter the world shrinks and degree is recounted on what is left: the
  // 86-zone classic world has no seven-way hub at all, so it drew no names
  // whatever. Naming the top share of whatever is drawn, with a floor of ten
  // so a small world still gets its landmarks, holds up at any size. Ties at
  // the cut are all named, which keeps the choice deterministic.
  const zoom = fitted.w / box.w;
  const share = zoom >= 6 ? 1 : zoom >= 3 ? 0.6 : zoom >= 1.6 ? 0.3 : 0.06;
  const byDegree = [...drawn.graph.zones].sort(
    (a, b) => b.degree - a.degree || a.shortName.localeCompare(b.shortName),
  );
  const budget = Math.min(byDegree.length, Math.max(10, Math.ceil(byDegree.length * share)));
  const labelAbove = budget > 0 ? byDegree[budget - 1].degree : Infinity;

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
            {drawn.hidden > 0 &&
              ` · ${drawn.hidden} later than ${eraById.get(era ?? "")?.short ?? era} hidden`}
            {drawn.unknown > 0 && ` · ${drawn.unknown} of unknown era kept`}
            {drawn.omitted > 0 &&
              ` · ${drawn.omitted} with no labelled exit${eraLimit === undefined ? "" : " in this era"} not drawn`}
            {found && ` · ${found.size === 0 ? "nothing matches" : `${found.size} match`} “${search.trim()}”`}
            {" · scroll to zoom, drag to pan"}
          </span>
        </div>

        <div className="map-controls">
          {/* Finds zones by name without moving anything: hits light up where
              they are and the rest steps back, so the search reads as "where
              is X" rather than a list to pick from. Escape clears it. */}
          <input
            className="map-filter map-search"
            placeholder="Find a zone…"
            value={search}
            spellCheck={false}
            onChange={(e) => setSearch(e.target.value)}
            onKeyDown={(e) => e.key === "Escape" && setSearch("")}
            title="Fuzzy: “gfay” finds The Greater Faydark. Matching zones light up and the rest dim."
          />
          {/* Which expansion the server has reached. The player's call: the
              log names only zones already visited, and the map files carry no
              content gating, so nothing here can guess it (issue #57). */}
          <select
            className="mini-select"
            value={eraLimit === undefined ? "" : era}
            onChange={(e) => onEraChange(e.target.value || null)}
            title="How far your server has unlocked. Zones from later expansions are hidden and never routed through; zones whose era is unknown stay. Nothing in the log can say this, so it is yours to set."
          >
            <option value="">Any era</option>
            {graph.eras.map((e, i) => (
              <option key={e.id} value={e.id}>
                {i === 0 ? `${e.short} only` : `through ${e.short}`} ({e.year})
              </option>
            ))}
          </select>
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
            // An edge that touches no hit steps back with the zones it joins.
            // A route stays lit regardless: it was asked for too.
            const dim = found !== null && !lit && !found.has(e.from) && !found.has(e.to);

            return (
              <line
                key={key}
                x1={a.x}
                y1={a.y}
                x2={b.x}
                y2={b.y}
                className={"zone-edge" + (lit ? " on" : "") + (dim ? " dim" : "")}
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
            const hit = found?.get(z.shortName);
            const dim = found !== null && hit === undefined && !lit;

            // With an era chosen, a zone the table could not place is kept
            // but marked: it may or may not exist on this server.
            const unsure = eraLimit !== undefined && !z.era;
            const eraNote = z.era
              ? ` · ${eraById.get(z.era)?.short ?? z.era}${z.eraSource === "curated" ? " (set by hand)" : ""}`
              : unsure
                ? " · era unknown, kept"
                : "";

            return (
              <g
                key={z.shortName}
                transform={`translate(${p.x} ${p.y})`}
                className={
                  "zone-node" +
                  (lit ? " on" : "") +
                  (unsure ? " unsure" : "") +
                  (hit ? " hit" : "") +
                  (dim ? " dim" : "")
                }
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
                  {eraNote}
                </title>
                {/* While searching, only hits are named — a dimmed label is
                    clutter over what you are looking for — and every hit is,
                    however small, because it is what you asked for. */}
                {(found ? hit !== undefined || near || lit : z.degree >= labelAbove || near || lit) && (
                  <text
                    x={0}
                    y={-nameSize * 0.7}
                    style={{ fontSize: nameSize, strokeWidth: nameSize / 4 }}
                  >
                    {nameRuns(names.get(z.shortName) ?? z.shortName, hit)}
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
            {eraLimit === undefined
              ? "The maps' labels do not join those two. That is a gap in the annotation, not proof you cannot walk it."
              : `The maps' labels do not join those two using only zones through ${eraById.get(era ?? "")?.short ?? era}. Either a later expansion is the way — try "Any era" — or the annotation has a gap.`}
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
