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
