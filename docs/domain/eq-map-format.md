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

## 4. Zone connections

Labels beginning `to ` or `from ` name a way out of the zone. This is the only
place in an EverQuest install where the world's connectivity is written down.

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
| `name` | 108 | Short name and display name agree once punctuation and a leading "The" are removed. |
| `graph` | 31 | Deduced from connection labels: a known zone's `to` labels name its neighbours in **display-name space**, so an unknown zone is pinned by the neighbours pointing at it, and confirmed when it points back. |
| `curated` | 125 | Written down by hand. |

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

The table is knowingly incomplete: 264 of 581 short names, covering 128 of the
133 zones a stock client ships a map for. An unknown zone resolves to no map and
the user picks one, which is also how a wrong pairing gets corrected.

### 5.1 One display name, several maps

Normal, not a defect. A revamped zone keeps its old map beside the new one and
both claim the name: `freportw` and `freeportwest` are both "West Freeport";
`tox` and `toxxulia` are both "Toxxulia Forest". Eight display names in the
shipped table have two maps. Offer both — only the player knows which they mean.

### 5.2 Instances

The log names an instance with its difficulty attached: `The Estate of Unrest 4
(Refined)`. An instance is the same geometry as its open-world zone, so strip
the suffix (`InstanceZone.Parse`) before looking a map up.

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
