# What's new in EQDeeps

Written for the people who use it. Each release's section here is what the
update dialog shows (the first six bullets) and what the GitHub release page
carries in full. **One sentence per bullet: a bold lead and what changed for
you.** No numbers, no reasoning, no before-and-after — that lives in the commit
and the ADR. Newest first; a change lands under **Unreleased** with its pull
request, and the release's Docs commit renames that heading to the version
being cut.

## v0.16.0 — 2026-08-17

- **Long logs are fast again** — switching any view, and every second of a live fight, no longer redraws or re-sends thousands of fights; the Incoming table shows the 200 most recent mobs until you filter or ask for all.
- **The World map can label each zone with its level range** — turn it on with the **levels** button in the World header.
- **The World's Mobs tab now browses the whole world by level**, opening on your own band, instead of listing the zone you are standing in.
- **The left rail now stays how you left it on every view** — the Map no longer collapses it on its own.

## v0.15.2 — 2026-08-17

- **The World map opens fast after every launch.** The first open after starting the app used to sit on "Reading every map's exits…" for a couple of seconds, no matter how many times you had opened it before — the app was rediscovering your game install once per zone. Now it is a quarter of a second, and even the very first build on a new install is a third of what it was.
- **The Bestiary now finds the mobs the site spells without their "a" or "an".** "An imp protector", every aqua goblin, aviak, centaur, cinder goblin and clockwork — about one generic mob in ten — did nothing when clicked, because EQLBase lists them without the article your log uses. They open now, and match everywhere a name from your log meets one from the site: the Bestiary and its measured tables, the Map's rosters and pins. A mob the site really does not list opens too, with what your own logs measured and "not listed" for the rest.


## v0.15.1 — 2026-08-16

- **The Mobs tab has folded into the Bestiary.** Everything it showed — what each mob took to kill on your server, at every zone and difficulty tier, with the range and how sure the estimate is — is on the mob's Bestiary page, beside what the world lists for it. One place to read it instead of two.
- **The update dialog now reads its notes properly.** Bold, code and links in these notes show as such rather than as raw `**` marks.

## v0.15.0 — 2026-08-16

- **The Bestiary now opens on something.** It used to sit on "loading…" and an empty page; now it opens on the mobs you have actually killed, most-killed first, with level bands to browse the rest of the world.
- **A mob's page tells you the two things that decide a fight.** Listed health beside what it really took to kill one, and listed damage beside what it really hit you for — from your own logs, on your server, with a plain-English reading of how they compare. The name and level chips are coloured the way a /consider would show them, against a level you can change.
- **Bestiary and Map are joined both ways.** From a mob, jump to any zone it stands in with its spawn points drawn on the map. From a zone, see everyone who stands there — with your kill counts beside them — and open any of them. Every hop leaves a breadcrumb back.
- **Pin mobs to the maps.** Pin any mob and it stays drawn in its own colour on every zone it stands in, and every one of those zones is ringed on the world map. Your pins are kept between sessions.
- **Back and forward.** Arrows beside the EQDeeps name, the mouse's thumb buttons, or Alt+←/→ take you back to the last screen you were actually on — across views, mobs and zones.
- **The World view is no longer bare.** It has the same rail as the zone view: the zone list (a click frames the zone in the world), the mob search — point at a mob and every zone it lives in lights up — and the era chooser, which now trims the zone list too, so "Classic only" is 78 zones and not 570.
- **Zone maps show where everything spawns.** Every listed mob's spawn points are drawn quietly on the zone; point at one in the list and its points light up while the rest step back.
- **Fixed:** zooming the World while a search was typed took several notches to stick; the fit no longer resets on you.

## Earlier

Releases before v0.15.0 are described on their [GitHub release pages](https://github.com/Moonchopper/EQDeeps/releases).
