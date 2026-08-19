# ADR-017: The rail groups views by the question they answer, and the group decides the furniture

Status: accepted (2026-08-15). Scope: the SPA shell — `App.tsx`,
`SessionBar.tsx`, `styles.css`. Supersedes the "one heading, both levels"
part of ADR-014 decision 1; ADR-014's fight-list decisions (2 and 3) stand.

## Context

ADR-014 stood the two rows of tabs up into one rail with two headings:
"Overview" for the views that ship with the app, "Dashboards" for the user's
own. At the time Overview held six entries and they were all fight parses.
By v0.11.3 it held ten, and they were four different kinds of thing:

- fight parses over a time frame — Summary, Healing, Tanking, Stances;
- the character's record over the life of the log — Experience, Faction, Loot;
- knowledge learned across every log opened on the server — Mobs, and the
  second half of Incoming;
- a tool that reads a folder on disk — Map.

"Overview" was a name left over from the tab it used to be, and a heading that
means "everything" over ten entries is a list of tabs by another name. The
owner's brief was exactly that: not a flat list, and group the utilities,
because the app is now more than a DPS meter.

Two smaller faults followed from the flat list. Which views get the fight list
was decided per view, by name (`showFights = !(MOBS || MAPS)`), and every new
view meant revisiting that line. And the header's time controls — range,
window, overlay, strip, hours, scroll, reset — stayed lit on Map and Mobs,
where nothing on screen answers to them.

## Decision 1: three groups, named for the question

| Group | Views | Time frame applies |
|---|---|---|
| **Combat** — what happened in the fight | Summary, Healing, Tanking, Stances, Incoming | yes |
| **Character** — what happened to me over the log | Experience, Faction, Loot | yes |
| **World** — what this server's world is worth, learned across every log | Mobs, Map | no |

Then **Dashboards**, unchanged: the user's own, plus New.

Grouping is by the question, not the mechanism. Incoming is half a raw feed
and half a server index, but the reason anyone opens it is "what is hitting
me", which is a Combat question; Mobs is a server index and Map a folder on
disk, and both answer "what is this world like", which is World. The test of a
grouping is whether the backlog lands without argument: Deaths (F9), Spells
(F10) and Hit distribution (F11) are Combat; Chat archive (F15) is Character;
nothing in P1 or P2 wants a fourth heading. Combat is the largest group and
will stay so, which is right — it is still a DPS meter first.

"Overview" is gone. Summary is the first Combat entry and remains the landing
view. If a cross-group landing page is ever wanted — the meter, the last
death, xp/hr on one screen — that is what a "Home" entry would mean, and it is
not built.

## Decision 2: the group decides the furniture

Each group carries one flag, `framed`: whether the app-wide time frame (F7a)
applies to its views. That one bit now drives both the fight list *and* the
header's time controls. World views show neither.

This generalises ADR-014 decision 2 — hide the selector, keep the setting —
from one view named in code to a property of the group. A view added to a
group inherits the right chrome; the per-view name check is gone. The frame
itself is untouched by the absence, exactly as before: a range framed on
Summary is still in force on Map and still there on the way back.

The time controls are absent rather than disabled for the same reason the
fight list is: a greyed control promises a state the view cannot reach.

## Decision 3: the groups are data

`ui/src/dashboards/railGroups.ts` holds the table — key, label, `framed`, the
view ids in order. The rail renders from it; the standard-view ids resolve
against what this log has, so Stances vanishes from Combat on a character who
never held one, the way it vanished from the flat list. The four hand-built
screens (Summary, Mobs, Incoming, Map) keep their names and hover text in
`RAIL_ENTRIES` beside the rail, because they are not dashboards and have no
name of their own to bring.

## What was considered and not done

- **An activity bar plus a per-group sidebar** (icons in a 48px column, the
  group's views in a second column). Groups have two to five entries; a
  second click-column for that taxes the ninety-percent path (open the app,
  read Summary), and Map would become three columns because it already
  brings a zone list. The grouped rail collapsed to icons *is* an activity
  bar, so this stays the upgrade path if the view count ever warrants it,
  without a redesign.
- **A top mode switcher** (Combat | Character | World) with the rail changing
  under it. That re-creates the two-level hierarchy ADR-014 removed.
- **Moving the fight list left.** ADR-014 decision 3 holds; nothing here needs
  it.

## Consequences

- Layout only. Nothing about the QuerySpec model, the standard-view
  definitions, or what is persisted changed. `eqdeeps.stdView` in
  localStorage keeps its meaning; a remembered view id lights up under
  whichever group now holds it.
- `SessionBar` takes a `framed` prop and renders the time-controls group
  only when it is true; `.version` holds the right edge on its own when the
  group is absent.
- `scripts/screenshots.mjs` still drives `.rail-tab` by label; nothing there
  moved.
- The rest of the brief this ADR came from is phased behind it, each its own
  change: a Settings dialog absorbing the set-once toggles from the header
  (density, pets → owners, overlay, strip, hours), a Logs popover absorbing
  the open-path form and detected-logs select, and a real icon set so the
  rail can collapse — and auto-collapse on Map, where the zone list is a
  second left column. ADR-015's open note on the Unicode glyphs is the same
  item as that last one.

## Decision 4 (2026-08-15): preferences in one dialog, reached from the rail's foot

The header carried five different jobs, and two of them were preferences: two
checkboxes by the log picker (pets → owners, compact) and four selects inside
the time-controls group (overlay, hours, strip, scroll), plus an update menu
hung off the version number. A thing you decide once sat beside the range you
change all night, and the row had run out of width at anything narrower than
the owner's monitor.

Those now live in a **Settings** dialog — sections Display, Charts, Updates —
opened from a **utility cluster pinned to the foot of the rail**, which also
shows the version and a dot when an update is staged or on offer. Sections
scale where a toolbar does not: the next preference gets a row, not a header
slot. Every control writes straight through to the handler the header used,
so a change shows behind the dialog as it is made and there is no Apply. The
hover titles became visible sentences: a preference you set once is one you
have to understand once, and a tooltip is the wrong place for a sentence.

What stays in the header is what changes during play: the session tabs, the
log picker (until the Logs popover), the time-frame group — range, window and
the reset pill — and the update's *live* state (a download in flight, a staged
install, a failure), which is something happening rather than something to
set. `reset` now resets only the time state; it used to reset the fight
overlay too, from when that sat in the same group, and a reset that quietly
undid a preference was a trap.

`UpdateSettings.tsx` is gone; its menu is the Updates section.

## Decision 5 (2026-08-16): one log picker, in a dialog, reached from the rail and a `+`

The header's other permanent resident was the log trio: a "Detected logs
(n)…" dropdown that flattened every source — the running game, install
folders, recently opened, the bundled sample — into one list and hid whatever
was already open; a rescan button; and a free-text path box. Between them
they were most of the header's width, and they are used at the start of a
session and then not again for the night. The welcome screen, meanwhile, had
grown its own list of the same logs, with the one affordance the dropdown
lacked (forget a recent log), and its own copy of the sample callout.

There is now **one `LogPicker`**, used by both. It groups by how EQDeeps knows
about a log — *Running now*, *Recent* (with ✕ to forget), *Installed*, and
the *Sample* on its own dashed row — shows a log that is already open with an
`open` tag and switches to it on click rather than hiding it, and carries the
path box beneath. The **Logs dialog** wraps it with rescan and close, and is
opened from the rail's utility cluster (above Settings) or from a **`+` after
the session tabs**, which is where a browser puts "new tab" and needs no
explaining. The welcome screen is the same picker with a heading.

The header is now: brand · session tabs · `+` · the time-frame group ·
the update's live state. The time-frame group also hides when no log is
open, since there is nothing for it to frame — the same rule as the World
views, applied one level up.

## Decision 6 (2026-08-16): icons, and a rail that collapses to them

Every rail entry now carries an icon, from Tabler Icons (MIT; only the
seventeen used are bundled, about 11 KB) — chosen for the question the view
answers rather than the mechanism, and one shared glyph for every user
dashboard because their names are what tell them apart. The rail extracted
into `NavRail.tsx` on the way; it had outgrown the middle of `App.tsx`.

The rail **collapses to that column of icons** — 44px against 150px — from a
toggle at its foot, and the choice persists. Collapsed, it is the same rail:
labels move into the hover title, group headings become their rule, nothing
is a different control. This is the activity-bar upgrade path Decision 1
left open, arrived at by narrowing rather than redesigning.

**On the Map the rail starts collapsed** whatever the standing preference,
because the Map brings its own left column — the zone list — and two wide
lists side by side was the shape that prompted this. A toggle there is an
override for that visit, not a change of preference; leaving the Map drops
it and the preference stands. Two pieces of state, deliberately: a
preference and a per-visit override, rather than one bit that the Map would
have to silently rewrite.

*Reversed 2026-08-17.* In use it read as the rail changing its mind per
view — open on Loot, closed on Map, open again on Bestiary, which has a
left column of its own and never did this. The owner called it
inconsistent, and it was: a preference the app overrides is not a
preference. One bit now, the user's, honoured on every view; whoever wants
the Map wide collapses the rail once and it stays collapsed everywhere,
which is what "preference" means.

ADR-015's open note on Unicode glyphs is *partly* closed by this: the rail's
are gone. The ones elsewhere — ⚔ and ☠ in the fight list, ✕, ↻, ★, the
chevrons — remain, and are the same job for a later change now that the set
is in.
