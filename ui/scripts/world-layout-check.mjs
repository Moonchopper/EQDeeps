// Invariant checks for the World view's bearing-led layout (F27, worldLayout.ts).
//
//   npm --prefix ui run test:world
//   node --experimental-strip-types scripts/world-layout-check.mjs
//
// No browser, no server, no sample log: the layout is pure arithmetic, so
// this runs worldLayout.ts directly under bare Node's type stripping against
// hand-built fixture graphs — about as fast as ui/scripts/layout-check.mjs's
// CSS fixtures, for the same reason.
//
// Why invariants rather than golden positions: a recorded-output test would
// break on every deliberate retune of ORIENTATION_STRENGTH, REPULSION_CUTOFF
// or HUB_DEGREE and prove nothing about whether the new numbers are any
// good — the same "golden file recorded from the code under test cannot
// catch a drifting formula" reasoning CLAUDE.md §8 gives for the query-engine
// tests. What actually has to hold survives a retune: a hinted edge ends up
// near its bearing, two runs of the same graph agree, nothing lands on top
// of anything else, and a hub's spokes ignore their own bearings. Those are
// the five checks below.
//
// None of the five pins the *value* of a constant, and invariant 1 in
// particular is weaker than it looks: on the small tree below, the seed walk
// alone (bearings baked into the starting positions) or the orientation
// force alone (400 iterations to correct a bad start) is already enough to
// satisfy it — confirmed by zeroing ORIENTATION_STRENGTH with the BFS seed
// left in place, and separately by reverting only the seed to the old
// name-order circle with the force left at full strength; neither alone
// turned this fixture red. Only reverting both at once (the genuine
// pre-feature behaviour) does. What invariant 1 actually guards is that
// bearings are wired up at all and pointing the right way, not that
// ORIENTATION_STRENGTH is tuned well. The constant's value is validated
// separately, against the real classic-world corpus (68% of sides within
// 45°, 0 edge crossings) — see the ADR and domain doc, not a unit check.
import { layout, packedLayout } from "../src/maps/worldLayout.ts";

const angleBetween = (ax, ay, bx, by) => {
  const cos = (ax * bx + ay * by) / ((Math.hypot(ax, ay) || 1) * (Math.hypot(bx, by) || 1));
  return (Math.acos(Math.max(-1, Math.min(1, cos))) * 180) / Math.PI;
};

const zone = (shortName, degree) => ({ shortName, degree });
// `eras: []` mirrors the real ZoneGraph shape; layout()/packedLayout() never
// read it, so it costs nothing and keeps these fixtures honest about what
// they stand in for.
const graph = (zones, edges) => ({ zones, edges, eras: [] });

const noTwoCloserThan10 = (pos) => {
  const failures = [];
  const pts = [...pos.entries()];
  for (const [name, p] of pts) {
    if (!Number.isFinite(p.x) || !Number.isFinite(p.y)) {
      failures.push(`${name} is not finite: ${JSON.stringify(p)}`);
    }
  }
  for (let i = 0; i < pts.length; i++) {
    for (let j = i + 1; j < pts.length; j++) {
      const d = Math.hypot(pts[i][1].x - pts[j][1].x, pts[i][1].y - pts[j][1].y);
      if (d < 10) {
        failures.push(`${pts[i][0]} and ${pts[j][0]} are only ${d.toFixed(1)} units apart`);
      }
    }
  }
  return failures;
};

let failed = 0;
const report = (name, failures) => {
  if (failures.length) {
    failed += failures.length;
    console.log(`FAIL  ${name}`);
    for (const f of failures) console.log(`        ${f}`);
  } else {
    console.log(`ok    ${name}`);
  }
};

// 1. A compass rose — a hub with N/E/S/W arms, one of them a three-zone
// chain — is a tree the bearings fully determine. Every hinted edge should
// end up close to its own bearing once the seed walk (which places a tree
// exactly on its bearings) and the orientation force have had their say.
// Guards that bearings are used at all, not the strength of the orientation
// force: on a tree this small either mechanism alone already satisfies it
// (see the header comment), so this fixture would not catch
// ORIENTATION_STRENGTH drifting to a weaker-but-nonzero value.
{
  const g = graph(
    [
      zone("hub", 4),
      zone("north", 1),
      zone("east1", 2),
      zone("east2", 2),
      zone("east3", 1),
      zone("south", 1),
      zone("west", 1),
    ],
    [
      { from: "hub", to: "north", dx: 0, dy: -1 },
      { from: "hub", to: "east1", dx: 1, dy: 0 },
      { from: "hub", to: "south", dx: 0, dy: 1 },
      { from: "hub", to: "west", dx: -1, dy: 0 },
      { from: "east1", to: "east2", dx: 1, dy: 0 },
      { from: "east2", to: "east3", dx: 1, dy: 0 },
    ],
  );
  const pos = layout(g, 400);
  const failures = [];
  for (const e of g.edges) {
    const a = pos.get(e.from);
    const b = pos.get(e.to);
    const ang = angleBetween(b.x - a.x, b.y - a.y, e.dx, e.dy);
    if (ang > 45) {
      failures.push(`${e.from}->${e.to} ended up ${ang.toFixed(1)}° off its bearing`);
    }
  }
  report("compass rose: every hinted edge within 45° of its bearing", failures);
}

// 2. Determinism: nothing in the simulation may read Math.random, the clock,
// or an iteration order that varies between calls, so the same graph laid
// out twice — including an unhinted edge, which exercises the golden-angle
// fallback — must give back the same numbers both times.
{
  const zones = [zone("a", 3), zone("b", 2), zone("c", 2), zone("d", 1), zone("e", 1)];
  const edges = [
    { from: "a", to: "b", dx: 1, dy: 0 },
    { from: "b", to: "c", dx: 0, dy: 1 },
    { from: "a", to: "d", dx: -1, dy: 0 },
    { from: "c", to: "e" },
  ];
  const pos1 = layout(graph(zones.map((z) => ({ ...z })), edges.map((e) => ({ ...e }))), 200);
  const pos2 = layout(graph(zones.map((z) => ({ ...z })), edges.map((e) => ({ ...e }))), 200);
  const failures = [];
  for (const [name, p1] of pos1) {
    const p2 = pos2.get(name);
    if (!p2 || p1.x !== p2.x || p1.y !== p2.y) {
      failures.push(`${name} moved between runs: ${JSON.stringify(p1)} vs ${JSON.stringify(p2)}`);
    }
  }
  report("two runs give identical positions", failures);
}

// 3. Finite and separated: every position is a real number, and the
// coincident-node nudge actually keeps zones apart even when several of
// them share one parent with no bearing to spread them (the golden-angle
// fallback's job).
{
  const zones = Array.from({ length: 10 }, (_, i) => zone(`z${i}`, i === 0 ? 9 : 1));
  const edges = Array.from({ length: 9 }, (_, i) => ({ from: "z0", to: `z${i + 1}` }));
  const pos = packedLayout(graph(zones, edges));
  report("every position finite, no two zones closer than 10 units", noTwoCloserThan10(pos));
}

// 4. The hub rule: a zone at or above HUB_DEGREE ignores its own edges'
// bearings entirely — laying the same hub-and-spokes graph out with real
// bearings on every spoke and with none at all must land every zone in
// exactly the same place, because worldLayout.ts masks a hub's edges before
// either the seed walk or the orientation force sees them.
{
  const spokeCount = 12;
  const hub = zone("hub", spokeCount);
  const spokes = Array.from({ length: spokeCount }, (_, i) => zone(`spoke${i}`, 1));
  const angleOf = (i) => (2 * Math.PI * i) / spokeCount;
  const edgesWithBearings = spokes.map((z, i) => ({
    from: "hub",
    to: z.shortName,
    dx: Math.cos(angleOf(i)),
    dy: Math.sin(angleOf(i)),
  }));
  const edgesWithout = spokes.map((z) => ({ from: "hub", to: z.shortName }));
  const posWith = layout(graph([hub, ...spokes], edgesWithBearings), 400);
  const posWithout = layout(graph([hub, ...spokes], edgesWithout), 400);
  const failures = [];
  for (const [name, p1] of posWith) {
    const p2 = posWithout.get(name);
    if (!p2 || p1.x !== p2.x || p1.y !== p2.y) {
      failures.push(`${name} differs with vs without spoke bearings: ${JSON.stringify(p1)} vs ${JSON.stringify(p2)}`);
    }
  }
  report(`a hub of degree ${spokeCount} lays out identically with and without bearings on its edges`, failures);
}

// 5. A graph with no bearings anywhere — the shape the API sends before any
// map has placed an exit, or a graph restored from a cache written before
// this feature — must still lay out: finite positions, nothing overlapping.
{
  const zones = [zone("q1", 2), zone("q2", 3), zone("q3", 2), zone("q4", 1), zone("q5", 2)];
  const edges = [
    { from: "q1", to: "q2" },
    { from: "q2", to: "q3" },
    { from: "q2", to: "q4" },
    { from: "q3", to: "q5" },
  ];
  const pos = packedLayout(graph(zones, edges));
  report("a graph with no bearings at all still lays out (finite, separated)", noTwoCloserThan10(pos));
}

console.log(failed ? `\n${failed} world-layout failure(s)` : "\nall world-layout checks passed");
process.exit(failed ? 1 : 0);
