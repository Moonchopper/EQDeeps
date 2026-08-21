// Regenerates the README screenshots in docs/media/.
//
// Everything is captured against the bundled sample log, so the shots stay
// reproducible and carry nobody's real character names.
//
//   1. Build the UI into the server (npm --prefix ui run build) and build the
//      server with the version the rail's foot should show — a plain dev
//      build says v0.1.0:
//
//        dotnet build -c Release -p:Version=0.16.0 src/EQDeeps.Server
//
//      then run it with every store redirected, so the demo's dashboards, pins
//      and learned indexes stay out of your real ones. All of the flags, not
//      just the obvious ones: the stores resolve %AppData% themselves and
//      ignore the environment, and --storeRoot is the one that guards work
//      you can't get back (see CLAUDE.md §3). The server should be this
//      script's alone: it closes any other session it finds there. And
//      --stay-alive, or the server exits a few seconds after the script's
//      browser does, the way it does when you close the last tab.
//
//        $d = "D:\tmp\eqdemo"
//        dotnet run -c Release --no-build --project src/EQDeeps.Server -- `
//          --no-browser --stay-alive --no-update-check --urls http://127.0.0.1:5490 `
//          --storeRoot $d\store --recentLogsRoot $d\recent --sampleLogRoot $d\sample `
//          --updateRoot $d\update --mobRoot $d\mobs --attackRoot $d\attacks `
//          --itemRoot $d\items --referenceRoot $d\reference --cacheRoot $d\cache `
//          --mapRoot "<your EverQuest install>\maps"
//
//      --mapRoot is the one real folder in the list: the Map and World shots
//      draw the maps an install already has, and nothing is written there.
//      The Bestiary shot fetches from the reference site on demand, as the
//      app does.
//
//   2. node scripts/screenshots.mjs http://127.0.0.1:5490 docs/media
//
//      Playwright is resolved out of ui/node_modules (it is a dev dependency
//      there) and drives the Edge that Windows ships, so nothing is
//      downloaded. The script opens the sample itself, waits for the backfill,
//      frames one evening of it, and walks the views.
//
// Shots are 1800px wide: a 1288px viewport rendered at the scale that lands on
// 1800, so what is written is what gets committed — no resize step.
import { createRequire } from "node:module";
import { copyFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";

const require = createRequire(new URL("../ui/package.json", import.meta.url));
const { chromium } = require("playwright");

const base = process.argv[2] ?? "http://127.0.0.1:5490";
const outDir = process.argv[3] ?? ".";
mkdirSync(outDir, { recursive: true });

const VIEWPORT = { width: 1288, height: 820 };
const OUT_WIDTH = 1800;

// The stretch of the sample every framed shot looks at: the afternoon and
// evening of its first day, which is the busiest continuous play it has.
// Chosen by wall clock rather than by row, so a change in how fights are cut
// moves the edges by a pull and not by an hour.
const FRAME_FROM = "2026-08-01T13:30:00";
const FRAME_TO = "2026-08-02T00:10:00";

// ---- the session: open the sample and wait for it to be read ------------

async function api(path, init) {
  const res = await fetch(base + path, init);
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`);
  return res.json();
}

const sample = (await api("/api/logs/discovered")).find((l) => l.source === "sample");
if (!sample) throw new Error("the server offered no sample log — is the resource embedded?");

// The sample is deliberately kept out of the learned indexes — a fixture must
// not teach the app what a server's mobs are worth (SessionManager) — so a
// session over it shows the Bestiary landing and Incoming's learned half
// empty. The shots open a copy of the same file from another path, which the
// app treats as any log: it learns into this run's redirected stores and
// nothing else. Same bytes, same sanitized names, nobody's real character.
const copyDir = join(tmpdir(), "eqdeeps-screenshots");
mkdirSync(copyDir, { recursive: true });
const logPath = join(copyDir, basename(sample.path));
copyFileSync(sample.path, logPath);

// A re-run against a server that already has the copy open reuses that
// session rather than stacking a second one over the same file; anything
// else open is closed, so the header carries one chip.
const open = await api("/api/sessions");
let session =
  open.find((s) => s.path.toLowerCase() === logPath.toLowerCase()) ??
  (await api("/api/sessions", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ path: logPath }),
  }));
for (const other of open) {
  if (other.id !== session.id) await fetch(`${base}/api/sessions/${other.id}`, { method: "DELETE" });
}
while (!session.backfillComplete) {
  await new Promise((r) => setTimeout(r, 500));
  session = await api(`/api/sessions/${session.id}`);
}
// Kills are banked into the learned indexes by a sweep a moment after the
// backfill, not during it; the Bestiary landing and Incoming's lower half
// read from there, so wait for the first kills to land.
for (let i = 0; i < 60 && (await api(`/api/sessions/${session.id}/mobs`)).kills === 0; i++) {
  await new Promise((r) => setTimeout(r, 500));
}
const { fights } = await api(`/api/sessions/${session.id}/fights`);
console.log(`session ${session.id}: ${session.fightCount} fights, ${session.recordCount} records`);

const inFrame = fights.filter((f) => f.beginTime >= FRAME_FROM && f.beginTime <= FRAME_TO);
if (inFrame.length < 2) throw new Error("the sample has no fights in the chosen window");
const oldest = inFrame[0];
const newest = inFrame[inFrame.length - 1];

// ---- the browser ----------------------------------------------------------

const browser = await chromium.launch({ channel: "msedge" });
const page = await browser.newPage({
  viewport: VIEWPORT,
  deviceScaleFactor: OUT_WIDTH / VIEWPORT.width,
  colorScheme: "dark",
});

// Charts default to the last 15m at a 10-bucket window, which over a
// multi-hour frame is a sliver of flat line. An hour smoothed at 30 buckets
// shows the shape of a session without turning into noise.
await page.addInitScript(() => {
  localStorage.setItem(
    "eqdeeps.chartDefaults",
    JSON.stringify({ windowBuckets: 30, spanSec: 3600 }),
  );
});

const settle = (ms = 2500) => page.waitForTimeout(ms);

async function shot(name) {
  await settle();
  await page.screenshot({ path: join(outDir, `${name}.png`) });
  console.log("shot:", name);
}

// The entry is a button around an icon and a label span; match the button by
// the whole of its label, not by `:text-is`, which resolves to the span.
async function railTab(label) {
  await page
    .locator(".nav-rail .rail-tab")
    .filter({ has: page.locator(".rail-label", { hasText: new RegExp(`^${label}$`) }) })
    .click();
  await settle(3500);
}

/**
 * Clicks one fight's row. The list only renders the rows in view, so the
 * row is scrolled to first: its offset is summed from the rows and pull-chain
 * dividers above it in the list's newest-first order, using the heights the
 * list actually rendered at. The row is then found by what it shows — name
 * and begin time, formatted the way the page formats it — and nudged into
 * view if the estimate was a few rows off.
 */
async function clickFight(fight, modifiers = []) {
  const clock = await page.evaluate(
    (iso) => new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" }),
    fight.beginTime,
  );
  const row = page
    .locator(".fight-row")
    .filter({ has: page.locator(".fight-name", { hasText: fight.name }) })
    .filter({ has: page.locator(".fight-meta", { hasText: clock }) })
    .first();

  await page.evaluate(
    ({ fights, id }) => {
      const el = document.querySelector(".fight-scroll");
      const gap = 2; // the slice's column gap, part of every row's stride
      const rowH = (document.querySelector(".fight-row")?.offsetHeight ?? 45) + gap;
      const groupH = (document.querySelector(".fight-group")?.offsetHeight ?? 22) + gap;
      let offset = 0;
      let lastGroup = -1;
      for (let i = fights.length - 1; i >= 0; i--) {
        const f = fights[i];
        if (f.groupIndex !== lastGroup) {
          lastGroup = f.groupIndex;
          offset += groupH;
        }
        if (f.id === id) break;
        offset += rowH;
      }
      el.scrollTop = Math.max(0, offset - 200);
    },
    { fights, id: fight.id },
  );
  await settle(600);

  for (let step = 0; (await row.count()) === 0 && step < 12; step++) {
    // Alternate a little below and a little above the estimate.
    const delta = (step % 2 === 0 ? 1 : -1) * 120 * (Math.floor(step / 2) + 1);
    await page.evaluate((d) => {
      document.querySelector(".fight-scroll").scrollTop += d;
    }, delta);
    await settle(400);
  }
  if ((await row.count()) === 0) throw new Error(`could not bring "${fight.name}" at ${clock} into view`);
  await row.click({ modifiers });
}

await page.goto(base, { waitUntil: "networkidle" });
await settle(4000);
// The app reopens on the view it was last on; the fight list is only there
// on a framed view, so start where the framing can happen.
await railTab("Summary");

// Frame the evening: click the newest fight in the window, then shift-click
// the oldest, the way a person would.
await clickFight(newest);
await settle(1500);
await clickFight(oldest, ["Shift"]);
await settle(6000);

for (const [label, file] of [
  ["Summary", "overview"],
  ["Healing", "healing"],
  ["Tanking", "tanking"],
  ["Experience", "experience"],
  ["Loot", "loot"],
  ["Incoming", "incoming"],
]) {
  await railTab(label);
  await shot(file);
}

// The Map opens on the zone the log last stood in; its first read and the
// World's first build both wait on disk, so give them their own waits.
await railTab("Map");
await page.waitForSelector(".map-body svg, .map-body canvas", { timeout: 60_000 });
await settle(4000);
await shot("map");

await page.locator(".map-mode").getByRole("button", { name: "World", exact: true }).click();
await page.waitForSelector(".zone-graph", { timeout: 120_000 });
await settle(3000);
// Level ranges on every zone — the World's own button. It is on by default
// for a fresh profile, so make sure of it rather than toggle it. The bands
// are fetched the first time they are asked for, and the first ask on a fresh
// reference cache is the slow one, so wait for a label to actually carry one.
const levelsButton = page.locator('.map-controls button[title^="Label every zone with its level"]');
if (!/\bon\b/.test((await levelsButton.getAttribute("class")) ?? "")) await levelsButton.click();
await page.waitForSelector(".zone-graph tspan.lvl", { timeout: 120_000 });
await settle(3000);
await shot("world");

// The Bestiary lands on the mobs this log killed; open the most-killed one so
// the page shows listed beside measured.
await railTab("Bestiary");
await page.waitForSelector(".bestiary-landing .bestiary-row", { timeout: 60_000 });
await page.click(".bestiary-landing .bestiary-row");
await page.waitForSelector(".bestiary-stats, .bestiary-hero, .mob-table", { timeout: 60_000 });
await settle(3000);
await shot("bestiary");

// The custom-dashboard pair: clone a standard view — which is how a user gets
// an editable copy — then open the query builder on one of its panels.
await railTab("Tanking");
await page.getByRole("button", { name: "Customize a copy", exact: true }).click();
await settle(5000);
await shot("dashboard");

await page.hover(".grid-panel");
await page.click('.grid-panel .mini-btn[title="Edit query"]');
await settle(1500);
await shot("query-builder");

// Leave the store as it was found: the clone was only ever for the picture,
// and a second run would otherwise show two of them in the rail. A reload
// drops the editor; the dashboard's own rail entry carries the delete.
await page.goto(base, { waitUntil: "networkidle" });
await settle(3000);
await page
  .locator(".rail-group", { has: page.locator(".rail-heading-text", { hasText: /^Dashboards$/ }) })
  .locator(".rail-tab:not(.add)")
  .first()
  .click();
await settle(1500);
page.once("dialog", (d) => d.accept());
await page.locator(".rail-actions .mini-btn", { hasText: /^delete$/ }).click();
await settle(1500);

await browser.close();
