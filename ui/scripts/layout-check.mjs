// Geometry regression checks for the dense surfaces, run against the real
// stylesheet in a real browser.
//
//   npm --prefix ui run test:layout
//   node ui/scripts/layout-check.mjs --shots artifacts/layout   (writes PNGs too)
//
// Why this exists, and why it asserts geometry rather than diffing pixels:
// three layout faults shipped undetected because nothing ever measured the
// rendered output. A panel on Incoming was 17px tall, the same panel on Mobs
// was 4px, and the tier ladder's numeric columns walked 23px sideways down the
// ladder. All three were invisible in source, obvious in one render, and all
// three predated the re-theme that made them noticeable.
//
// A pixel diff would have caught them too, and would then have failed on every
// deliberate colour change for the rest of the project. These checks encode
// INVARIANTS instead — "a panel is never shorter than its own title bar", "a
// column of numbers shares a right edge" — so they stay quiet while the app is
// restyled and speak up when it breaks. Each one is a bug that actually
// happened, plus the density budget that the typography pass has to respect.
//
// It lives under ui/ rather than scripts/ because that is where its one
// dependency is installed; scripts/screenshots.mjs stays at the root because it
// drives the real app and installs playwright ad hoc.
//
// Fixtures are hand-built markup using the app's own class names rather than
// the running app: no server, no sample log, no session state, runs in about
// two seconds. The cost is that they can drift from the components — when a
// surface is restructured, its fixture needs the same edit.
import { chromium } from "playwright";
import { readFileSync, mkdirSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
// --css lets the harness be pointed at another revision of the stylesheet,
// which is how these checks were confirmed to actually fail: every one of them
// was run against the pre-fix file and watched to go red before being trusted.
const cssAt = process.argv.indexOf("--css");
const cssPath = cssAt === -1 ? join(root, "src/styles.css") : process.argv[cssAt + 1];
const css = readFileSync(cssPath, "utf8");

const shotsAt = process.argv.indexOf("--shots");
const shotsDir = shotsAt === -1 ? null : process.argv[shotsAt + 1];
if (shotsDir) mkdirSync(shotsDir, { recursive: true });

const rows = (n, f) => Array.from({ length: n }, (_, i) => f(i)).join("");

/** A row tint exactly as tableTools.meterStyle builds it, alpha included. */
const tint = (color, pct) =>
  `background: linear-gradient(to right, ${color}24 ${pct.toFixed(1)}%, transparent ${pct.toFixed(1)}%)`;

const FIXTURES = [
  {
    name: "dense-table",
    // 30 rows at production density is the stated floor in the brief; if a
    // typography change pushes the row height up, this is where it shows.
    viewport: { width: 1100, height: 700 },
    html: `
      <div class="panel" style="height:640px">
        <div class="panel-title"><span class="panel-name">Damage</span></div>
        <div class="table-scroll">
          <table>
            <thead><tr><th>Name</th><th class="num">Total</th><th class="num">DPS</th><th class="num">Crit</th></tr></thead>
            <tbody>
              ${rows(30, (i) => `
                <tr style="${tint("#e56386", 100 - i * 3)}">
                  <td>Nightreaver ${i}</td>
                  <td class="num">849.9K</td><td class="num">92</td><td class="num">31%</td>
                </tr>`)}
            </tbody>
          </table>
        </div>
      </div>`,
    checks: ["noOverflow", "panelsNotCollapsed", "stickyHeaderOpaque", "rowTintsPaint", "numericColumnsAlign", "densityBudget"],
  },
  {
    name: "tier-ladder",
    viewport: { width: 1100, height: 400 },
    html: `
      <div class="mob-ladder">
        ${[["open world", "616", "baseline", "high"],
           ["1 · Awakened", "708", "×1.15", "high"],
           ["2 · Adaptive", "790", "×1.28", "medium"],
           ["3 · Fused", "805", "×1.31", "low"],
           ["4 · Refined", "1.5K", "×2.42", "medium"]]
          .map(([t, h, m, c], i) => `
            <div class="mob-rung" style="${tint("#03a8ba", 40 + i * 12)}">
              <span class="mob-rung-tier">${t}</span>
              <span class="mob-rung-health">${h}</span>
              <span class="mob-rung-mult subtle">${m}</span>
              <span class="mob-confidence ${c}">${c}</span>
            </div>`).join("")}
      </div>`,
    checks: ["noOverflow", "ladderColumnsAlign", "noClippedLabels"],
  },
  {
    name: "two-panels",
    // The Incoming and Mobs shape: a small pane above or below a table holding
    // every mob the server has ever been seen to fight.
    viewport: { width: 1100, height: 700 },
    html: `
      <div style="height:680px;display:flex;flex-direction:column;min-height:0">
        <div class="dashboard-main" style="flex:1;min-height:0">
          <div class="panel hit-feed-panel">
            <div class="panel-title"><span class="panel-name">Recent hits</span></div>
            <div class="mob-scroll"><table class="mob-table">
              <tbody>${rows(40, () => `<tr><td>A fetid fiend</td><td class="num">44</td></tr>`)}</tbody>
            </table></div>
          </div>
          <div class="panel">
            <div class="panel-title"><span class="panel-name">What they hit for</span></div>
            <div class="mob-scroll"><table class="mob-table">
              <tbody>${rows(1890, () => `<tr><td>A fetid fiend</td><td class="num">44</td></tr>`)}</tbody>
            </table></div>
          </div>
        </div>
      </div>`,
    checks: ["noOverflow", "panelsNotCollapsed"],
  },
  {
    name: "live-meter",
    viewport: { width: 480, height: 240 },
    html: `
      <div class="panel">
        <div class="panel-title"><span class="panel-name">Live meter</span></div>
        <div class="meter-rows">
          ${[["Nightreaver", "1,204", 100], ["Glubbug", "856", 71], ["Yakesh", "459", 38]]
            .map(([n, v, w]) => `
              <div class="meter-row">
                <span class="meter-bar" style="width:${w}%;background:#e56386"></span>
                <span class="meter-name">${n}</span><span class="meter-nums">${v}</span>
              </div>`).join("")}
        </div>
      </div>`,
    checks: ["noOverflow", "panelsNotCollapsed"],
  },
];

/* Each check returns an array of human-readable failures. They run in the page
   so they read computed style and real geometry, not the source. */
const CHECKS = {
  noOverflow: () => {
    const d = document.documentElement;
    return d.scrollWidth > d.clientWidth + 1
      ? [`page scrolls horizontally: ${d.scrollWidth}px of content in ${d.clientWidth}px`]
      : [];
  },

  panelsNotCollapsed: () => {
    const out = [];
    for (const p of document.querySelectorAll(".panel")) {
      const h = p.getBoundingClientRect().height;
      // A panel is at minimum its own title bar. Below that it is not "small",
      // it is a rendering fault the user will report as one.
      if (h < 28) {
        const name = p.querySelector(".panel-name")?.textContent ?? "(unnamed)";
        out.push(`panel "${name}" collapsed to ${Math.round(h)}px`);
      }
    }
    return out;
  },

  stickyHeaderOpaque: () => {
    const out = [];
    for (const th of document.querySelectorAll("th")) {
      const bg = getComputedStyle(th).backgroundColor;
      const m = bg.match(/rgba?\(([^)]+)\)/);
      const alpha = m ? Number(m[1].split(",")[3] ?? 1) : 1;
      // Rows scroll under a sticky header; anything translucent shows them.
      if (alpha < 1) out.push(`sticky th is translucent (${bg})`);
    }
    return out.slice(0, 1);
  },

  rowTintsPaint: () => {
    const out = [];
    const tinted = [...document.querySelectorAll("tbody tr[style*='gradient']")];
    for (const tr of tinted) {
      // meterStyle concatenates hex + alpha as a string. An invalid stop
      // resolves to `none` and every bar in the app vanishes with no error.
      if (getComputedStyle(tr).backgroundImage === "none") {
        out.push("a row tint resolved to no gradient — meterStyle produced an invalid colour");
        break;
      }
    }
    return tinted.length === 0 ? ["fixture has no tinted rows to check"] : out;
  },

  numericColumnsAlign: () => {
    const out = [];
    const bodyRows = [...document.querySelectorAll("tbody tr")];
    if (bodyRows.length < 2) return out;
    const cols = bodyRows[0].querySelectorAll("td.num").length;
    for (let c = 0; c < cols; c++) {
      const edges = bodyRows.map((r) => {
        const cell = r.querySelectorAll("td.num")[c];
        const b = cell.getBoundingClientRect();
        return Math.round(b.right);
      });
      const spread = Math.max(...edges) - Math.min(...edges);
      if (spread > 1) out.push(`numeric column ${c + 1} right edges vary by ${spread}px`);
    }
    return out;
  },

  ladderColumnsAlign: () => {
    const out = [];
    for (const ladder of document.querySelectorAll(".mob-ladder")) {
      for (const cls of ["mob-rung-health", "mob-rung-mult"]) {
        const edges = [...ladder.querySelectorAll("." + cls)].map((e) =>
          Math.round(e.getBoundingClientRect().right),
        );
        if (edges.length < 2) continue;
        const spread = Math.max(...edges) - Math.min(...edges);
        // Each rung is its own grid; a content-sized track makes the columns
        // walk sideways with whichever word that row happens to carry.
        if (spread > 1) out.push(`.${cls} right edges vary by ${spread}px down the ladder`);
      }
    }
    return out;
  },

  noClippedLabels: () => {
    const out = [];
    for (const e of document.querySelectorAll(".mob-confidence, .fight-tier, .sample-badge")) {
      if (e.scrollWidth > e.clientWidth + 1) out.push(`"${e.textContent}" is clipped by its track`);
    }
    return out;
  },

  densityBudget: () => {
    const out = [];
    const trs = [...document.querySelectorAll("tbody tr")];
    if (!trs.length) return out;
    const tallest = Math.max(...trs.map((r) => r.getBoundingClientRect().height));
    // 15-40 rows on screen is non-negotiable per the brief, so row height is
    // the number a typography or density change must not quietly cross.
    //
    // Today a row measures exactly 30.0px at 13px/1.45 with 4px cell padding.
    // The budget is 32, and the 2px is a deliberate allowance rather than
    // slack: a bundled face with a larger x-height than Segoe UI may need a
    // hair more leading to stay comfortable. Spending more than that is a
    // density decision, and it should be argued for rather than discovered.
    if (tallest > 32) out.push(`table row height ${tallest.toFixed(1)}px exceeds the 32px density budget (was 30.0 before the re-theme)`);
    return out;
  },
};

const browser = await chromium.launch({ channel: "msedge" });
let failed = 0;

for (const fx of FIXTURES) {
  const page = await browser.newPage({ viewport: fx.viewport, colorScheme: "dark" });
  await page.setContent(
    `<!doctype html><html><head><meta charset="utf-8"><style>${css}</style></head><body>${fx.html}</body></html>`,
    { waitUntil: "load" },
  );
  await page.waitForTimeout(120);

  const failures = [];
  for (const name of fx.checks) {
    const found = await page.evaluate(`(${CHECKS[name].toString()})()`);
    failures.push(...found.map((f) => `${name}: ${f}`));
  }

  if (shotsDir) await page.screenshot({ path: join(shotsDir, `${fx.name}.png`), fullPage: false });

  if (failures.length) {
    failed += failures.length;
    console.log(`FAIL  ${fx.name}`);
    for (const f of failures) console.log(`        ${f}`);
  } else {
    console.log(`ok    ${fx.name}  (${fx.checks.length} checks)`);
  }
  await page.close();
}

await browser.close();
console.log(failed ? `\n${failed} layout failure(s)` : "\nall layout checks passed");
process.exit(failed ? 1 : 0);
