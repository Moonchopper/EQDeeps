# A snapshot of EQLBase's NPC data — not ours, and not under this repo's licence

Everything in this folder except this file came from **[EQLBase](https://eqlbase.com)**, a
player-made reference for EverQuest Legends by Rex Saurus. `manifest.json` records the URL each
file was read from, its ETag, and when.

| | |
|---|---|
| `search-index.json` | EQLBase's search index, **trimmed to its NPC and zone rows** — the only ones this app reads. Their items, spells, recipes and quests are not here. |
| `npcs-<n>.json` | One file per zone, byte-for-byte as published at `/data/npcs/<n>.json`. |
| `manifest.json` | Written by `scripts/snapshot-eqlbase.mjs`: source, snapshot time, and per file the path, ETag and size. |

## What this is for

EQDeeps' Bestiary (F30) and Slayer planner (F35) read this data. Until 2026-09-21 the app fetched
it from the site on demand and cached it (ADR-020). Working out where a creature type lives means
reading every zone's file, so every install was going to read the whole set; the owner chose to
ship one snapshot instead, so that an install asks EQLBase for **nothing** unless the player
presses Refresh — and a Refresh is conditional on the ETags recorded here, so an unchanged file
costs the site a header rather than a download. Fewer requests to a hobby site, and an app that
still works on a day the site does not.

## What it is not

**It is not covered by this repository's MIT licence.** The MIT grant in `LICENSE` is for
EQDeeps' own code and its own data files. It does not, and cannot, extend to this folder.

EQLBase states no licence. Its own position on the underlying material is that "game data and
assets remain the property of their respective owners and are presented here for informational
purposes under fair use". This snapshot is redistributed here **without an explicit grant from
EQLBase's author** — a decision the repository's owner made knowingly, for a non-commercial tool
used by a handful of people, and recorded in `docs/architecture/adr-020-npc-reference.md`. Nobody
reading this should take the presence of these files in an MIT repository as permission to reuse
them. Go to the source.

Every screen in the app that shows this data names EQLBase and links to it.

## If you are EQLBase's author, or a rights holder

Open an issue on this repository and the folder goes. It is built to be removable: delete
`data/eqlbase/` and the app falls back to asking the site for one zone at a time, only when a
player opens something that needs it, exactly as it did before the snapshot existed.

## Refreshing it

```
node scripts/snapshot-eqlbase.mjs
```

by hand, after a game patch — never from a build, never from CI. One request at a time with a
pause between them, each one conditional, so a re-run on unchanged data transfers nothing.
