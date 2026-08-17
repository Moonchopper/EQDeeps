# EverQuest Map Files — Domain Reference

What EverQuest's map files contain and how to read them (feature F27,
[ADR-016](../architecture/adr-016-zone-maps.md)).

Unlike the log format, this one has no reference implementation to defer to and
no published spec. **The corpus is the authority.** Every claim below was
checked against a stock EQ Legends install — 1904 files, 3,244,827 line
segments, 35,719 labels — and the counts are quoted so a future reader can tell
whether their install still agrees.

## 1. Files

- Maps live in `<EQ install>\maps\`. A stock install ships 196 files there,
  plus 1708 in `maps\brewalls\` — a community set many players copy in.
- A file is named for the zone's **short name**: `gukbottom.txt`, `poknowledge.txt`.
- A zone is split across up to four files, which the client draws as
  independently toggleable layers: `gukbottom.txt`, `gukbottom_1.txt`,
  `gukbottom_2.txt`, `gukbottom_3.txt`. Only `_1`–`_3` are layer suffixes; a
  name that merely ends in a digit (`arena2`, `qeynos2`, `Thurgadina1`) is its
  own zone.
- Layer files are frequently present but empty.
- Encoding is ASCII. Players edit these files by hand, so treat everything here
  as hostile input: count what does not parse, never throw.

## 2. Records

One record per line, of two kinds:

```
L x1, y1, z1, x2, y2, z2, r, g, b
P x, y, z, r, g, b, size, Label_With_Underscores
```

`L` is a line segment, `P` a labelled point. Segments outnumber labels about
90:1, so anything that walks every record is dominated by geometry.

Colour channels are 0–255. The corpus contains out-of-range and fractional
channels; the client draws those maps, so clamp rather than reject.

The `P` size field is unspecified and used inconsistently — across the default
maps the values are 0 (1340×), 200 (184×), 240 (143×), then a scatter of
others. Treat it as opaque, not as a font size.

### 2.1 Two traps

Both were found by parsing the whole corpus rather than by reading a sample, and
both silently lose data if unhandled.

**Labels contain commas.** 1660 of them do:

```
P 770.1974, -12.3611, 68.7689,  0, 0, 240,  2,  Draton`ra,_Master_of_the_Void
```

Splitting a `P` record on commas and taking field 8 truncates that to
`Draton\`ra`. The label is **everything after the seventh comma**.

**Records run together.** A handful of files omit the newline between two
records:

```
L 368.8268, 2320.9848, 1951.6470, 368.8268, 2320.9848, 1951.6470, 0, 0, 0P -178.0000, -207.0000, -1624.3743, 255, 0, 0, 3, from_The_Plane_of_Tranquility
```

The client draws both, so a parser that splits only on newlines loses map detail
the player can see in game. Split additionally wherever `L ` or `P ` follows a
digit — requiring the preceding digit is what stops a label being cut in half.

### 2.2 Underscores are spaces

The format has no quoting; a space in a label is written as an underscore. So
`to_The_City_of_Guk` is "to The City of Guk". A label that genuinely wants an
underscore cannot say so, and none in the corpus appears to try.

## 3. Coordinates

Map-file order is **X, Y, Z**. Note this is *not* the order the game says
coordinates in: `/loc` prints Y, X, Z.

World space maps onto screen space with **no axis flipped**: +X is east and runs
right, +Y is south and runs down, so north is up.

This contradicts widely repeated folklore that EQ maps are drawn negated, so it
was settled against the files. The test only works on zones that share an
**outdoor border** — every zone has its own origin, so a door between two
interiors says nothing about which is north of which. (Northern and Southern
Felwithe appear to disagree for exactly this reason and are not evidence.)

On real borders it is not close:

| Zone | Exit | Lies |
|---|---|---|
| East Commonlands | West Commonlands | west |
| West Commonlands | East Commonlands | east |
| West Freeport | East Freeport | east |
| East Freeport | West Freeport | west |
| Qeynos Hills | South Qeynos | west |
| South Qeynos | North Qeynos | north |
| North Qeynos | South Qeynos | south |

Five of five east-west and both halves of the Qeynos pair agree, each confirming
the other. Rendered this way South Qeynos has the ocean to the west with its
docks reaching into it, which is where Antonica's western city keeps its harbour.

**Z is a floor**, not decoration: dungeons stack, and a Z window is the only way
to read a zone like Old Guk without every level drawn on top of the others.

**Game coordinates onto a map file.** A spawn point as a reference site (or
the game's `/loc`, once its Y-X order is undone) gives it is `[x, y, z]` in the
game's own frame, and the map files hold the same axes **negated**: a game
`(x, y)` draws at map `(−x, −y)`. Settled the same way as the orientation
above, against data rather than folklore — under that transform every one of
1,265 listed spawn points across six zones (West Karana, South Karana,
Kithicor, West and East Commonlands, Neriak Third Gate) lands inside its
map's bounding box; under identity, a swap, or a single-axis flip no zone
does better than 60% and Neriak Third Gate, whose geometry sits wholly in one
quadrant, scores zero. This is what the Bestiary's "show on map" uses
(ADR-020, decision 6).

## 4. Zone connections

Labels beginning `to ` or `from ` name a way out of the zone. This is the only
place in an EverQuest install where the world's connectivity is written down.
(There is no zone-guide data on disk: `Resources/` holds `ZoneNames.txt` and a
load-screen table, and the in-game atlas's pathing is not a file.)

The connection word is not always first. Exits that are an *object* rather
than a zone line — a portal, a teleporter, a clickable — tend to be labelled
by the object: the client's own East Freeport map writes
`portal_to_The_Plane_of_Sky_(click)`; the Fear portal is
`portal_to_The_Plane_of_Fear` on the Feerrott side and `Zone_In_from_Feerrott`
on the Fear side; West Freeport has `Teleport_to_Academy_of_Arcane_Sciences`.
A survey of both map sets (2026-08-16) found 280 labels with `to`/`from`
somewhere other than the start; run through the zone table, 9 resolve, of
which 7 are real connections and 2 are a Riwwi mob's patrol notes —
`Reluctant_Gladiator_(Hunter,Paths_To_Arena)` — whose `to` sits inside the
parenthetical. So: drop the parenthetical *before* looking for the word, then
accept it anywhere, and when it is not first require what follows to be
capitalised (prose like `back_to_entrance` and `Note:_complete_the_event_to_
open_the_floor` is not a name). The zone table remains the last word: `Teleport
to Hub`, `Elevator to Top` and `Portal to Island 3` are candidates it rejects.

They are **community annotation, not game data**, and read like it:

- Apostrophes are usually backticks: ``Dagnor`s Cauldron``.
- The same place is spelled several ways: `Griegs End`, ``Grieg`s End``;
  `Warslik's Woods`, `Warsliks Woods`.
- A parenthetical often carries *how* the exit is used, not part of the name:
  `to The Ocean of Tears (Boat)`, `to Grimling Forest (click stone block)`.
- One point can name several destinations:
  `to Butcherblock/Ocean of Tears/Qeynos`, `to Erudin or South Qeynos`.
- Some are truncated to uselessness: `to Ak`.

Of 681 distinct destinations across the default maps, only 327 are verbatim
client zone names; the rest need normalising (case, punctuation, a leading
"The") before they resolve, and some never do.

`from ` labels are mostly *internal* markers — `from 1A`, `from 2B` — naming a
point elsewhere in the same zone rather than another zone. Resolving every
destination against the zone table and dropping what does not land is what
separates the two without special-casing.

Coverage is partial and lopsided: 94 of the 196 client maps carry any `to`
label, against 528 of Brewall's 1708. Anything building a world graph should read
**both sets** — which map a zone is *drawn* from is a matter of taste, but which
exits exist is not.

## 5. The name join

**The log says `You have entered The Estate of Unrest.` The file is
`unrest.txt`. Nothing in an EverQuest install connects those two names.**

`Resources\ZoneNames.txt` lists display names against zone ids
(`63^The Estate of Unrest^12^60`, 699 rows, 576 distinct names). `maps\` is
named by short name. No file carries both, and this is not an oversight: the
server tells the client its zone's short name on zone-in, so the client never
needs a table. The log is the one consumer that has only the display name.

Short names are historical abbreviations rather than contractions of the display
name — `poknowledge`, `cazicthule`, `gfaydark` — so normalising and comparing
resolves only 108 of the 581 zones that have maps.

EQDeeps ships a table built three ways, each row marked with which
(`Maps/zones.tsv`):

| Source | Rows | How |
|---|---|---|
| `name` | 107 | Short name and display name agree once punctuation and a leading "The" are removed. |
| `graph` | 31 | Deduced from connection labels: a known zone's `to` labels name its neighbours in **display-name space**, so an unknown zone is pinned by the neighbours pointing at it, and confirmed when it points back. |
| `curated` | 130 | Written down by hand. |

The graph step needs **two independent neighbours plus a reciprocated edge**. A
single neighbour proves nothing — it merely names whatever is left over, which
is how an early pass concluded `oldblackburrow` was The Void and `veeshan` was
Gates of Kor-Sha.

Every display name in the file is checked verbatim against the client's own
`ZoneNames.txt` by `ZoneTableTests`. That check rejected 31 of the first 89
curated rows — "Permafrost Caverns" for Permafrost Keep, "Neriak Commons" for
`Neriak - Commons`, "Plane of Sky" for `The Plane of Sky`. **It catches an
invented name; it cannot catch an invented pairing**, which is why provenance is
carried to the UI rather than smoothed away.

Nor can it catch a *real* name attached to the wrong place. The mechanical
`name` join matched `hole` to "The Hole" — a real client name, but id 539, an
event copy — when the map is the classic zone the client calls "The Ruins of
Old Paineel" (id 39), which is what the log prints. `fearplane` had been paired
with "Fear Itself", a House of Thule zone, rather than "The Plane of Fear".
Both were found by joining the table to the client's zone ids (§5.3) and asking
which rows landed somewhere implausible; that join is a second, cheap check
worth re-running when rows are added.

The table is knowingly incomplete: 268 of 581 short names, covering 128 of the
133 zones a stock client ships a map for. An unknown zone resolves to no map and
the user picks one, which is also how a wrong pairing gets corrected.

### 5.0 The user outranks the table

Because the table is incomplete and fallible by construction, the person who can
see both the map and the game gets the last word. `map-settings.json` in the
document store — beside their dashboards, because it is their work and not a
cache — holds their corrections:

| Field | What |
|---|---|
| `root` | A maps folder they nominated, when discovery found none. Replaces discovery outright. Machine-level: it is where the files are. |
| `installs[<install>].chosen` | Normalized zone name → map short name, for one installation of the game. Beats anything the table says. |
| `installs[<install>].era` | The expansion that install's world has reached, as an era id (§5.3). Absent means the whole world. |
| `lastInstall` | The install most recently written to, so the Map tab opened with no log shows the world as it was last set. |
| `chosen`, `era` | The same two fields at the top level: the layer underneath every install, read as the fallback and written to only when no install is known. A file from before there were installs is read as-is. |

Keyed by **installation**, not by the shard in the log's file name. Which
drawing is right and how far the world is unlocked are facts about the game a
log comes from — an EverQuest Legends install runs a classic world with the
old Freeport and no Planes of Power; live has the revamps and everything; a
Project 1999 client its own era — and one machine may hold several. Every
server on one install shares its client and its era, so `qeynos` is a finer
cut than the thing that varies. The install is named by its folder — the log
sits in `<install>\Logs\`, so it is the folder above `Logs`, which every
client, live or emulated, agrees on — and reported on the session as
`install`. A log copied out of its game folder names no install and falls to
the layer underneath. A forget is applied to the install's layer and the one
underneath, so "forget" means gone rather than "the older answer shows
through".

The one thing this cannot tell apart is two servers on one install with
different eras — a progression server beside the regular ones on live. If that
matters, a per-server era on top of the install's is the addition, not a
different key.

The key is normalized exactly as `ZoneTable.Normalize` does it, with the
instance suffix stripped first, so a choice made against "The Estate of Unrest
4 (Refined)" is found again for "The Estate of Unrest". The two normalizers are
written out separately in C# and TypeScript; if they ever drift, overrides
silently stop applying, which is the one failure here that would be hard to
notice.

### 5.1 One display name, several maps

Normal, not a defect. A revamped zone keeps its old map beside the new one and
both claim the name: `freportw` and `freeportwest` are both "West Freeport";
`tox` and `toxxulia` are both "Toxxulia Forest", 699 segments against 7738.
**Twelve** display names in the shipped table have two maps:

```
The Bazaar          barter, bazaar          East Freeport   freeporteast, freporte
Befallen            befallen, befallenb     West Freeport   freeportwest, freportw
The Temple of Droga droga, overtheretwo     Highpass Hold   highpass, highpasshold
Erud's Crossing     erudsxing, erudsxing2   Misty Thicket   misty, mistythicket
The Ocean of Tears  oceanoftears, oot       Steamfont Mts   steamfont, steamfontmts
The Plane of Hate   hateplane, hateplaneb   Toxxulia Forest tox, toxxulia
```

Offer both — only the player knows which they mean. But offer them as one
*place* with a choice of drawing, not as two identical rows in a zone list:
the two cases behind a shared name (one zone drawn twice; two zones that share
a name across a revamp) are indistinguishable from the data, and in both the
player wants the place first and the file second. The world graph follows the
same rule — one node per place, its exits pooled across the drawings — because
a label to "The Plane of Hate" resolves to both files, and drawn per file that
made two Planes of Hate off the Oasis of Marr.

Note this is **not** the same axis as the map *sets*. A zone present in both
`maps/` and `maps/brewalls/` is one entry with two sets; `tox` and `toxxulia`
are two entries, each of which happens to exist in both sets.

The reverse — **one map claimed by several client names** — is real too and the
table cannot say it. Event and Hardcore Heritage copies keep the geometry and
rename the zone: `crushbone` is "Clan Crushbone" and "Reinforced Clan
Crushbone", `hateplane` is "The Plane of Hate" and, on EQ Legends, "The Plane
of Hate - Group". A row has one display name, so the second name resolves to
nothing and the user is asked to pick. Known gap; the fix is a many-to-many
table, not more curated rows.

### 5.2 Instances

The log names an instance with its difficulty attached: `The Estate of Unrest 4
(Refined)`. An instance is the same geometry as its open-world zone, so strip
the suffix (`InstanceZone.Parse`) before looking a map up.

### 5.3 Eras: which expansion a zone is from

**A stock install ships every expansion's maps whether or not the server has
unlocked them, and nothing on the disk says which it has.** The log names only
zones already visited — a lower bound at best. The map files carry geometry and
labels. `ZoneNames.txt` lists every zone that ever existed. So the World view's
era filter is **chosen by the player and never inferred**; with none chosen the
view is exactly what it was before eras existed.

What *can* be read off the client is a zone id per display name, and the ids
were handed out in blocks as expansions shipped. That is folklore, so it was
checked against the file itself — 699 rows in the install this was derived from
— and turned into two more columns of `zones.tsv`, `era` and `eraSource`, by
`scripts/derive-zone-eras.mjs`. The result is **checked in as data**: the app
never reads the player's `ZoneNames.txt`, and the derivation can be re-run and
argued with (`node scripts/derive-zone-eras.mjs --check` says whether the table
still matches the install it points at).

The same script writes a sixth column, `ids` — every id the client gives the
row's display name, ascending, comma-separated (`oceanoftears … 69,409,569`).
The eras only needed the lowest; the Bestiary (F30) needs them all, because
the reference site files its NPCs a thousand ids per zone id, so an id is the
address of a zone's roster and a listing's id says which zone it stands in
(ADR-020, decision 6). Two drawings of one name (`freportw`, `freeportwest`)
carry the same ids, and which one a site means is settled by content, not by
the table.

**What an era means.** The *earliest* expansion the place can exist in — a
lower bound. The World view hides a zone whose era is later than the chosen one
and routes only through zones that are not hidden, and the Map's zone list
drops it too — the chooser sits beside that list, since it narrows both. A
*named* zone with **no** era is shown under every filter: the same bias as the
rest of this feature, where a smaller truthful graph beats hiding a place the
player can walk into. An *unnamed* map (no table row, so no name and no era —
313 of the 581 files) is the exception in the list only: kept, they are three
quarters of a "Classic only" list, so under an era they step out and the list
says how many; "Any era" brings them back.

**One name, several ids.** Revamps and event copies keep the display name:
"The Ocean of Tears" is 69, 409 and 569; "The Sleeper's Tomb" is 128, 628, 801
and 831. A row takes the **lowest** id, because the place has been there since
then whichever drawing the player holds. 71 of 268 rows have ids in more than
one band and every one resolves to the earliest, which is what the log on a
classic-era server confirms: it prints "West Commonlands" (21), "The Northern
Desert of Ro" (34), "North Freeport" (8) — the launch names, not "The
Commonlands" (408) or "North Desert of Ro" (392), which are new names and
correctly get later eras.

**The bands**, inclusive, with the names at their edges so a later reader can
tell whether their file still agrees:

| Ids | Era | Edges |
|---|---|---|
| 1–77 | classic | South Qeynos … The Arena |
| 78–109 | kunark | The Field of Bone … Veksar (see overrides for the launch zones filed here) |
| 110–130 | velious | The Iceclad Ocean … The Marauders Mire |
| 150–182 | luclin | Shadow Haven … The Akheva Ruins; Arenatwo, Jaggedpine Forest, Nedaria's Landing fill 180–182 |
| 200–223 | pop | Ruins of Lxanvom (the Crypt of Decay) … The Prison of the Forsaken |
| 224–228 | loy | The Gulf of Gunthak … Hate's Fury |
| 229–277 | ldon | Deepest Guk: Cauldron of Lost Souls … Chardok: The Halls of Betrayal |
| 278–299 | god | The Caverns of Exile … Qvic |
| 300–336 | oow | Wall of Slaughter … The Ruined City of Dranik |
| 337–346 | don | The Broodlands … The Accursed Nest; Guild Lobby, Guild Hall, The Bartering Quarter |
| 347–368 | dodh | Ruins of Illsalin … Shadowed Grove |
| 369–393 | por | Arcstone … Deathknell; the Freeport revamp (382–391) and North/South Desert of Ro (392–393) |
| 394–415 | tss | Crescent Reach … Ashengate; revamps of nine older zones (407–415), only The Commonlands a new name |
| 416–435 | tbs | Katta Castrum … The Open Sea |
| 436–451 | sof | Fortress Mechanotus … Deepscar's Den |
| 452–479 | sod | Field of Scale … Ngreth's Den |
| 480–495 | uf | Brell's Rest … Lair of the Fallen |
| 700–723 | hot | The Feerrott (revamp) … Hermit's Hideaway Interior |
| 724–751 | voa | Argath … Modest Guild Hall |
| 752–769 | rof | Shard's Landing … Heart of Fear: The Epicenter |
| 770–776 | cotf | Bixie Warfront … Argin-Hiz |
| 777 | tbm | Sul Vius: Demiplane of Life, filed ahead of the next block |
| 778–785 | tds | Arx Mentis … Tempest Temple |
| 786–816 | tbm | **interleaved**: The Broken Mirror (796–798), Empires of Kunark (788, 790–791, 793–795, 799–800), Ring of Scale (789, 792, 813–816), anniversary and Hardcore Heritage revamps. Nothing here predates The Broken Mirror, so that is the bound; a zone here may really be later. |
| 817–823 | tbl | Plane of Smoke … Chamber of Tears |
| 824–830 | tov | The Eastern Wastes … Crystal Caverns |
| 831–836 | cov | The Sleeper's Tomb … The Temple of Veeshan |
| 843–848 | tol | Maiden's Eye … Basilica of Adumbration |
| 849–856 | nos | Bloodfalls … Deepshade |
| 857–863 | ls | Firefall Pass … Moors of Nokk |
| 864–865, 870–871 | tob | Unkempt Woods, Timorous Falls; Hodstock Hills, The Theater of Eternity |

Not banded, and left blank: 131–149 (unused), 183–199 (system zones — `Load`,
`CLZ`, the tutorials — and Shadowrest, see below), 502–699 (event and Hardcore
Heritage copies of older zones: "Reinforced Clan Crushbone", "The Feast of
Tishe Virm"), 837–842 and 866–869 (seasonal: Winter, Frostfell, Stomples Day),
and 872 onward (content newer than the vocabulary, plus 900 "Lake Nerius" and
the 99x test zones). Anything there that also has a lower id resolves to that;
Shadowrest is the only shipped row that lands nowhere else, and it is set by
hand.

**Where the band is wrong**, the row is set by hand and marked `curated`. Every
override carries its reason in the script; the shape of them is:

- **Launch zones filed in the Kunark block.** The Temple of Solusek Ro is id
  80, Erud's Crossing 98. Both are `classic`.
- **Free content between expansions.** The Stonebrunt Mountains (100) and The
  Warrens (101) were added on Odus within months of Kunark; on a classic-era
  ruleset they are open (a classic-era EQ Legends log enters both), so hiding
  them before Kunark would be the wrong mistake. Both are `classic`. Veksar
  (109) is left to its band: it cannot predate Kunark, which is what a lower
  bound has to get right.
- **A reused gap.** Neriak - Fourth Gate is id 43, in the launch block, and was
  added in 2016. Its own map connects it to Ethernere Tainted West Karana
  (Call of the Forsaken), so it is `cotf`. Shadowrest (187, among the system
  zones) has one labelled exit, to the Plane of Knowledge, so it is `pop`.
- **The interleaved block.** Where the expansion is beyond doubt — The
  Scorched Woods and Lceanium (Empires of Kunark), Gorowyn (Ring of Scale) —
  the row says so rather than settling for the block's bound.
- **No expansion to give.** New Sebilis Expedition (99) is EQ Legends-only and
  belongs to nothing; it is left blank and so always shown.

The result: 267 of 268 rows carry an era, 257 from their band and 10 by hand;
87 are classic, 28 Kunark, 15 Velious, 24 Luclin, 15 Planes of Power, and the
rest thin out along the tail.

**Two things the era deliberately does not know.** A *connection* has no era.
The maps annotate present-day exits — Brewall's Ruins of Old Paineel labels a
"portal" to Neriak - Third Gate, and the classic-only route from Qeynos to
Faydwer walks straight through it — and gating edges would need a second table
this corpus cannot supply. And a modern-client server
keeps the revamped versions of some old zones from day one — TLP-style servers
have the merged Commonlands and the rebuilt Freeport in "classic" — so a revamp
with a *new* name may exist earlier than its band says. EQ Legends does not do
this (its log prints the launch names), which is the case that was checked.

**An observation, not used.** In the Legends install the third and fourth
columns of `ZoneNames.txt` are `12^60` (or `12^0` for Kedge, Fear, Permafrost
and Hate) for exactly 77 rows: the launch zones minus the never-shipped ones
(Highpass Caves, Sunset Home, Nektropos, Aviak) and The Arena, plus 80, 98–101
and the two Legends-only zones. Every other row is `0^0`. That is a
classic-shaped set on a classic-era server, and it corroborates the band; but
the columns are undocumented, could not be checked against a live install, and
issue #57 rules out inferring the era from the client anyway. Recorded so the
next person does not have to rediscover it.

## 6. Sizes

For anything that has to hold or draw this:

- Largest single zone: `everfrost`, 26,383 segments. Next: `brewalls/resplendent`
  at 20,653.
- Whole corpus: 3,244,827 segments, 35,719 labels, **zero malformed** with the
  rules above.
- A zone has few distinct colours — everfrost's 26,383 segments use six. Group
  by colour before drawing: a canvas that changes stroke style per segment is
  the difference between smooth panning and a slideshow.
- Reading every map's labels for the world graph takes ~5 s, almost all of it
  disk. Parsing labels only (skipping `L` records) is worth doing: geometry is
  99% of the bytes and the graph draws none of it.

## 7. Colour on a dark page

The files were coloured for the client's **light** background. The darkest lines
in the corpus are `64,64,64`, which is close to invisible on EQDeeps' page
colour (`#0f0d0b`, ADR-015).

Raise lightness at draw time and leave hue and saturation alone — mapmakers use
colour to mean something (red zone lines, blue water) and players have learned to
read it. Never rewrite the file: these are the player's own maps, and an app that
"fixed" their colours on disk would be editing their work to suit its theme.
