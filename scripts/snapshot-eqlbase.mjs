// Takes the snapshot of EQLBase's published NPC data that ships with the app (ADR-020 Decision 1,
// as reversed on 2026-09-21; ADR-023 Decision 7) into data/eqlbase/.
//
//   node scripts/snapshot-eqlbase.mjs            refresh the snapshot in place
//   node scripts/snapshot-eqlbase.mjs --dry      say what would be asked for, ask for nothing
//
// This is the ONE place in the repo that reads the whole site, so it is built to cost them as
// little as it can:
//   - one request at a time, with a pause between them — never faster than a person clicking;
//   - every request is CONDITIONAL on the ETag the last snapshot recorded (manifest.json), so a
//     file that has not changed costs them a header, not a download, and a re-run on unchanged
//     data moves no bytes at all;
//   - it says who it is, the way the app does (ReferenceSource.cs).
//
// What is kept is what the app reads and nothing else: every NPC shard, and from the index only
// the NPC ("n") and zone ("z") rows — their items, spells, recipes and quests are not ours to
// carry around for no reason. The manifest records where each file came from and when, so the
// app can tell a cache that is newer than the snapshot from one that is older, and so a Refresh
// inside the app can be conditional too.
//
// It is not part of any build and is never run by CI. Run it by hand, after a game patch, and
// commit what changed.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SOURCE = "https://eqlbase.com";
const PAUSE_MS = 1500;
const OUT = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "data", "eqlbase");
const DRY = process.argv.includes("--dry");
const AGENT = "EQDeeps-snapshot/1 (+https://github.com/Moonchopper/EQDeeps)";

const manifestPath = path.join(OUT, "manifest.json");
const previous = fs.existsSync(manifestPath) ? JSON.parse(fs.readFileSync(manifestPath, "utf8")) : { files: {} };
const files = {};
let asked = 0, changed = 0, unchanged = 0, missing = 0;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** One conditional GET. Returns the body when it changed, null when it did not or is not there. */
async function get(urlPath, name) {
  const known = previous.files?.[name];
  const onDisk = fs.existsSync(path.join(OUT, name));
  if (DRY) { console.log(`would ask for ${urlPath}${known?.etag && onDisk ? ` if changed since ${known.etag}` : ""}`); return null; }
  if (asked++ > 0) await sleep(PAUSE_MS);
  const headers = { "User-Agent": AGENT };
  // Only conditional when the file it would let us keep is really there.
  if (known?.etag && onDisk) headers["If-None-Match"] = known.etag;
  const res = await fetch(SOURCE + urlPath, { headers });
  if (res.status === 304) { unchanged++; files[name] = known; return null; }
  if (res.status === 404) { missing++; return null; }
  if (!res.ok) throw new Error(`${urlPath}: HTTP ${res.status} — stopping rather than writing half a snapshot`);
  const body = await res.text();
  // A 200 whose bytes are what we already hold is not a change. Their validator cannot be
  // relied on for this: measured 2026-09-22, eleven of eighty files came back 200 with identical
  // bytes half an hour after being fetched, and one of them did so even when sent its own
  // current ETag. Bytes are the truth; the ETag is only a hint. (The index is compared after
  // trimming, below, since what is on disk is the trimmed form.)
  const unchangedBytes = onDisk && name !== indexName && fs.readFileSync(path.join(OUT, name), "utf8") === body;
  if (unchangedBytes) { unchanged++; files[name] = { ...known, etag: res.headers.get("etag") ?? known?.etag }; return null; }
  changed++;
  files[name] = { path: urlPath, etag: res.headers.get("etag"), fetchedUtc: new Date().toISOString() };
  return body;
}

fs.mkdirSync(OUT, { recursive: true });

// The index first: it says which shards exist. Trimmed to the rows the app parses.
const indexName = "search-index.json";
const indexBody = await get("/data/search-index.json", indexName);
if (indexBody !== null) {
  const rows = JSON.parse(indexBody).filter((r) => Array.isArray(r) && (r[1] === "n" || r[1] === "z"));
  const trimmed = JSON.stringify(rows);
  const indexPath = path.join(OUT, indexName);
  if (fs.existsSync(indexPath) && fs.readFileSync(indexPath, "utf8") === trimmed) {
    changed--; unchanged++; // the rows the app reads did not move, whatever the rest of their index did
    files[indexName] = { ...previous.files[indexName], etag: files[indexName].etag };
  } else {
    fs.writeFileSync(indexPath, trimmed);
    files[indexName].rows = rows.length;
  }
}
if (DRY && !fs.existsSync(path.join(OUT, indexName))) { console.log("…then one request per shard the index names."); process.exit(0); }

const index = JSON.parse(fs.readFileSync(path.join(OUT, indexName), "utf8"));
const shards = [...new Set(index.filter((r) => r[1] === "n").map((r) => Math.floor(r[2] / 1000)))].sort((a, b) => a - b);

for (const shard of shards) {
  const name = `npcs-${shard}.json`;
  const body = await get(`/data/npcs/${shard}.json`, name);
  if (body !== null) {
    JSON.parse(body); // a shard that is not JSON must not replace one that was
    fs.writeFileSync(path.join(OUT, name), body);
    files[name].bytes = Buffer.byteLength(body);
  }
  if (!DRY) process.stdout.write(`\r${name.padEnd(16)} ${shards.indexOf(shard) + 1}/${shards.length}`);
}
if (DRY) process.exit(0);

// A shard the index no longer names is a zone the site dropped or renumbered: it goes, so the
// snapshot never carries a roster the source has withdrawn.
for (const f of fs.readdirSync(OUT)) {
  if (/^npcs-\d+\.json$/.test(f) && !files[f]) { fs.unlinkSync(path.join(OUT, f)); console.log(`\nremoved ${f} (no longer in the index)`); }
}

const manifest = {
  source: SOURCE,
  // The moment this snapshot speaks for. A cache file written after it is newer than the bundle;
  // one written before it is older. Only moves when something actually changed.
  snapshotUtc: changed > 0 || !previous.snapshotUtc ? new Date().toISOString() : previous.snapshotUtc,
  files: Object.fromEntries(Object.entries(files).sort(([a], [b]) => a.localeCompare(b, "en", { numeric: true }))),
};
fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 1) + "\n");
console.log(`\n${asked} requests: ${changed} changed, ${unchanged} unchanged, ${missing} not there. Snapshot of ${manifest.snapshotUtc}.`);
