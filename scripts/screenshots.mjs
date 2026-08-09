// Regenerates the README screenshots in docs/media/.
//
// Everything is captured against the bundled sample log, so the shots stay
// reproducible and carry nobody's real character names.
//
//   1. Run the server against the sample with an APPDATA of its own, so the
//      demo's dashboards stay out of your real ones:
//
//        APPDATA=D:\tmp\eqdemo-appdata dotnet run --project src/EQDeeps.Server \
//          -c Release -- --no-browser --no-update-check --urls http://127.0.0.1:5490
//
//      then POST the log path to /api/sessions and wait for backfill.
//   2. npm i playwright  (uses installed Edge — no browser download)
//      node scripts/screenshots.mjs http://127.0.0.1:5490 docs/media
//
// Images land at 2x and are downscaled to 1800px wide before committing.
import { chromium } from "playwright";
import { mkdirSync } from "fs";
import { join } from "path";

const base = process.argv[2] ?? "http://127.0.0.1:5490";
const outDir = process.argv[3] ?? ".";
mkdirSync(outDir, { recursive: true });

const browser = await chromium.launch({ channel: "msedge" });
const page = await browser.newPage({
  viewport: { width: 1288, height: 820 },
  deviceScaleFactor: 2,
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

async function railTab(label) {
  await page.click(`.rail-tab:text-is("${label}")`);
  await settle(3500);
}

await page.goto(base, { waitUntil: "networkidle" });
await settle(4000);

// Frame the busiest continuous stretch in the sample (the Aug 1 evening) by
// shift-clicking a range off the fight list. Rows are newest first: the first
// nine are the next morning's stragglers, and the evening runs to row 716.
const rows = page.locator(".fight-row");
await rows.nth(9).click();
await settle(1500);
await rows.nth(716).click({ modifiers: ["Shift"] });
await settle(6000);

for (const [label, file] of [
  ["Summary", "overview"],
  ["Healing", "healing"],
  ["Tanking", "tanking"],
  ["Experience", "experience"],
  ["Loot", "loot"],
]) {
  await railTab(label);
  await shot(file);
}

// The custom-dashboard pair: clone a standard view — which is how a user gets
// an editable copy — then open the query builder on one of its panels.
await subTab("Tanking");
await page.click('button:text-is("Customize a copy")');
await settle(5000);
await shot("dashboard");

await page.hover(".grid-panel");
await page.click('.grid-panel .mini-btn[title="Edit query"]');
await settle(1500);
await shot("query-builder");

await browser.close();
