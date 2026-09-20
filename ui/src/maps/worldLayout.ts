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
 * full-world edge crossings 307 → 676; masking them (this rule) alone
 * brings it to ~430 — but masking the bearing left a hub's *physics*
 * untouched, which is its own problem: the seed walk still started at
 * Plane of Knowledge (the best-connected zone in any era) and fanned 37
 * zones into a ring before the rest of the world existed, and every edge to
 * it still pulled at full spring strength, folding the Karanas over each
 * other. The two rules below fix that: a hub neither starts nor steers the
 * seed walk (it is placed last, one portal at a time), and its edges pull
 * at a tenth strength (`HUB_SPRING`) once the simulation starts. The
 * classic world has no zone over 8 exits, so none of this ever fires there.
 */
export const HUB_DEGREE = 12;

/**
 * The spring force on an edge touching a hub is multiplied by this before it
 * pulls two zones together. A hub sits at the middle of everything it
 * reaches, so at full strength it draws every zone with a book to it toward
 * one point regardless of where that zone's real neighbours are — this is
 * what folded the Karanas together, not the bearing. Swept on the any-era
 * world with the portals-last seed already in place (confident non-hub
 * edges within 45° of their own bearing, edges worse than 90° off, edge
 * crossings among walkable edges): shipped (spring 1, name-order seed) 59%
 * / 48 / 208; 0.25 → 72% / 25 / 116; **0.1 → 75% / 18 / 89**; 0.03 → 77% /
 * 19 / 97, but the longest edge grows 461 → 520; 0 lets a hub drift off
 * entirely (longest edge 1147) because nothing holds it once it also
 * contributes no bearing. 0.1 is the strength that still tames Lake
 * Rathetear and the Karanas without giving up an edge to get there.
 */
const HUB_SPRING = 0.1;

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
  hub: boolean;
  dx?: number;
  dy?: number;
}

/** `IndexedEdge` before the ends missing from this sub-graph are filtered out. */
interface RawEdge {
  a: number | undefined;
  b: number | undefined;
  hub: boolean;
  dx?: number;
  dy?: number;
}

interface Neighbour {
  to: number;
  hub: boolean;
  dx?: number;
  dy?: number;
}

/**
 * Lays the world out with a small force simulation: edges pull, everything
 * pushes apart within reach, and each hinted edge is also nudged to point
 * the way its own map's exit label says it should.
 *
 * <p>Deterministic on purpose — positions start from a breadth-first walk
 * in a fixed order (best-connected non-hub zone first, ties and neighbour
 * order broken by shortName) rather than at random or reshuffled per visit,
 * so the same world produces the same picture every time. A layout that
 * reshuffled on each visit would make the map harder to learn, and learning
 * the shape is the point of drawing it.</p>
 *
 * <p>The walk places each newly met zone one `k` along its edge's bearing —
 * for a tree, that <em>is</em> the layout the bearings ask for, so the
 * simulation below only has to settle cycles and collisions rather than
 * discover orientation from scratch.</p>
 *
 * <p>Portals are crossed last. A hub (`HUB_DEGREE`) never starts a
 * component and is never walked to while any real exit is still unplaced —
 * once a walk's frontier runs out, it crosses exactly one hub edge and
 * resumes walking from what that reaches, one portal at a time. Without
 * this, the walk starts at whichever zone is best-connected overall, which
 * in any era is the Plane of Knowledge, and fans 37 zones into a ring
 * before the geography around them exists. Once the simulation starts, a
 * hub edge also pulls at a tenth strength (`HUB_SPRING`) rather than full —
 * masking a hub's bearing alone still let its edges yank every neighbour
 * toward one point, which is what folded the Karanas over each other.</p>
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
  // `hub` itself travels with the edge so the seed walk can also give it its
  // own two rules below (crossed last, one at a time) without recomputing
  // degree a second time.
  const edges = graph.edges
    .map((e): RawEdge => {
      const hub = (degree.get(e.from) ?? 0) >= HUB_DEGREE || (degree.get(e.to) ?? 0) >= HUB_DEGREE;
      return {
        a: index.get(e.from),
        b: index.get(e.to),
        hub,
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
    nb[e.a].push({ to: e.b, hub: e.hub, dx: e.dx, dy: e.dy });
    nb[e.b].push({
      to: e.a,
      hub: e.hub,
      dx: e.dx === undefined ? undefined : -e.dx,
      dy: e.dy === undefined ? undefined : -e.dy,
    });
  }

  // Each component starts at its best-connected zone (ties by shortName),
  // so a hub anchors its own neighbourhood rather than an arbitrary leaf —
  // except a hub itself never starts one (see `startOrder` below): the
  // "neighbourhood" a portal room anchors is the whole world, not a place.
  const order = nodes
    .map((_, i) => i)
    .sort(
      (p, q) =>
        (degree.get(nodes[q]) ?? 0) - (degree.get(nodes[p]) ?? 0) || nodes[p].localeCompare(nodes[q]),
    );

  const isHub = (i: number) => (degree.get(nodes[i]) ?? 0) >= HUB_DEGREE;

  // Places `l.to` one `k` from `at`: along its bearing if it has a usable
  // one, otherwise fanned out by the golden angle (see GOLDEN_ANGLE).
  // Shared by the walk below and by the one-hub-edge crossing it makes
  // between a component's non-hub frontier and the next one, so both place
  // a zone by the same rule.
  const place = (at: number, l: Neighbour, fan: number) => {
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
      const ang = GOLDEN_ANGLE * (fan + at);
      ux = Math.cos(ang);
      uy = Math.sin(ang);
    }
    pos[l.to] = { x: pos[at]!.x + ux * k, y: pos[at]!.y + uy * k };
  };

  // Hubs move to the back of the start order, so a component is only ever
  // seeded from one if it has nothing else in it (a hub connected to
  // nothing but other hubs — the classic world has none).
  const startOrder = [...order.filter((i) => !isHub(i)), ...order.filter(isHub)];

  for (const start of startOrder) {
    if (pos[start]) {
      continue;
    }
    pos[start] = { x: 0, y: 0 };
    const queue = [start];
    // Every zone this walk has placed, in placement order — wider than the
    // BFS frontier (`queue`), because the portal crossing below resumes
    // from whichever placed zone reaches one, not necessarily the newest.
    const visited: number[] = [start];
    for (;;) {
      while (queue.length) {
        const at = queue.shift()!;
        // Neighbours visited in shortName order, so two runs place the same
        // zone the same way even when its neighbours tie on distance.
        const list = nb[at].slice().sort((p, q) => nodes[p.to].localeCompare(nodes[q.to]));
        let fan = 0;
        for (const l of list) {
          // A hub edge is left for the crossing below — walking it here
          // would let a hub steer this frontier the same way it always did.
          if (pos[l.to] || l.hub) {
            continue;
          }
          const hinted = l.dx !== undefined && l.dy !== undefined && Math.hypot(l.dx, l.dy) > 0.05;
          place(at, l, hinted ? 0 : fan++);
          queue.push(l.to);
          visited.push(l.to);
        }
      }
      // The walkable (non-hub) frontier is exhausted: cross exactly one
      // portal edge — the first placed zone, in placement order, that still
      // has an unplaced neighbour across a hub edge, breaking ties on the
      // neighbour's shortName — then resume walking from what it reaches.
      // One crossing at a time is what stops a hub from fanning its whole
      // roster into a ring in a single step; a second hub edge waits until
      // this frontier runs out again.
      let crossed = false;
      for (const at of visited) {
        const list = nb[at]
          .filter((l) => l.hub && !pos[l.to])
          .sort((p, q) => nodes[p.to].localeCompare(nodes[q.to]));
        if (list.length) {
          place(at, list[0], 0);
          queue.push(list[0].to);
          visited.push(list[0].to);
          crossed = true;
          break;
        }
      }
      if (!crossed) {
        break;
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
      // A hub edge pulls at a tenth strength (HUB_SPRING) — see the constant's
      // comment for the sweep this came from.
      const force = ((d * d) / k) * (e.hub ? HUB_SPRING : 1);
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
