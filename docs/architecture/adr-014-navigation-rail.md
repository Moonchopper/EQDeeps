# ADR-014: One navigation rail, and a conditional fight list

Status: accepted (2026-08-09). Scope: feature F7b (standard views), F8 (custom
dashboards).

## Context

Navigation had grown into two stacked horizontal rows above every screen. The
top row switched between Overview and the user's own dashboards; the second row,
visible only under Overview, held the standard views. By the time Gear (F24,
since withdrawn), Mobs (F25) and Incoming (F26) shipped, that second row carried
ten entries and
had started scrolling sideways — `.sub-tabs` had `overflow-x: auto`, which is
the point at which a navigation control has stopped showing you where you can
go. Adding the eleventh would have made it worse, and the backlog has more views
in it.

The two rows also cost about 60px of height on a layout whose panels are
height-hungry (stacked time charts) and width-rich (a 16:9 window with a
290px fight list on one side and a lot of unclaimed space in the middle).

## Decision 1: one vertical rail on the left, both levels in it

The standard views and the user's dashboards live in a single left rail,
separated by a heading rather than by being on different rows. The top tab row
is gone.

Vertical suits the content: the list grows with every view shipped, and a window
has far more spare width than spare height. A rail also removes the awkwardness
of a two-level hierarchy where the first level had exactly two states — a row
that said "Overview" and then repeated itself underneath.

Merging the levels means "which view am I on" has one answer in one place. The
cost is that standard views and user dashboards no longer *look* like different
kinds of thing by virtue of sitting on different rows; the section headings and
the divider carry that distinction now, and the read-only affordances on the
views themselves ("Customize a copy") already did the rest.

Dashboard actions — export, import, delete — move under the dashboard list, and
still appear only when a dashboard is selected. A permanent toolbar would be
disabled on every standard view, which is most of the app.

## Decision 2: the fight list appears only where it does something

The fight list is a **range selector**: what it picks becomes the app-wide time
frame (F7). It is therefore only meaningful on views that report over a time
frame — which is all of them except one:

- **Mobs** (F25) is what this server's mobs are worth, learned across every kill
  the app has ever seen. It has no time frame at all.

(Gear was the other, until F24 was withdrawn in v0.9.4.)

There the pane was 290px of furniture whose every click changed nothing on
screen. It is now absent rather than collapsed: collapsing implies there is a
state worth getting back to, and a spine to click would be a promise the view
cannot keep. The panels take the width.

The frame itself is untouched by the absence — a range framed on Summary is
still in force when Mobs is open, and is still there on the way back. Hiding the
selector hides the control, not the setting.

## Decision 3: the fight list keeps the far edge

Rail | panels | fights, rather than rail | fights | panels. Navigation and the
thing being navigated sit next to each other, and the selector does not drive a
wedge between them. It also leaves the fight list where the eye already looks
for a long scrolling list of pull names: against an edge, not in the middle.

## Consequences

- `.dashboard` is a three-column grid with two variants — the collapsed spine
  (26px) and no fight column at all — rather than one modifier on a two-column
  grid.
- The collapse chevrons flipped direction: the list now hides to the right.
- `scripts/screenshots.mjs` drives `.rail-tab`, not `.sub-tab`.
- Nothing about the QuerySpec model, the standard-view definitions, or what is
  persisted changed. This is layout: `App.tsx` and `styles.css`.
