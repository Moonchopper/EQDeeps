import { useEffect, useMemo, useRef, useState } from "react";
import { api, type ZoneGraph, type ZoneGraphNode, type ZoneRouteStep } from "../api";
import { fuzzyMatch, type FuzzyHit } from "../fuzzy";
import { zoneKey } from "./mapSettings";

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
  /**
   * A zone to frame on arrival — the one the Zone view was showing when the
   * user asked for the world. `focusSeq` changes on every ask, so asking for
   * the same zone twice frames it twice.
   */
  focus?: string;
  focusSeq?: number;
  /** Right-click on the world: back to the Zone view. */
  onBack?: () => void;
  /** The zone the log last said the character entered, if a log is open. */
  currentZone?: string;
  /**
   * The map the user chose for that zone, if they chose one — so "you are
   * here" lands on the drawing they picked when a name has two.
   */
  currentMap?: string;
  /**
   * Zones to light from outside — the map short names a mob stands in, while
   * the rail's mob search is pointing at it. Drawn exactly as search hits are:
   * lit and named, everything else stepped back. Ignored while a search is
   * typed, since the search is the more deliberate ask.
   */
  lit?: ReadonlySet<string> | null;
  /** What the lit zones have in common, for the header — a mob's name. */
  litLabel?: string;
}

export function ZoneGraphView({
  onOpenZone,
  era,
  onEraChange,
  focus,
  focusSeq = 0,
  onBack,
  currentZone,
  currentMap,
  lit = null,
  litLabel,
}: Props) {
  const [graph, setGraph] = useState<ZoneGraph | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [route, setRoute] = useState<ZoneRouteStep[] | null>(null);
  const [noRoute, setNoRoute] = useState(false);
  const [hover, setHover] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  /** Whether a search also lights the zones connected to what it found. */
  const [withLinks, setWithLinks] = useState(true);

  const wrapRef = useRef<HTMLDivElement | null>(null);
  /**
   * The pan in progress: where the pointer went down, where it was last seen,
   * and whether it has moved far enough to be a drag rather than a click.
   */
  const dragRef = useRef<{ x: number; y: number; sx: number; sy: number; moved: boolean } | null>(null);
  /** Set once a drag has happened, so the click that ends it opens nothing. */
  const draggedRef = useRef(false);
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

  // A different world invalidates wherever we were.
  useEffect(() => setView(null), [positions]);

  /**
   * A resized frame does not: it keeps the same centre at the same zoom,
   * re-fitted to the new shape.
   *
   * <p>This used to reset to the fit as well, and that is what made zooming
   * "sticky" while a search was typed: the header's summary grows with the
   * search, the fit button's own label changes width with every wheel notch,
   * and at the wrap point either one bounces the header by a line — which
   * resizes the body, which threw the view away and re-fitted it. Several
   * notches were needed for one to survive. The zoom is remembered as a
   * factor over the fit rather than as a box, so it means the same thing
   * after the frame changes shape.</p>
   */
  const zoomRef = useRef(1);
  useEffect(() => {
    setView((v) => {
      if (!v || fitted.w <= 0) {
        return v;
      }
      const w = fitted.w / zoomRef.current;
      const h = w * (fitted.h / fitted.w);
      return { x: v.x + v.w / 2 - w / 2, y: v.y + v.h / 2 - h / 2, w, h };
    });
  }, [fitted]);

  // Recorded when the view changes, against the fit of the same render — not
  // when the fit changes, or a resize would record the old box against the
  // new fit and the effect above would read a zoom that never existed.
  useEffect(() => {
    zoomRef.current = view ? fitted.w / view.w : 1;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view]);

  /**
   * Coming from the Zone view, land on the zone that was open there: framed
   * at a readable zoom and named, so the answer to "where is this in the
   * world" is on screen before anything is clicked. Runs once per ask, once
   * the layout exists to frame against — declared after the reset above so
   * that on a fresh load the frame wins over the fit.
   */
  const framedSeq = useRef(0);
  useEffect(() => {
    if (framedSeq.current === focusSeq || !focus) {
      return;
    }

    const p = positions.get(focus);
    if (!p || size.w === 0) {
      return;
    }

    framedSeq.current = focusSeq;
    const w = fitted.w / 3;
    const h = fitted.h / 3;
    setView({ x: p.x - w / 2, y: p.y - h / 2, w, h });
    setHover(focus);
  }, [focus, focusSeq, positions, fitted, size.w]);

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
   * <p>With the connections toggle on, every zone one step from a hit is
   * lit as well — "Feerrott" then shows Innothule Swamp and Cazic-Thule
   * beside it — and each such zone remembers which hits it touches, so the
   * picture can say <em>why</em> it is lit: the connecting edge is drawn
   * and the label reads "via The Feerrott". Only edges in the drawn world
   * count, so under an era filter a neighbour that does not exist yet stays
   * dark.</p>
   *
   * <p>Null when nothing is being searched, which is a different state from
   * "everything matched": with no query, nothing is dimmed and the labels
   * follow the rank rule; with a query, only hits are named and everything
   * else steps back.</p>
   */
  const found = useMemo(() => {
    if (!drawn) {
      return null;
    }

    // Lit from outside — a mob's zones — and nothing typed: those are the
    // hits, with no letters to mark and no neighbours to add. A node is a
    // place and may carry two drawings, so it matches on any of its maps.
    if (search.trim().length === 0) {
      if (!lit || lit.size === 0) {
        return null;
      }
      const hits = new Map<string, FuzzyHit>();
      for (const z of drawn.graph.zones) {
        if (z.maps.some((m) => lit.has(m))) {
          hits.set(z.shortName, { score: 0, positions: [] });
        }
      }
      return { hits, via: new Map<string, string[]>(), external: true };
    }

    let hits = new Map<string, FuzzyHit>();
    for (const z of drawn.graph.zones) {
      const byDisplay = fuzzyMatch(z.displayName ?? z.shortName, search);
      if (byDisplay) {
        hits.set(z.shortName, byDisplay);
      } else if (z.maps.some((m) => fuzzyMatch(m, search))) {
        hits.set(z.shortName, { score: 0, positions: [] });
      }
    }

    // A scattered subsequence is how "gfay" finds The Greater Faydark, but it
    // is also how "hate" finds eleven zones that merely contain those letters
    // in order. The matcher scores a literal substring far above any scatter,
    // so when anything matches literally, only literal matches light; the
    // scatter is the fallback for abbreviations, not a peer of the real thing.
    const tokens = search.trim().split(/\s+/).length;
    const literal = new Map([...hits].filter(([, h]) => h.score >= 900 * tokens));
    if (literal.size > 0) {
      hits = literal;
    }

    // Neighbour → the hits it is connected to, in name order so the label
    // reads the same every time.
    const via = new Map<string, string[]>();
    if (withLinks) {
      for (const e of drawn.graph.edges) {
        for (const [hitEnd, other] of [[e.from, e.to], [e.to, e.from]] as const) {
          if (hits.has(hitEnd) && !hits.has(other)) {
            const list = via.get(other) ?? [];
            list.push(hitEnd);
            via.set(other, list);
          }
        }
      }
      for (const list of via.values()) {
        list.sort((a, b) => (names.get(a) ?? a).localeCompare(names.get(b) ?? b));
      }
    }

    return { hits, via, external: false };
  }, [drawn, search, withLinks, names, lit]);

  /**
   * The node the character is standing in, or null. Resolved through the
   * user's chosen map first, then by name — the log says a display name, with
   * an instance suffix the graph does not carry, so both go through the same
   * key the zone list uses. Looked up in the whole graph rather than the drawn
   * one, so "you are somewhere this picture is not showing" can be said.
   */
  const here = useMemo(() => {
    if (!graph || !currentZone) {
      return null;
    }

    const byChoice = currentMap
      ? graph.zones.find((z) => z.maps.includes(currentMap))
      : undefined;
    const key = zoneKey(currentZone);
    const node =
      byChoice ?? graph.zones.find((z) => z.displayName && zoneKey(z.displayName) === key);

    return node?.shortName ?? null;
  }, [graph, currentZone, currentMap]);

  const hereDrawn = here !== null && positions.has(here);

  // Where you are is the natural start of a route. Filled in only while
  // nothing is chosen, so it never overrides a pick — and only when the zone
  // is actually in this picture, since a hidden zone cannot be routed from.
  useEffect(() => {
    if (hereDrawn && here) {
      setFrom((f) => f || here);
    }
  }, [here, hereDrawn]);

  /** Frames the current zone at a readable zoom without losing the room around it. */
  const goHere = () => {
    const p = here ? positions.get(here) : undefined;
    if (!p) {
      return;
    }

    const w = fitted.w / 3;
    const h = fitted.h / 3;
    setView({ x: p.x - w / 2, y: p.y - h / 2, w, h });
  };

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
        Reading every map's exits… this takes a few seconds the first time; after
        that it is remembered, and only a map you have edited is read again.
      </div>
    );
  }

  const box = view ?? fitted;

  // World units per screen pixel. Everything that should stay a constant size
  // on screen — dots, labels, stroke widths — is multiplied by this, so zooming
  // in shows *more* of the world rather than a bigger drawing of less of it.
  const unit = size.w > 0 ? box.w / size.w : 1;
  const nameSize = 11 * unit;

  /** The "via …" note a connected zone's label carries, or "". */
  const viaNoteOf = (z: ZoneGraphNode): string => {
    const via = found?.via.get(z.shortName);
    return via
      ? "via " +
          via
            .slice(0, 2)
            .map((v) => names.get(v) ?? v)
            .join(", ") +
          (via.length > 2 ? ` +${via.length - 2}` : "")
      : "";
  };

  /** Whether a zone's name is drawn right now, and if so what it says. Shared by the label and the hit test. */
  const labelOf = (z: ZoneGraphNode): string | null => {
    const near = hover === z.shortName;
    const lit = onRouteNode.has(z.shortName);
    const isHere = z.shortName === here;
    const shown = found
      ? found.hits.has(z.shortName) || found.via.has(z.shortName) || near || lit || isHere
      : z.degree >= labelAbove || near || lit || isHere;
    if (!shown) {
      return null;
    }
    const via = viaNoteOf(z);
    return (names.get(z.shortName) ?? z.shortName) + (via ? ` · ${via}` : "") + (isHere ? " · you are here" : "");
  };

  /**
   * The zone nearest a screen point, within reach, or null.
   *
   * <p>The dot is three to seven pixels across — a fine thing to look at and a
   * poor thing to aim at. A wider invisible disc per zone was tried first and
   * failed the other way: at fit zoom the world is dense enough that discs
   * overlap, the topmost wins, and clicking beside Butcherblock opened Old
   * Guk. Nearest-within-reach is both generous and unambiguous: whatever you
   * are closest to is what you get, up to twelve screen pixels away, and the
   * hover shows you which before you commit.</p>
   */
  const nearestZone = (clientX: number, clientY: number, rect: DOMRect): string | null => {
    if (!drawn) {
      return null;
    }

    const wx = box.x + ((clientX - rect.left) / rect.width) * box.w;
    const wy = box.y + ((clientY - rect.top) / rect.height) * box.h;
    const reach = 12 * unit;
    let best: string | null = null;
    let bestD = reach * reach;
    let bestOnLabel = false;

    for (const z of drawn.graph.zones) {
      const p = positions.get(z.shortName);
      if (!p) {
        continue;
      }

      const d = (p.x - wx) * (p.x - wx) + (p.y - wy) * (p.y - wy);

      // A drawn name is part of its zone: a point inside the name's box goes
      // to that zone whatever dot is nearer, because the name sits above its
      // dot and a hub's name is long enough to reach past its neighbours. The
      // box is estimated from the character count rather than measured — a
      // little wide or narrow is fine, the point is the middle of a name.
      const label = labelOf(z);
      let onLabel = false;
      if (label) {
        const halfW = label.length * nameSize * 0.26;
        const top = p.y - nameSize * (z.shortName === here ? 1.3 : 0.7) - nameSize;
        onLabel = wx >= p.x - halfW && wx <= p.x + halfW && wy >= top && wy <= top + nameSize * 1.2;
      }

      if (onLabel ? !bestOnLabel || d < bestD : !bestOnLabel && d < bestD) {
        bestD = d;
        best = z.shortName;
        bestOnLabel = onLabel;
      }
    }

    return best;
  };

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
            {found && !found.external &&
              ` · ${found.hits.size === 0 ? "nothing matches" : `${found.hits.size} match`} “${search.trim()}”` +
                (found.via.size > 0 ? ` and ${found.via.size} connected` : "")}
            {found && found.external &&
              ` · ${found.hits.size === 0 ? "no drawn zone has" : `${found.hits.size} zone${found.hits.size === 1 ? " has" : "s have"}`} ${litLabel ?? "that mob"}`}
            {/* Said out loud, because a silent marker that never appears reads
                as a bug. If the era filter is what hid it, that is worth
                knowing: the character is standing in a zone the filter says
                does not exist yet. */}
            {currentZone && here === null && ` · you are in ${currentZone}, which is not in the world graph`}
            {currentZone && here !== null && !hereDrawn &&
              ` · you are in ${currentZone}, ${eraLimit === undefined ? "which has no labelled exit and is not drawn" : "which the era filter hides"}`}
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
          {/* Also light what a match connects to. Each such zone says which
              match it is next to and the connecting line is drawn, so nobody
              is left asking why Innothule Swamp lit up for "Feerrott". */}
          <button
            className={"mini-btn" + (withLinks ? " on" : "")}
            onClick={() => setWithLinks((v) => !v)}
            title="Also light the zones connected to a match, with the connection drawn and named"
          >
            connections
          </button>
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
          {hereDrawn && (
            <button
              className="mini-btn"
              onClick={goHere}
              title={`Zoom to ${currentZone}, where the log says you are`}
            >
              here
            </button>
          )}
          {/* Fixed width: the label changes with every wheel notch, and a
              button that grows and shrinks at the header's wrap point
              bounces the whole frame — see the resize note above. */}
          <button
            className="mini-btn map-fit"
            onClick={() => setView(null)}
            disabled={view === null}
            title="Frame the whole world again"
          >
            {zoom > 1.05 ? `fit (${zoom.toFixed(1)}×)` : "fit"}
          </button>
        </div>
      </header>

      {/* Right-click on the world goes back to the zone, as right-click on
          the zone came here — the same gesture in both directions. */}
      <div
        className="map-body"
        ref={wrapRef}
        onContextMenu={(e) => {
          if (onBack) {
            e.preventDefault();
            onBack();
          }
        }}
      >
        <svg
          className="zone-graph"
          viewBox={`${box.x} ${box.y} ${box.w} ${box.h}`}
          preserveAspectRatio="xMidYMid meet"
          style={{ cursor: dragRef.current?.moved ? "grabbing" : hover ? "pointer" : "grab" }}
          // Every click resolves through the one nearest-zone rule, dots
          // included: a dot can sit under another zone's name, and the name
          // is what the person clicked. Nothing opens at the end of a drag.
          onClick={(e) => {
            if (draggedRef.current) {
              return;
            }
            const near = nearestZone(e.clientX, e.clientY, e.currentTarget.getBoundingClientRect());
            if (near) {
              onOpenZone(near);
            }
          }}
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
          // The pointer is captured only once a drag has actually begun — a
          // few pixels of travel — not on the way down. Capturing on the way
          // down retargets the pointer-up at the svg, and a click is delivered
          // to the common ancestor of down and up, so it never reached the
          // zone under the pointer: clicking a zone did nothing. Once a drag
          // is under way capture is what keeps it alive past the edge of the
          // frame, and the click that ends a drag is ignored by the zones.
          onPointerDown={(e) => {
            dragRef.current = { x: e.clientX, y: e.clientY, sx: e.clientX, sy: e.clientY, moved: false };
            draggedRef.current = false;
          }}
          onPointerMove={(e) => {
            const drag = dragRef.current;
            if (!drag) {
              // Not dragging: the hover follows the nearest zone within
              // reach, by the same rule a click uses, so what lights up is
              // what a click would open.
              const near = nearestZone(e.clientX, e.clientY, e.currentTarget.getBoundingClientRect());
              setHover((h) => (h === near ? h : near));
              return;
            }

            if (!drag.moved) {
              if (Math.hypot(e.clientX - drag.sx, e.clientY - drag.sy) < 4) {
                return;
              }
              drag.moved = true;
              draggedRef.current = true;
              e.currentTarget.setPointerCapture(e.pointerId);
            }

            setView({
              ...box,
              x: box.x - (e.clientX - drag.x) * unit,
              y: box.y - (e.clientY - drag.y) * unit,
            });
            drag.x = e.clientX;
            drag.y = e.clientY;
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
            // A search hit's own connections are drawn out — that line is the
            // answer to "why is this neighbour lit". Everything else that
            // touches no hit steps back with the zones it joins. A route stays
            // lit regardless: it was asked for too.
            const link = found !== null && (found.hits.has(e.from) || found.hits.has(e.to));
            const dim = found !== null && !lit && !link;

            return (
              <line
                key={key}
                x1={a.x}
                y1={a.y}
                x2={b.x}
                y2={b.y}
                className={
                  "zone-edge" + (lit ? " on" : "") + (link && !lit ? " link" : "") + (dim ? " dim" : "")
                }
                // Inline, not the strokeWidth attribute: a CSS rule beats a
                // presentation attribute, so the stylesheet's width would win
                // and every line would thicken into a ribbon as you zoom in.
                style={{ strokeWidth: (lit ? 3 : link ? 2 : 1) * unit }}
              />
            );
          })}

          {drawn.graph.zones.map((z) => {
            const p = positions.get(z.shortName);
            if (!p) {
              return null;
            }

            const lit = onRouteNode.has(z.shortName);
            const hit = found?.hits.get(z.shortName);
            const via = found?.via.get(z.shortName);
            const isHere = z.shortName === here;
            const dim = found !== null && hit === undefined && via === undefined && !lit && !isHere;
            const viaNote = viaNoteOf(z);

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
                  (via ? " via" : "") +
                  (isHere ? " here" : "") +
                  (dim ? " dim" : "")
                }
              >
                {/* Sizes are in view units scaled by `unit`, so a dot stays the
                    same size on screen however far in you are — zooming shows
                    more of the world, not a bigger picture of less of it. */}
                {/* Where the log says you are: a ring around the dot, in the
                    accent, drawn under it so the dot keeps its own colour. */}
                {isHere && <circle className="here-ring" r={12 * unit} />}
                <circle className="dot" r={Math.min(7, 2.5 + z.degree * 0.4) * unit} />
                <title>
                  {names.get(z.shortName)} — {z.degree}{" "}
                  {z.degree === 1 ? "connection" : "connections"}
                  {eraNote}
                  {isHere && " · you are here, by the log"}
                  {z.maps.length > 1 && ` · ${z.maps.length} maps: ${z.maps.join(", ")}`}
                  {via && ` · lit because it connects to ${via.map((v) => names.get(v) ?? v).join(", ")}`}
                </title>
                {/* While searching, only hits and their connections are named
                    — a dimmed label is clutter over what you are looking for —
                    and every hit is, however small, because it is what you
                    asked for. A connected zone carries its reason in the
                    label itself. */}
                {labelOf(z) !== null && (
                  <text
                    x={0}
                    y={-nameSize * (isHere ? 1.3 : 0.7)}
                    style={{ fontSize: nameSize, strokeWidth: nameSize / 4 }}
                  >
                    {nameRuns(names.get(z.shortName) ?? z.shortName, hit)}
                    {via && <tspan className="via"> · {viaNote}</tspan>}
                    {isHere && <tspan className="here"> · you are here</tspan>}
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
