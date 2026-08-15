// Derives the `era` and `eraSource` columns of src/EQDeeps.Core/Maps/zones.tsv
// from the zone ids in an EverQuest client's Resources/ZoneNames.txt (F27,
// docs/domain/eq-map-format.md §5.3, GitHub issue #57).
//
//   node scripts/derive-zone-eras.mjs                 rewrite the table's era columns
//   node scripts/derive-zone-eras.mjs --check         exit 1 if the table would change
//   node scripts/derive-zone-eras.mjs --eq "D:\EQ"    read ZoneNames.txt from that install
//
// The install is otherwise taken from EQDEEPS_EQ or the usual Daybreak paths.
//
// WHY THIS IS A SCRIPT AND NOT RUNTIME CODE. Nothing in a log or a map file says
// what expansion a server has unlocked, so the era filter is chosen by the
// player, never inferred (issue #57). What the client *does* carry is a zone id
// per display name, and those ids were handed out in blocks as expansions
// shipped — classic in the low numbers, Kunark next, and so on. That is folklore
// rather than documentation, so it is validated here against the file itself
// (every band below quotes the names at its edges) and the result is checked in
// as data, so the app never depends on the player's ZoneNames.txt and the
// derivation can be re-run and argued with.
//
// WHAT AN ERA MEANS. The era of a row is the *earliest* expansion the place can
// exist in, and the World view hides a zone whose era is later than the one
// chosen. A display name that has several ids (revamps and event copies keep
// the name: "The Ocean of Tears" is 69, 409 and 569) therefore takes its
// LOWEST id — the place has been there since then, whichever drawing you have.
// A row the bands cannot place is left blank, and a blank era is always shown:
// a smaller truthful graph beats an invented one, and hiding a zone that is
// really there is the worse mistake.

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const TABLE = resolve(here, "..", "src", "EQDeeps.Core", "Maps", "zones.tsv");

/**
 * Zone-id bands, validated against ZoneNames.txt (699 rows in the install this
 * was derived from). `evidence` names the rows at the edges so a future reader
 * can check their own file still agrees. Anything not covered — event and
 * Hardcore Heritage copies (502–699), seasonal zones (837–842, 866–869), system
 * zones (183–199), and content newer than the vocabulary (872+) — is left blank.
 *
 * Bands are inclusive. Where a band knowingly holds more than one expansion
 * (786–816) the era given is the earliest one in it, which keeps it an honest
 * lower bound.
 */
const BANDS = [
  { from: 1, to: 77, era: "classic", evidence: "South Qeynos (1) … The Arena (77)" },
  { from: 78, to: 109, era: "kunark", evidence: "The Field of Bone (78) … Veksar (109); see overrides for the launch zones filed here" },
  { from: 110, to: 130, era: "velious", evidence: "The Iceclad Ocean (110) … The Marauders Mire (130)" },
  { from: 150, to: 182, era: "luclin", evidence: "Shadow Haven (150) … The Akheva Ruins (179); Arenatwo, Jaggedpine Forest and Nedaria's Landing fill 180–182" },
  { from: 200, to: 223, era: "pop", evidence: "Ruins of Lxanvom (200, the Crypt of Decay) … The Prison of the Forsaken (223)" },
  { from: 224, to: 228, era: "loy", evidence: "The Gulf of Gunthak (224) … Hate's Fury (228)" },
  { from: 229, to: 277, era: "ldon", evidence: "Deepest Guk: Cauldron of Lost Souls (229) … Chardok: The Halls of Betrayal (277)" },
  { from: 278, to: 299, era: "god", evidence: "The Caverns of Exile (278) … Qvic, Prayer Grounds of Calling (299)" },
  { from: 300, to: 336, era: "oow", evidence: "Wall of Slaughter (300) … The Ruined City of Dranik (336)" },
  { from: 337, to: 346, era: "don", evidence: "The Broodlands (337) … The Accursed Nest (343); Guild Lobby, Guild Hall, The Bartering Quarter (344–346)" },
  { from: 347, to: 368, era: "dodh", evidence: "Ruins of Illsalin (347) … Shadowed Grove (368)" },
  { from: 369, to: 393, era: "por", evidence: "Arcstone, Isle of Spirits (369) … Deathknell (381); the Freeport revamp (382–391) and North/South Desert of Ro (392–393)" },
  { from: 394, to: 415, era: "tss", evidence: "Crescent Reach (394) … Ashengate (406); revamps of nine older zones (407–415), of which only The Commonlands (408) is a new name" },
  { from: 416, to: 435, era: "tbs", evidence: "Katta Castrum (416) … The Open Sea (431–435)" },
  { from: 436, to: 451, era: "sof", evidence: "Fortress Mechanotus (436) … Deepscar's Den (451)" },
  { from: 452, to: 479, era: "sod", evidence: "Field of Scale (452) … Ngreth's Den (479)" },
  { from: 480, to: 495, era: "uf", evidence: "Brell's Rest (480) … Lair of the Fallen (495)" },
  { from: 700, to: 723, era: "hot", evidence: "The Feerrott (700, revamp) … Hermit's Hideaway Interior (723)" },
  { from: 724, to: 751, era: "voa", evidence: "Argath, Bastion of Illdaera (724) … Modest Guild Hall (751)" },
  { from: 752, to: 769, era: "rof", evidence: "Shard's Landing (752) … Heart of Fear: The Epicenter (769)" },
  { from: 770, to: 776, era: "cotf", evidence: "Bixie Warfront (770) … Argin-Hiz (776)" },
  { from: 777, to: 777, era: "tbm", evidence: "Sul Vius: Demiplane of Life, filed ahead of the Darkened Sea block" },
  { from: 778, to: 785, era: "tds", evidence: "Arx Mentis (778) … Tempest Temple (785)" },
  {
    from: 786, to: 816, era: "tbm",
    evidence: "interleaved: The Broken Mirror (796–798), Empires of Kunark (788, 790–791, 793–795, 799–800), Ring of Scale (789, 792, 813–816), plus anniversary and Hardcore Heritage revamps; nothing here predates The Broken Mirror, so that is the bound",
  },
  { from: 817, to: 823, era: "tbl", evidence: "Plane of Smoke (817) … Chamber of Tears (823)" },
  { from: 824, to: 830, era: "tov", evidence: "The Eastern Wastes (824) … Crystal Caverns (830)" },
  { from: 831, to: 836, era: "cov", evidence: "The Sleeper's Tomb (831) … The Temple of Veeshan (836)" },
  { from: 843, to: 848, era: "tol", evidence: "Maiden's Eye (843) … Basilica of Adumbration (848)" },
  { from: 849, to: 856, era: "nos", evidence: "Bloodfalls (849) … Deepshade (856)" },
  { from: 857, to: 863, era: "ls", evidence: "Firefall Pass (857) … Moors of Nokk (863)" },
  { from: 864, to: 865, era: "tob", evidence: "Unkempt Woods, Timorous Falls" },
  { from: 870, to: 871, era: "tob", evidence: "Hodstock Hills, The Theater of Eternity" },
];

/**
 * Rows where the band is known to be wrong or knowably tighter, keyed by map
 * short name. Each carries its reason, because a hand-set era is the one thing
 * here resting on somebody's word and is marked `curated` in the table for that
 * reason. `era: null` means "looked at, and there is no expansion to give".
 */
const OVERRIDES = {
  soltemple: { era: "classic", why: "a launch zone whose id (80) sits in the Kunark block" },
  erudsxing: { era: "classic", why: "the launch boat zone between Qeynos and Erudin; id 98 sits in the Kunark block" },
  erudsxing2: { era: "classic", why: "same place as erudsxing" },
  stonebrunt: { era: "classic", why: "free content on Odus added within months of Kunark; open on a classic-era ruleset (observed on EQ Legends), so hiding it before Kunark would be wrong" },
  warrens: { era: "classic", why: "as stonebrunt, which it opens onto" },
  newsebexp: { era: null, why: "EQ Legends-only zone (id 99 is unused on live); it belongs to no expansion" },
  neriakd: { era: "cotf", why: "id 43 is a reused gap in the launch block: the zone was added in 2016. Its own map connects it to Ethernere Tainted West Karana (Call of the Forsaken), so it can be no older than that" },
  shadowrest: { era: "pop", why: "id 187 sits among system zones; the map's only labelled exit is the Plane of Knowledge, so it needs Planes of Power" },
  scorchedwoods: { era: "eok", why: "the 786–816 block is interleaved; this is an Empires of Kunark zone" },
  lceanium: { era: "eok", why: "as scorchedwoods" },
  gorowyn: { era: "ros", why: "the 786–816 block is interleaved; Gorowyn is Ring of Scale's hub" },
};

/** Mirrors ZoneTable.Normalize: letters and digits, lower-cased, leading "the" dropped. */
function normalize(value) {
  const bare = value.replace(/[^A-Za-z0-9]/g, "").toLowerCase();
  return bare.startsWith("the") && bare.length > 3 ? bare.slice(3) : bare;
}

function findInstall(explicit) {
  const candidates = [
    explicit,
    process.env.EQDEEPS_EQ,
    "D:\\Users\\Public\\Daybreak Game Company\\Installed Games\\EverQuest Legends",
    join(process.env.PUBLIC ?? "C:\\Users\\Public", "Daybreak Game Company", "Installed Games", "EverQuest Legends"),
    join(process.env.PUBLIC ?? "C:\\Users\\Public", "Daybreak Game Company", "Installed Games", "EverQuest"),
  ].filter(Boolean);

  for (const root of candidates) {
    const file = join(root, "Resources", "ZoneNames.txt");
    if (existsSync(file)) {
      return file;
    }
  }

  throw new Error("No Resources/ZoneNames.txt found. Pass --eq <install> or set EQDEEPS_EQ.");
}

function readZoneIds(file) {
  const ids = new Map();
  for (const line of readFileSync(file, "utf8").split(/\r?\n/)) {
    const parts = line.split("^");
    if (parts.length < 2 || !parts[1]) continue;
    const id = Number(parts[0]);
    if (!Number.isInteger(id)) continue;
    const key = normalize(parts[1]);
    if (!ids.has(key)) ids.set(key, []);
    ids.get(key).push(id);
  }
  return ids;
}

function bandFor(id) {
  return BANDS.find((b) => id >= b.from && id <= b.to) ?? null;
}

function derive(tableText, ids) {
  const out = [];
  const report = { byEra: new Map(), spanning: [], unbanded: [], overridden: [], missing: [] };

  for (const raw of tableText.split("\n")) {
    const line = raw.replace(/\r$/, "");
    if (!line.trim() || line.startsWith("#")) {
      out.push(line);
      continue;
    }

    const [shortName, display, source] = line.split("\t");
    const found = (ids.get(normalize(display)) ?? []).slice().sort((a, b) => a - b);
    let era = null;
    let eraSource = null;

    if (found.length === 0) {
      report.missing.push(`${shortName}=${display}`);
    } else {
      const band = bandFor(found[0]);
      const bands = [...new Set(found.map((i) => bandFor(i)?.era ?? "-"))];
      if (bands.length > 1) {
        report.spanning.push(`${shortName.padEnd(18)} ${display.padEnd(40)} ${found.join(",")} → ${bands.join("/")} ⇒ ${band?.era ?? "-"}`);
      }
      if (band) {
        era = band.era;
        eraSource = "id";
      } else {
        report.unbanded.push(`${shortName.padEnd(18)} ${display.padEnd(40)} ${found.join(",")}`);
      }
    }

    const override = OVERRIDES[shortName];
    if (override) {
      report.overridden.push(`${shortName.padEnd(18)} ${String(era ?? "-").padEnd(8)} → ${String(override.era ?? "-").padEnd(8)} ${override.why}`);
      era = override.era;
      eraSource = override.era ? "curated" : null;
    }

    report.byEra.set(era ?? "(none)", (report.byEra.get(era ?? "(none)") ?? 0) + 1);
    out.push(era ? `${shortName}\t${display}\t${source}\t${era}\t${eraSource}` : `${shortName}\t${display}\t${source}`);
  }

  return { text: out.join("\n"), report };
}

function main() {
  const args = process.argv.slice(2);
  const check = args.includes("--check");
  const eqIndex = args.indexOf("--eq");
  const install = eqIndex >= 0 ? args[eqIndex + 1] : undefined;

  const zoneNames = findInstall(install);
  const ids = readZoneIds(zoneNames);
  const before = readFileSync(TABLE, "utf8");
  const { text, report } = derive(before, ids);

  const say = (s = "") => process.stdout.write(s + "\n");
  say(`ZoneNames: ${zoneNames} (${[...ids.values()].reduce((n, v) => n + v.length, 0)} rows, ${ids.size} distinct names)`);
  say();
  say("Rows per era:");
  const order = [...BANDS.map((b) => b.era).filter((e, i, a) => a.indexOf(e) === i), "(none)"];
  for (const era of order) {
    if (report.byEra.has(era)) say(`  ${era.padEnd(8)} ${report.byEra.get(era)}`);
  }
  if (report.spanning.length) {
    say();
    say(`Names with ids in more than one band (lowest wins) — ${report.spanning.length}:`);
    report.spanning.forEach((s) => say("  " + s));
  }
  if (report.unbanded.length) {
    say();
    say(`Rows whose ids fall outside every band (left blank) — ${report.unbanded.length}:`);
    report.unbanded.forEach((s) => say("  " + s));
  }
  if (report.overridden.length) {
    say();
    say(`Overrides applied — ${report.overridden.length}:`);
    report.overridden.forEach((s) => say("  " + s));
  }
  if (report.missing.length) {
    say();
    say(`Display names not in ZoneNames.txt (no era possible) — ${report.missing.length}:`);
    report.missing.forEach((s) => say("  " + s));
  }
  say();

  const changed = text !== before;
  if (check) {
    say(changed ? "CHECK FAILED: zones.tsv would change. Run without --check to update it." : "OK: zones.tsv is up to date.");
    process.exit(changed ? 1 : 0);
  }

  if (changed) {
    writeFileSync(TABLE, text, "utf8");
    say(`Wrote ${TABLE}`);
  } else {
    say("zones.tsv already up to date.");
  }
}

main();
