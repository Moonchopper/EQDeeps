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
// Fonts are inlined as data URIs. The stylesheet is injected with setContent,
// so a root-relative @font-face url resolves against about:blank and quietly
// fails — the page would then render in the fallback and every metric-sensitive
// check here (row height above all) would be measuring the wrong typeface while
// reporting green.
const css = readFileSync(cssPath, "utf8").replace(
  /url\("\/fonts\/([^"]+)"\)/g,
  (_, file) => {
    const bytes = readFileSync(join(root, "public/fonts", file));
    return `url("data:font/woff2;base64,${bytes.toString("base64")}")`;
  },
);

const shotsAt = process.argv.indexOf("--shots");
const shotsDir = shotsAt === -1 ? null : process.argv[shotsAt + 1];
if (shotsDir) mkdirSync(shotsDir, { recursive: true });

const rows = (n, f) => Array.from({ length: n }, (_, i) => f(i)).join("");

/** The meter exactly as tableTools.meterStyle hands it over: custom properties
    that a pseudo-element turns into the fill. */
const tint = (color, pct) =>
  `--meter-pct:${pct.toFixed(1)}%;--meter-color:${color};--meter-alpha:0.26`;

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
                <tr class="${i === 0 ? "self-row" : ""}" style="${tint("#e56386", 100 - i * 3)}">
                  <td>Nightreaver ${i}</td>
                  <td class="num">849.9K</td><td class="num">92</td><td class="num">31%</td>
                </tr>`)}
            </tbody>
          </table>
        </div>
      </div>`,
    checks: ["noOverflow", "panelsNotCollapsed", "stickyHeaderOpaque", "rowTintsPaint", "numericColumnsAlign", "densityBudget", "selfRowStandsOut"],
  },
  {
    name: "dense-table-compact",
    // The same table with the opt-in density, so the tighter mode is held to
    // its own ceiling rather than inheriting the comfortable one.
    viewport: { width: 1100, height: 700 },
    density: "compact",
    html: `
      <div class="panel" style="height:640px">
        <div class="panel-title"><span class="panel-name">Damage</span></div>
        <div class="table-scroll">
          <table>
            <thead><tr><th>Name</th><th class="num">Total</th></tr></thead>
            <tbody>${rows(30, (i) => `<tr><td>Nightreaver ${i}</td><td class="num">849.9K</td></tr>`)}</tbody>
          </table>
        </div>
      </div>`,
    checks: ["noOverflow", "densityBudget", "numericColumnsAlign"],
  },
  {
    name: "two-panels",
    // The Incoming shape: a small pane above or below a table holding every
    // mob the server has ever been seen to fight.
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
    name: "controls",
    // Every button and field treatment in the app, side by side. They were
    // written one at a time over eight phases and had drifted into eleven
    // button looks and eight field looks; this fixture is what keeps them
    // agreeing from here on.
    viewport: { width: 900, height: 320 },
    focusStops: 12,
    html: `
      <div class="panel" style="padding:14px;display:flex;flex-direction:column;gap:12px">
        <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
          <button class="btn primary">Open log</button>
          <button class="btn">Cancel</button>
          <button class="mini-btn">edit</button>
          <button class="mini-btn on">live</button>
          <button class="session-add">+</button>
          <button class="range-chip">-6h</button>
          <button class="range-chip on">-1h</button>
          <button class="link-btn">not now</button>
        </div>
        <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
          <input class="search-input" placeholder="Filter" />
          <input class="num-input" value="30" />
          <select class="panel-select"><option>everyone</option></select>
          <span class="time-controls"><select><option>10s</option></select></span>
          <form class="log-open-form" style="margin:0"><input placeholder="C:\EverQuest\Logs\eqlog_Name_server.txt" /></form>
        </div>
      </div>`,
    checks: ["noOverflow", "controlsConsistent"],
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
    // The meter is a pseudo-element sized from --meter-pct. If meterStyle ever
    // emits something the property parser rejects, the width falls back to 0
    // and every bar in every table vanishes with no error and no exception —
    // which is precisely what this check exists to notice.
    const tinted = [...document.querySelectorAll("tbody tr[style*='--meter-pct']")];
    for (const tr of tinted) {
      const cell = tr.querySelector("td:first-child");
      const cs = getComputedStyle(cell);
      const w = parseFloat(getComputedStyle(cell, "::before").width) || 0;
      if (w <= 0) {
        out.push("a row meter computed to zero width — meterStyle emitted a value the parser rejected");
        break;
      }
      // Width alone is not enough, and this check learned that the hard way:
      // the fill sits at z-index -1, so without a stacking context on the cell
      // it paints behind the panel's background and disappears while still
      // measuring correctly. A green check on an invisible bar is worse than
      // no check, so the context is asserted too.
      if (cs.isolation !== "isolate" && cs.zIndex === "auto" && cs.position === "static") {
        out.push("the meter cell establishes no stacking context — the fill will paint behind the panel");
        break;
      }
    }
    return tinted.length === 0 ? ["fixture has no metered rows to check"] : out;
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

  noClippedLabels: () => {
    const out = [];
    for (const e of document.querySelectorAll(".mob-confidence, .fight-tier, .sample-badge")) {
      if (e.scrollWidth > e.clientWidth + 1) out.push(`"${e.textContent}" is clipped by its track`);
    }
    return out;
  },

  controlsConsistent: () => {
    const out = [];
    const fields = [...document.querySelectorAll("input, select")];
    const seen = new Map();
    for (const f of fields) {
      const cs = getComputedStyle(f);
      const key = `${cs.backgroundColor} | ${cs.borderTopColor} | ${cs.borderTopLeftRadius}`;
      seen.set(key, (seen.get(key) ?? 0) + 1);
    }
    // One field treatment. Eight different backgrounds and radii across eight
    // inputs is where "hand-rolled CSS accreted" actually shows.
    if (seen.size > 1) {
      out.push(`fields disagree: ${seen.size} distinct background/border/radius combinations — ${[...seen.keys()].join("  ·  ")}`);
    }
    // Nothing interactive should be shorter than the text inside it plus air.
    for (const el of document.querySelectorAll("button, input, select")) {
      const h = el.getBoundingClientRect().height;
      if (h < 18) out.push(`"${(el.textContent || el.value || el.className).trim().slice(0, 20)}" is only ${Math.round(h)}px tall`);
    }
    return out;
  },

  selfRowStandsOut: () => {
    const self = document.querySelector("tr.self-row td");
    const other = document.querySelector("tr:not(.self-row) td");
    if (!self || !other) return ["fixture has no self row to compare"];
    const a = getComputedStyle(self), b = getComputedStyle(other);
    // Weight, not brightness: promoting a row must not spend contrast, so the
    // colour may differ but the weight is the channel doing the work.
    if (Number(a.fontWeight) <= Number(b.fontWeight)) {
      return [`the monitored character's row is weight ${a.fontWeight}, no heavier than everyone else's ${b.fontWeight}`];
    }
    return [];
  },

  densityBudget: () => {
    const out = [];
    const trs = [...document.querySelectorAll("tbody tr")];
    if (!trs.length) return out;
    const tallest = Math.max(...trs.map((r) => r.getBoundingClientRect().height));
    const compact = document.documentElement.dataset.density === "compact";
    const ceiling = compact ? 28 : 32;
    // 15-40 rows on screen is non-negotiable per the brief, so row height is
    // the number a typography or density change must not quietly cross.
    //
    // Today a row measures exactly 30.0px at 13px/1.45 with 4px cell padding.
    // The budget is 32, and the 2px is a deliberate allowance rather than
    // slack: a bundled face with a larger x-height than Segoe UI may need a
    // hair more leading to stay comfortable. Spending more than that is a
    // density decision, and it should be argued for rather than discovered.
    if (tallest > ceiling) {
      out.push(`table row height ${tallest.toFixed(1)}px exceeds the ${ceiling}px budget for ${compact ? "compact" : "comfortable"}`);
    }
    return out;
  },
};

const browser = await chromium.launch({ channel: "msedge" });
let failed = 0;

for (const fx of FIXTURES) {
  const page = await browser.newPage({ viewport: fx.viewport, colorScheme: "dark" });
  await page.setContent(
    `<!doctype html><html data-density="${fx.density ?? "comfortable"}"><head><meta charset="utf-8">` +
      `<style>${css}</style></head><body>${fx.html}</body></html>`,
    { waitUntil: "load" },
  );
  await page.waitForTimeout(120);

  // Guard against the harness measuring a fallback face and reporting green.
  // NOT document.fonts.check(): that answers "could this family be resolved",
  // which is true for any system-installed name and so never fires. Ask the
  // FontFaceSet whether the @font-face rule itself produced a loaded face.
  await page.evaluate(() => document.fonts.ready);
  const bundled = await page.evaluate(() =>
    [...document.fonts].filter((f) => f.status === "loaded").map((f) => f.family),
  );
  const failures = bundled.length
    ? []
    : ["font: no @font-face loaded — every metric below is a fallback typeface"];
  for (const name of fx.checks) {
    const found = await page.evaluate(`(${CHECKS[name].toString()})()`);
    failures.push(...found.map((f) => `${name}: ${f}`));
  }

  if (fx.focusStops) {
    // :focus-visible only engages for keyboard interaction, so the focus has to
    // arrive by Tab rather than by element.focus().
    await page.evaluate(() => document.body.insertAdjacentHTML("afterbegin", '<a href="#" id="__seed">seed</a>'));
    await page.focus("#__seed");
    for (let i = 0; i < fx.focusStops; i++) {
      await page.keyboard.press("Tab");
      const stop = await page.evaluate(() => {
        const el = document.activeElement;
        if (!el || el === document.body) return null;
        const cs = getComputedStyle(el);
        // outline-width computes independently of outline-style, so `outline:
        // none` still reports a width. Style is the property that decides
        // whether anything is actually drawn.
        const ring = cs.outlineStyle === "none" ? 0 : parseFloat(cs.outlineWidth) || 0;
        const shadow = cs.boxShadow !== "none";
        return { tag: el.tagName.toLowerCase(), cls: el.className, ring, shadow };
      });
      if (stop && stop.ring === 0 && !stop.shadow) {
        failures.push(`focus: <${stop.tag} class="${stop.cls}"> shows no focus indicator when tabbed to`);
      }
    }
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
