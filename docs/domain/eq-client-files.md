# What the EverQuest Client Keeps on Disk — Domain Reference

An inventory of the game install, made for one question: **which item, NPC,
spell and zone facts can the app learn without a website?** Written for issues
#51 (an NPC list you can search) and #62 (from an item in the log to the page
that says how to get it), and kept because the answer is mostly "not that",
and the next person to wonder should not have to open ten thousand files to
find out again.

Like the [map-file reference](eq-map-format.md), this has no spec to defer to.
**The install is the authority.** Every claim was checked against one stock
EverQuest Legends install on 2026-08-16 (`D:\Users\Public\Daybreak Game
Company\Installed Games\EverQuest Legends`, launcher id `eqns`); sizes and
counts are quoted so a future reader can tell whether their install still
agrees. Legends ships the whole live client — every expansion's zones and
strings are on disk — with only the classic world unlocked server-side, so
what is here is what *any* Daybreak client of the same vintage carries.

## 1. The short answer

| You want… | On disk? | Where |
|---|---|---|
| Item id → name, stats, lore, slot, class | **No.** Never was, on any EQ client — item data is server-side and sent on demand. | — |
| Item names the *player* has seen | Yes, as a by-product | `userdata\LF_<Char>_<server>.ini` (loot filters: `#ITEM_ID^FILTER_ID^ICON_ID^ITEM_NAME`), `<Char>_<server>-Inventory.txt` (`/outputfile inventory`) |
| Item links in the log, with ids | **No, not on Legends.** 0 `\x12` bytes in a 112 MB log; a linked item arrives as plain text (`Glubbug tells the group, 'Fine Steel Two Handed Sword +2'`). Live clients do write the `\x12<payload>name\x12` markup. | `Logs\eqlog_<Char>_<server>.txt` |
| Item icons | Yes — 379 sheets of 36 icons, 40 px, but only useful once an icon id is known (the `LF_` file has one per item) | `uifiles\default\dragitem1..379.dds`, layout in `EQUI_DragItems.xml` |
| NPC / spawn database (name, level, zone, loot, faction) | **No.** | — |
| Named/rare NPC → zone | **Yes, ~2,150 names** — the Hunter achievements list every rare by zone, classic through Velious and beyond | `Resources\Achievements\AchievementComponentsClient.txt` (type-1 rows) joined to `AchievementsClient.txt` ("rare monsters in *Zone*") |
| NPC locations on the map | Yes, community data — the Brewall pack is installed with the client | `maps\brewalls\<zone>_1.txt` `P` labels: `Name_(Roam)`, `Willaen_(Banker)`, `GS:_Item_Name` (ground spawns) |
| NPC lore blurbs | 306 famous ones (Overseer agent cards) | `dbstr_us.txt` types 52/53/61 |
| Faction id → name | Yes, complete, 2,048 rows | `dbstr_us.txt` type 45; ripple table in `Resources\Faction\FactionAssociations.txt` |
| Spell database | **Yes, complete**: 73,963 spells, 173 columns; cast messages; descriptions | `spells_us.txt`, `spells_us_str.txt` (headed: `#SPELLINDEX^CASTERMETXT^CASTEROTHERTXT^CASTEDMETXT^CASTEDOTHERTXT^SPELLGONE^`), `dbstr_us.txt` type 6 |
| Every system/combat message template | Yes — 7,120 format strings with `%N` slots | `eqstr_us.txt` |
| Zone id → long name | Yes, 699 rows — but **no short name column**, so nothing on disk joins `2` ↔ `qeynos2` ↔ "North Qeynos" | `Resources\ZoneNames.txt` (`id^Long Name^n^n`) |
| Expansion names | Yes, 64 | `dbstr_us.txt` type 20 |
| Which expansions this *server* has unlocked | **No.** Nothing on disk says; it is a server fact | — (issue #57: the era is a setting) |
| Server list / current server | Only the last one | `eqlsPlayerData.ini` `LastServerName=Qeynos`; `_characters.ini` `Character0=Moonchopper,qeynos`; the log's file name |
| Quest / task text, merchant lists, loot tables | **No.** | — |

## 2. The files, briefly

Root of the install (bytes / lines): `spells_us.txt` 38,205,280 / 73,963 ·
`dbstr_us.txt` 9,833,836 / 72,915 · `spells_us_str.txt` 5,218,227 / 73,964 ·
`eqstr_us.txt` 454,039 / 7,122 · `racedata.txt` · `eqclient.ini` ·
`eqlsPlayerData.ini` · per-character `UI_<Char>_<server>_LO*.ini`,
`<Char>_<server>-Inventory.txt`. Around 200 more `*_chr.txt` model manifests.
No `.csv`/`.tsv`/`.json` at the root.

Archives: 1,456 `.eqg` (modern zones and models; `IT#####.eqg` are item
*meshes* by "IT number", no names), 788 `.s3d` (classic PFS: geometry,
`global*_chr.s3d` race models), 19 `.pak` (loading art), 9 `.pfs` (audio).
None bundles item or NPC data; the format never carried it.

### `dbstr_us.txt` — the master string table

`id^type^text^flag^`, 68 types. The ones that matter here:

| type | rows | what |
|---|---|---|
| 1 / 4 | 2,584 / 6,024 | AA name / description |
| 6 | 35,859 | spell description (keyed from a `spells_us.txt` column) |
| 11 / 12 / 13 | | race singular / plural, class plural |
| 17 / 18 / 47 | 79 / 83 / 81 | alternate currency name / plural / description |
| 20 | 64 | expansion names |
| 39 / 44 | | resist and damage type names |
| 43 | 1,125 | item click-effect descriptions — keyed by *effect* id, not item id |
| 45 | 2,048 | **faction names** (`262^45^Guards of Qeynos^`) |
| 52 / 53 / 61 | 306 each | Overseer agent short name / full name / lore — real NPCs (`181^53^Fippy Darkpaw^`) |

Nothing in it is an item name or item lore.

### `Resources\`

`Achievements\AchievementComponentsClient.txt` (3,235 rows,
`ach_id^index^type^value^Display Text^`) — type-1 rows are "kill this NPC" and
the display text is the NPC's name: 2,156 unique names. `AchievementsClient.txt`
(865 rows) titles them, 84 as "rare monsters in *Zone*", so the join yields
**NPC → zone for the named/rare mobs of every zone from The Qeynos Hills to
Skyshrine**. The `value` column is an achievement-internal id and joins to
nothing else. Also: `Faction\*` (ids only — names are in `dbstr` 45),
`ZoneNames.txt`, `skillcaps.txt`, `basedata.txt`, `ACMitigation.txt`,
`SpellStackingGroups.txt`, `ItemDistillerDefs.txt` (22 item ids, the only
item ids in any shipped table), `npct.ini` (name-plate *colours*), the
Overseer `Ovr*` tables, and armour texture maps (`Layers\`, `NewArmorTagData`).

### Player-written files — the real item source

`userdata\LF_<Char>_<server>.ini` grows as the player sets loot filters:

```
#ITEM_ID^FILTER_ID^ICON_ID^ITEM_NAME
13374^4^819^Froglok Poison Gland
5016^4^605^Rusty Broad Sword +4
```

478 rows on the reference install; item ids 1,002–177,922; icon ids 500–10,266.
**These ids are the game's own** — checked against a Legends community
database (EQLBase), 407 of 478 names match by id exactly and the rest differ
only in capitalisation (`Raw-Hide` / `Raw-hide`) — so an item the log names
can be joined to an id here and from there to any id-addressed site.
`<Char>_<server>-Inventory.txt` (`Location⇥Name⇥ID⇥Count⇥Slots`) carries ids
too, for what is worn and banked. Both are per character and per server.

**Legends decorates names.** Upgraded items carry a rank (`Fine Steel Rapier
+2`, `Mesh Gauntlets +1`), exalted ones a tag (`Guise of the Deceiver
(Exaltation)`). Reference sites list the base name; strip ` +N` and
` (Exaltation)` before asking one.

### `maps\`

Covered in full by [eq-map-format.md](eq-map-format.md). For this question:
the stock pack's 1,718 `P` labels are almost all zone connections; the
installed Brewall pack's 33,994 carry the NPC/POI semantics — 975 end in
`(Roam)` (named roamers), 453 name merchants/bankers, `GS:_` prefixes ground
spawns by item name, `TRAP:`, tradeskill stations. Community data, no ids,
no levels — coordinates and names only.

## 3. What this means for the app

1. **Names are the join key.** The log names things; nothing on disk numbers
   them for it. Item and NPC lookup is by name first, and by id only after a
   local file (`LF_`, inventory) has supplied one for that name.
2. **The log itself is the NPC database that matters.** Every mob fought,
   considered, killed or heard speaking is in the log with a zone around it,
   which is exactly what F25/F26 already persist per server; the achievements
   file adds a zone for named mobs never met, and the Brewall labels a place
   for them on the map.
3. **Spells are the exception** — complete on disk, and the one domain where
   a bundled or install-read reference table is worth building.
4. **Read from the install, never bundle** (as F27 does with maps): every file
   above is Daybreak's or the player's own, and copying it into the repo would
   be both a licence question and a stale copy.
