# ADR-016: Zone maps — the geometry, the name join, and the world graph

Status: accepted (2026-08-14). Scope: feature F27.

## Context

Every view in this app is an aggregation of things that happened. A map is not.
It is the one piece of the game the log never mentions and the player always has
open on a second monitor — where a zone's exits are, what the floor below looks
like, how to get from here to there.

The material for it is already on the player's disk. A stock install ships
`maps/` (196 files) and `maps/brewalls/` (1708 more), and those files carry both
the geometry and, in their labels, the zone connections. Nothing about this
feature needs a network, a dataset, or the game running.

One thing it does need turns out not to exist anywhere, and that is most of what
this ADR is about.

## Decision 1: read the player's maps, never ship our own

The maps are read from the EverQuest install at runtime and are not bundled.

Three reasons, in order of weight. The community map sets are freely
*distributed* but not licensed for redistribution under anything compatible with
this repo's MIT/Apache/BSD rule, and shipping them would be the first dependency
in the project that could not be justified from its licence. Players edit their
maps — adding camp markers, deleting clutter — and a bundled copy would silently
show them someone else's version of a zone they have customised. And the files
total roughly 100 MB, against an installer that is currently a fraction of that.

The cost is that a machine without EverQuest on it has no maps. That is accepted:
this is a companion app for a game, run on the machine the game runs on. The
install is located by the usual paths and is overridable, and a missing install
degrades to an empty Map view with an explanation, not an error.

## Decision 2: a map is not a QuerySpec

The house rule is to check whether a new view should be a query first. This one
should not, and it is worth writing down why so the question is not reopened.

A `QuerySpec` describes an aggregation over records the log produced. A map has
no records behind it, no time frame, no metric, and no grouping — the time frame
control that sits above every other panel would have nothing to act on. Forcing
it through the panel model would mean a panel carrying a `QuerySpec` that is
never executed, which is worse than an honest second kind of thing.

So the map is a first-class navigation-rail destination (ADR-014), and the
dashboard panel is a *separate*, deliberately reduced view for people who want
"where am I" beside their parse.

## Decision 3: the zone name table has to be shipped, and is derived, not sourced

**The problem.** The log says `You have entered The Estate of Unrest.` The map
file is `unrest.txt`. Nothing in an EverQuest install joins those two names.
`Resources/ZoneNames.txt` lists display names against zone ids; `maps/` is named
by short name; no file carries both. This is not an oversight — the server tells
the client its zone's short name on zone-in, so the client never needs a table.
The log is the one consumer that has only the display name.

Short names are historical abbreviations rather than contractions of the display
name (`poknowledge`, `cazicthule`, `gfaydark`), so normalising and comparing the
two resolves only **108 of the 581** zones that have maps.

**What closed the gap.** The maps' own `to_<Zone>` labels name a zone's
neighbours *in display-name space* — the same space the log speaks. So once some
zones are known, an unknown one is pinned by the neighbours pointing at it, and
confirmed when it points back. Running that to a fixed point added **31 more**,
every one of them reciprocated.

The threshold matters. An earlier pass accepted an inference from a *single*
neighbour and produced `oldblackburrow → The Void`, `veeshan → Gates of Kor-Sha`
and `barter → The Plane of Knowledge` — because one neighbour with one
unassigned target does not identify anything, it just names whatever is left
over. Requiring two independent neighbours plus a reciprocated edge took the
suspect count to zero.

**The rest is hand-written**, and marked as such. 125 rows are curated. Every
display name in the file, derived or curated, is checked verbatim against the
client's own `ZoneNames.txt` by `ZoneTableTests`. That check is not ceremony: it
rejected **31 of the first 89 curated rows** — "Permafrost Caverns" for
Permafrost Keep, "Neriak Commons" for `Neriak - Commons`, "Plane of Sky" for
`The Plane of Sky`.

It catches an invented *name*. It cannot catch an invented *pairing*, and that
is the one thing in this feature resting on somebody's word. So
`ZoneNameSource` — `Name`, `Graph`, `Curated` — is carried all the way to the UI
rather than smoothed away, and the user can override any zone's map.

**The table is deliberately incomplete**: 264 of 581 short names, covering 128
of the 133 zones a stock client ships a map for. An unknown zone resolves to no
map and the user picks one; that same escape hatch is how a wrong pairing gets
corrected. Completing it by transcribing an external zone dataset was considered
and rejected — the provenance of such a list is exactly the kind of thing this
repo cannot take on, and a wrong row from a stranger is harder to notice than a
missing one.

## Decision 4: the world graph is undirected, and drops what it cannot resolve

Edges come from the `to_`/`from_` labels, which are community annotation rather
than game data and read like it: backticks for apostrophes, `(Boat)` suffixes,
truncations like `to Ak`, and points naming three destinations at once
(`to Butcherblock/Ocean of Tears/Qeynos`).

Every label is resolved through the zone table and **dropped if it does not land
on a zone the client has a name for**. That biases toward a smaller, truthful
graph: a route the app cannot describe is better than one it invents.

Edges are undirected for routing. A mapmaker labels the side of the connection
they were standing on, so roughly half of every real pair is written down only
once; requiring both directions fragments the world into islands.

Routing is breadth-first and unweighted. The graph carries no travel times, so
"fewest zones" is the only ordering the data supports, and neighbours are walked
in name order so the same question gives the same answer twice.

## Consequences

- The map parser is a pure function of the file text, like the log grammars, so
  the format is testable from string literals. Two quirks were found by parsing
  the whole corpus rather than by reading a spec — there is no spec — and both
  are pinned by tests: **1660 labels contain commas**, and a handful of files
  omit the newline between two records.
- `MapCorpusTests` and `ZoneGraphCorpusTests` parse a real install end to end and
  are opt-in via `EQDEEPS_MAPS` / `EQDEEPS_EQ`, since CI has no game on it.
  Measured on a stock install: 3,244,827 segments, 35,719 labels, **zero
  malformed**.
- Rendering budget is set by the largest zone, `everfrost` at 26,383 segments.
  That is comfortable for a 2D canvas and rules out an SVG DOM node per segment.
- The maps' colours were chosen for the client's light background — the darkest
  are `64,64,64` — and this app is dark-only (ADR-015). Lifting them is a
  rendering concern, handled at draw time, and the file's colour is never
  rewritten.
