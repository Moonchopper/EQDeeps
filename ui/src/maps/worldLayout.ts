import type { ZoneGraph } from "../api";

/**
 * The World view's node layout: a small force simulation, plus a
 * bearing-led seed and an orientation force that turn each edge toward the
 * way its own map's exit label points (F27).
 *
 * <p>Pure and side-effect free on purpose — no React, no fetch, nothing but
 * arithmetic — because `ui/scripts/world-layout-check.mjs` runs this file
 * directly under bare Node (`node --experimental-strip-types`). That means
 * no runtime imports (the `ZoneGraph` import above is `import type`, erased
 * before Node ever sees it) and only TypeScript syntax Node can strip
 * without a compiler: no `enum`, no `namespace`, no parameter properties.
 * Non-null assertions and `as` are fine — they erase too.</p>
 */

export interface Point {
  x: number;
  y: number;
}

// ---- baked-in force constants, ported from the architect's layout.mjs
// prototype ({seed: true, strength: 3, cutoff: 3, gravity: 0.25}) ----

/**
 * How hard an edge's bearing turns it toward the direction its own map says
 * the exit lies, relative to the plain spring force that already holds
 * edges together. Measured on the real corpus (`layout.mjs`'s `score()`, on
 * the classic world): at this strength, exits within 45° of their own map's
 * bearing go 17% → 68%, and edge crossings go 4 → 0.
 */
const ORIENTATION_STRENGTH = 3;

/**
 * Repulsion between two zones is ignored past this many multiples of `k`
 * (the simulation's ideal edge length) apart. Unbounded repulsion is what
 * defeats bearings at the fringe of the world: it shoves leaf chains
 * radially outward regardless of which way their one edge points, undoing
 * the orientation force faster than the iteration budget can win it back.
 */
const REPULSION_CUTOFF = 3;

/**
 * Multiplies the base gravity pull (0.06, unchanged from the original
 * plain-force layout) down to a quarter strength. Components are now laid
 * out separately by `packedLayout`, so gravity no longer has to hold a
 * whole scattered world together on its own — and at full strength it
 * squeezes a world whose repulsion is now local (`REPULSION_CUTOFF`)
 * instead of global.
 */
const GRAVITY_MULTIPLIER = 0.25;

/**
 * A zone this well connected is a portal room, not a place: Plane of
 * Knowledge (37 exits) and Plane of Tranquility (15) place their stones for
 * the room's convenience, not the world's. An edge touching a hub — either
 * end at or above this degree, in the drawn graph — contributes no bearing,
 * in the seed walk or the orientation force. Honouring hub bearings raised
 * full-world edge crossings 307 → 676; ignoring them (this rule) brings it
 * to ~430. The classic world has no zone over 8 exits, so the rule never
 * fires there.
 */
const HUB_DEGREE = 12;

/**
 * The angle (radians) successive un-bearinged siblings fan out by when
 * seeding a zone with no usable direction to its neighbour. The golden
 * angle never repeats a direction as it accumulates, so a hub's unhinted
 * neighbours spread evenly around it instead of stacking on the same ray.
 */
const GOLDEN_ANGLE = 2.399963;

interface IndexedEdge {
  a: number;
  b: number;
  dx?: number;
  dy?: number;
}

/** `IndexedEdge` before the ends missing from this sub-graph are filtered out. */
interface RawEdge {
  a: number | undefined;
  b: number | undefined;
  dx?: number;
  dy?: number;
}

interface Neighbour {
  to: number;
  dx?: number;
  dy?: number;
}

/**
 * Lays the world out with a small force simulation: edges pull, everything
 * pushes apart within reach, and each hinted edge is also nudged to point
 * the way its own map's exit label says it should.
 *
 * <p>Deterministic on purpose — positions start from a breadth-first walk
 * in a fixed order (best-connected zone first, ties and neighbour order
 * broken by shortName) rather than at random or reshuffled per visit, so
 * the same world produces the same picture every time. A layout that
 * reshuffled on each visit would make the map harder to learn, and learning
 * the shape is the point of drawing it.</p>
 *
 * <p>The walk places each newly met zone one `k` along its edge's bearing —
 * for a tree, that <em>is</em> the layout the bearings ask for, so the
 * simulation below only has to settle cycles and collisions rather than
 * discover orientation from scratch.</p>
 */
export function layout(graph: ZoneGraph, iterations = 400): Map<string, Point> {
  const nodes = graph.zones.map((z) => z.shortName);
  const degree = new Map(graph.zones.map((z) => [z.shortName, z.degree]));
  const index = new Map(nodes.map((n, i) => [n, i]));
  const n = nodes.length;

  // The radius scales with the node count so that the *spacing* between
  // zones comes out the same in every component. A fixed radius gives a
  // five-zone pocket the same area as a two-hundred-zone continent, and once
  // those are packed side by side the pocket takes half the frame.
  const radius = Math.max(60, 34 * Math.sqrt(n));
  const area = radius * radius * 4;
  const k = Math.sqrt(area / Math.max(1, n));

  // A hub's bearings are dropped once, here, before either the seed walk or
  // the orientation force below sees them — masking it in one place is what
  // keeps both honouring the same rule (HUB_DEGREE) without repeating it.
  const edges = graph.edges
    .map((e): RawEdge => {
      const hub = (degree.get(e.from) ?? 0) >= HUB_DEGREE || (degree.get(e.to) ?? 0) >= HUB_DEGREE;
      return {
        a: index.get(e.from),
        b: index.get(e.to),
        dx: hub ? undefined : e.dx,
        dy: hub ? undefined : e.dy,
      };
    })
    .filter((e): e is IndexedEdge => e.a !== undefined && e.b !== undefined);

  const pos: (Point | null)[] = new Array(n).fill(null);

  // Breadth-first seed: each newly met zone is placed one `k` along the
  // bearing of the edge that met it. The reverse direction is the negated
  // bearing — an edge pointing east from a to b points west from b to a.
  const nb: Neighbour[][] = nodes.map(() => []);
  for (const e of edges) {
    nb[e.a].push({ to: e.b, dx: e.dx, dy: e.dy });
    nb[e.b].push({
      to: e.a,
      dx: e.dx === undefined ? undefined : -e.dx,
      dy: e.dy === undefined ? undefined : -e.dy,
    });
  }

  // Each component starts at its best-connected zone (ties by shortName),
  // so a hub anchors its own neighbourhood rather than an arbitrary leaf.
  const order = nodes
    .map((_, i) => i)
    .sort(
      (p, q) =>
        (degree.get(nodes[q]) ?? 0) - (degree.get(nodes[p]) ?? 0) || nodes[p].localeCompare(nodes[q]),
    );

  for (const start of order) {
    if (pos[start]) {
      continue;
    }
    pos[start] = { x: 0, y: 0 };
    const queue = [start];
    while (queue.length) {
      const at = queue.shift()!;
      // Neighbours visited in shortName order, so two runs place the same
      // zone the same way even when its neighbours tie on distance.
      const list = nb[at].slice().sort((p, q) => nodes[p.to].localeCompare(nodes[q.to]));
      let fan = 0;
      for (const l of list) {
        if (pos[l.to]) {
          continue;
        }
        const len = l.dx === undefined || l.dy === undefined ? 0 : Math.hypot(l.dx, l.dy);
        let ux: number;
        let uy: number;
        if (len > 0.05) {
          // len > 0.05 only when dx/dy above were both defined.
          ux = l.dx! / len;
          uy = l.dy! / len;
        } else {
          // No usable bearing: fan out by the golden angle so unhinted
          // siblings spread evenly around their parent instead of landing
          // on top of each other.
          const ang = GOLDEN_ANGLE * (fan++ + at);
          ux = Math.cos(ang);
          uy = Math.sin(ang);
        }
        pos[l.to] = { x: pos[at]!.x + ux * k, y: pos[at]!.y + uy * k };
        queue.push(l.to);
      }
    }
  }

  // The walk above starts at every unplaced node in `order`, so by now every
  // index has a position, whatever component (or isolated zone) it is in.
  const placed = pos as Point[];

  let temp = radius / 4;
  const disp: Point[] = placed.map(() => ({ x: 0, y: 0 }));

  for (let step = 0; step < iterations; step++) {
    for (let i = 0; i < n; i++) {
      disp[i].x = 0;
      disp[i].y = 0;
    }

    // Repulsion, range-limited: pairs farther than REPULSION_CUTOFF * k
    // apart do not repel at all, and inside that reach the force fades to
    // nothing at the edge rather than stepping off it.
    const reach = REPULSION_CUTOFF * k;
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        let dx = placed[i].x - placed[j].x;
        let dy = placed[i].y - placed[j].y;
        let d2 = dx * dx + dy * dy;

        if (d2 < 0.01) {
          // Two zones exactly on top of each other have no direction to
          // separate along; nudge them by index so it stays deterministic.
          dx = ((i % 7) - 3) * 0.1;
          dy = ((j % 7) - 3) * 0.1;
          d2 = dx * dx + dy * dy || 0.01;
        }

        const d = Math.sqrt(d2);
        if (d >= reach) {
          continue;
        }
        const fade = 1 - d / reach;
        const force = ((k * k) / d) * fade;
        const fx = (dx / d) * force;
        const fy = (dy / d) * force;

        disp[i].x += fx;
        disp[i].y += fy;
        disp[j].x -= fx;
        disp[j].y -= fy;
      }
    }

    for (const e of edges) {
      const dx = placed[e.a].x - placed[e.b].x;
      const dy = placed[e.a].y - placed[e.b].y;
      const d = Math.sqrt(dx * dx + dy * dy) || 0.01;
      const force = (d * d) / k;
      const fx = (dx / d) * force;
      const fy = (dy / d) * force;

      disp[e.a].x -= fx;
      disp[e.a].y -= fy;
      disp[e.b].x += fx;
      disp[e.b].y += fy;

      // Orientation force: turns the edge toward its bearing without
      // stretching it — the target sits at the bearing's direction, at the
      // edge's *current* length, so the spring above still decides how long
      // the edge is and this only decides which way it points.
      if (e.dx !== undefined && e.dy !== undefined) {
        const w = Math.hypot(e.dx, e.dy);
        if (w > 0.05) {
          const len = Math.max(d, k * 0.5);
          const tx = (e.dx / w) * len;
          const ty = (e.dy / w) * len;
          // (dx, dy) here is pos[a] - pos[b], the *current* b->a vector, so
          // (tx + dx, ty + dy) is target-minus-current for b relative to a.
          // Ported as-is from layout.mjs rather than rederived into a more
          // obviously-named form — proved against the sanity-check score,
          // not by eye.
          const ox = (tx + dx) * ORIENTATION_STRENGTH * w;
          const oy = (ty + dy) * ORIENTATION_STRENGTH * w;
          disp[e.b].x += ox;
          disp[e.b].y += oy;
          disp[e.a].x -= ox;
          disp[e.a].y -= oy;
        }
      }
    }

    // Gravity. Without it nothing bounds repulsion, and a zone with few
    // connections drifts until it is off in a corner on its own — which then
    // sets the viewBox and squashes the entire world into a speck in the
    // middle. Edges alone cannot hold a sparse graph together. Quartered
    // (GRAVITY_MULTIPLIER) now that repulsion is range-limited and
    // components are packed separately — full strength squeezed a world
    // that no longer needs it.
    for (let i = 0; i < n; i++) {
      const pull = 0.06 * GRAVITY_MULTIPLIER * (1 + Math.min(4, degree.get(nodes[i]) ?? 0));
      disp[i].x -= placed[i].x * pull;
      disp[i].y -= placed[i].y * pull;
    }

    for (let i = 0; i < n; i++) {
      const d = Math.sqrt(disp[i].x * disp[i].x + disp[i].y * disp[i].y) || 0.01;
      const limit = Math.min(d, temp);
      placed[i].x += (disp[i].x / d) * limit;
      placed[i].y += (disp[i].y / d) * limit;
    }

    temp *= 0.98;
  }

  return new Map(nodes.map((name, i) => [name, placed[i]]));
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
export function packedLayout(graph: ZoneGraph): Map<string, Point> {
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
