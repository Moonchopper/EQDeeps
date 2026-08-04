# EverQuest Inventory Dump — Domain Reference

What `/outputfile inventory` writes, and why it is the only way a player's
equipped gear reaches disk. Captured from real EQ Legends output (2026-08);
the reference implementation (EQLogParser) has no inventory handling at all, so
unlike `eq-log-format.md` this document has no prior art behind it — the file
itself is the authority.

## 1. Why this file exists at all

**Nothing else records equipped gear.** Specifically, on EQ Legends:

- **Loadouts are *class* loadouts, not gear sets.** `eqstr_us.txt:16001`
  describes them as the multiclass system, reached from the Inventory window or
  the `n` key. The client stores per-loadout UI and hotbars (hence
  `<Character>_<server>_LO<n>.ini` and `UI_<Character>_<server>_LO<n>.ini`) and
  per-loadout auto-attack skills (`[Combat] AutoSkillsLO0..LO2`), but the
  equipment attached to a loadout lives **server-side**.
  `uifiles/default/EQUI_LoadoutWnd.xml` renders `LOW_Equip_Combo` and
  `LOW_CopyEquip_Button`; `[LoadoutWnd]` in the UI ini is window geometry only.
- **The log says nothing.** A loadout swap emits no system line. There is no
  "you have equipped" message to parse.
- **`[Bandolier]` holds no data either** — only `[BandolierWnd]` window
  positions.
- `userdata\LF_<Character>_<server>.ini` (the loot filter) is the only other
  client-side item table, and it reflects filter choices, not what is worn.

So the dump is it. It is a manual act by the player, which is a real cost, and
the reason the app nudges rather than assumes.

## 2. Producing it

`/outputfile inventory [optional filename]` (`eqstr_us.txt:1898`). With no
filename it writes `<InstallRoot>\<Character>_<server>-Inventory.txt`,
overwriting any previous dump. `/outputfile recipes` documents a 5-minute rate
limit (`eqstr_us.txt:8064`); inventory has none documented, but a reader should
treat "the file did not change" as the normal case and never retry-spam.

The file carries **no timestamp and no character name** — use the file's
last-write time and the filename respectively.

## 3. Layout

Tab-delimited, two sections separated by a blank line.

### 3.1 Slots

Header `Location	Name	ID	Count	Slots`, then one row per slot:

```
Head	Skull-Shaped Barbute +7	4301	1	10
Face	Carved Ivory Mask +2	10144	1	10
Face-Slot7	Carved Ivory Mask (Exaltation)	10144	1	10
Face-Slot8	Guise of the Deceiver (Exaltation)	2469	1	10
Wrist	Pristine Studded Leather Bracer	1881	1	10
Wrist	Silver-Plated Bracer +6	4303	1	10
Wrist-Slot7	Serpentine Bracer (Exaltation)	10148	1	10
General 2	Light Burlap Sack	17353	1	8
General 2-Slot3	Runed Mithril Bracer +1	4406	1	10
General 2-Slot3-Slot7	Runed Mithril Bracer (Exaltation)	4406	1	10
Bank1	Empty	0	0	0
```

- **`-Slot<n>` suffixes are nesting.** One level under an equipment slot is an
  **augment**; one level under a bag is its **contents**; two levels under a bag
  is an augment inside a bagged item. Strip suffixes repeatedly to get the root
  slot and the depth.
- **Equipment is what is left when the containers are removed.** Match by
  exclusion, not by a list of slot names: EQ Legends already ships a generic
  **`Any Slot`**, and a parser that only knows yesterday's slot names silently
  drops today's gear.
- **Containers are the indexed roots, plus `Held` (the cursor).** Known ones
  are `General <n>`, `Bank<n>`, `SharedBank<n>` and `Personal-Depot<n>`, but
  match the *shape* — a trailing digit — rather than the names. Enumerating
  them got this wrong once: a personal depot appeared in a later dump, and its
  twelve tradeskill components (Imp Blood, Star Ruby, Ale…) were recorded as
  worn gear, inflating the equipped set and making every diff around it
  nonsense. No equipment slot in the dump ends in a digit; the list of stores
  is the game's to grow.
- **Slot labels repeat and are not unique.** `Ear`, `Wrist`, `Fingers` come in
  pairs and `Any Slot` appears twice. Their only stable identity is **position
  in the file**, so number each root slot by order of appearance — counting
  bare slots too, or "the second Wrist" changes number the moment the first is
  emptied.
- **Augments follow their parent immediately**, so the most recent depth-0 row
  is the one they belong to.
- `Empty` with ID `0` is a placeholder for a bare slot — present so the slot is
  still counted, but not an item.
- The `Slots` column is capacity: bag size for containers (8), augment sockets
  for items (10). It is *not* the number of augments actually fitted.

### 3.2 Names

- **`+N` is the EQ Legends upgrade level**, carried inside the name:
  `Short Sword of the Ykesha +5`. Split it out — a `+2` and a `+5` of the same
  sword are the same item to the player and a different item to their parse,
  and only the split makes a diff read "+2 → +5" rather than "one item removed,
  another added".
- **`(Exaltation)`** marks the augment form of an item. Its ID matches the
  non-augment version (`Carved Ivory Mask` and `Carved Ivory Mask (Exaltation)`
  are both `10144`), so **name, not ID, distinguishes them**.
- **`*`** appears on some names (`Backpack*`, `Bandages*`). Meaning unknown —
  preserve it verbatim rather than stripping something that may be significant.

### 3.3 KeyRing

After a blank line, header `KeyRing	Name	ID`, rows categorised `Augmentation`
or `Equipment`:

```
Augmentation	Moonstone Ring (Exaltation)	10150
Equipment	Dark Reaver +4	5404
```

This is the pool a character's loadouts draw from — owned gear not currently
worn. Parsed and stored, but not yet used for anything.

## 4. Reading it defensively

Same contract as the log parsers: **total, never throwing.** Ragged rows,
missing trailing columns, an absent KeyRing section, or an unrelated file
should yield less data rather than an exception. A file with no equipped rows
at all yields *no snapshot* rather than an empty one — "this player wore
nothing" is a claim, and the absence of data is not evidence for it.

## 5. What a snapshot means

A dump proves the gear at the instant it was written and says nothing about any
earlier moment. See `docs/architecture/adr-011-gear-snapshots.md` for why
attribution is forward-only.
