# EQ Legends: class loadouts, and what the client tells you about them

**One character is several characters.** EQ Legends' multiclass system lets a
character carry several **class loadouts**, and each one levels independently.
Swapping between them produces **no log line and no client-side record**.

This page exists because that fact has bitten this project twice — once by
hiding two thirds of a user's data behind a "my level" filter
([ADR-013](../architecture/adr-013-incoming-damage.md)), once by making gear
untrustworthy enough to withdraw
([ADR-011](../architecture/adr-011-gear-snapshots.md)). Anything that reads a
level, a class, or an item on this server has to start here.

Every claim below was measured against a real 690,000-line log
(`Moonchopper`/`qeynos`, 29 Jul – 9 Aug 2026) and the client install, not
inferred.

---

## 1. Each loadout levels on its own

A character's level is not a number. It is one number *per loadout*, and the
log's `Welcome to level N!` announces whichever is active. Read as a single
series it looks like nonsense:

```
07-29 19:45  level 12       07-29 21:53  level 11   ← swap
07-29 20:13  level 13       07-29 22:24  level 14   ← swapped back, +1 on the first
...
08-02 12:49  level 41       08-02 14:15  level 11   ← swap, 30 levels down
08-02 15:03  level 12       08-02 15:31  level 11   ← a *third* loadout
08-02 18:49  level 17/18
08-03 20:14  /who says 44   ← back on the first
```

Read as three interleaved series it is perfectly ordinary: each loadout climbs
by one at a time, and the jumps are the swaps. On 08-06 that character played at
levels **19, 20 and 48** in one day; on 08-09 at **12, 49 and 50**.

**A ding is monotonic within a loadout.** So any step that is not `+1` — down at
all, or up by more than one — proves a swap happened. It does not say *which*
loadout is now active.

## 2. Nothing announces a swap

- **No system message.** Grepping all 690,000 lines for `loadout`
  (case-insensitive) returns only players typing the word in chat.
- **No equip message.** Equipping an item is equally silent.
- **No race message** — see §3, where the character's race changes between two
  `/who` lines with nothing in the log to mark it.

The practical consequence: **the active loadout at any instant is unknowable.**
"The level right now" is the last level *announced*, and after a swap that is
the loadout that was put away, until the new one dings or the player types
`/who`.

## 3. What `/who` does say

A self-`/who` is the one line that observes rather than announces, and it is
richer than the single-class form the base game uses:

```
[31 PAL/MNK/BER] Moonchopper (Dwarf) <Faceless> ZONE: North Kaladim (kaladimb)
[40 PAL/MNK/BER] Moonchopper (Troll) <Faceless> ZONE: The Ruins of Old Guk 32514 (gukbottom)
[44 PAL/MNK/BER] Moonchopper (Troll) <Faceless> ZONE: Nagafen's Lair 18717 (soldungb)
```

- **The level is the active loadout's**, and it is trustworthy at that instant.
- **The class field lists every loadout's class**, slash-separated. It is not
  one class. Other characters in the same log show `PAL/DRU`, `PAL/ENC/BER`,
  `PAL/DRU/WIZ`, `PAL/ROG/BER` — so the count varies (two and three both seen)
  and this is where "how many loadouts does this character have" comes from.
  **Anything treating `WhoEvent.ClassText` as a single class is wrong on this
  server** — this matters for the pending class-detection work.
- **The race can differ between two `/who` lines for the same character**
  (`Dwarf` on 08-01, `Troll` on 08-02) with nothing logged in between.

Also note the zone suffix — `The Ruins of Old Guk 32514` — is an instance *id*
here, not the difficulty tier that `You have entered …` carries. Do not feed
`/who` zone text to `InstanceZone.Parse`.

## 4. Client-side files: all checked, none of them help

Recovered from the F24 investigation (the file-format doc was deleted with the
feature; this is the part still worth having):

| Where | What it actually holds |
|---|---|
| `eqstr_us.txt:16001` | Describes loadouts as the multiclass system, reached from the Inventory window or `n` |
| `<Character>_<server>_LO<n>.ini`, `UI_<Character>_<server>_LO<n>.ini` | Per-loadout **UI and hotbars** only |
| `[Combat] AutoSkillsLO0..LO2` | Per-loadout auto-attack skills — the closest thing to a loadout enumeration on disk, but not which is active |
| `uifiles/default/EQUI_LoadoutWnd.xml` | Renders `LOW_Equip_Combo` / `LOW_CopyEquip_Button` — i.e. the **equipment lives server-side** |
| `[LoadoutWnd]` in the UI ini | Window geometry |
| `[Bandolier]` | Window positions only, no data |
| `userdata\LF_<Character>_<server>.ini` | Loot filter choices, not what is worn |

So: the client persists a loadout's *interface*, and the server keeps its
*equipment*. There is no file to poll for "which loadout am I on".

## 5. What follows for code

**Do:**

- Treat level as a per-loadout fact. `DefenderLevels` resolves "the level last
  announced at or before instant t" and that is the best available answer.
- Let the level axis double as a **loadout axis**. It is a good proxy: a
  different loadout is a different class with different mitigation, so its
  numbers genuinely belong in separate rows (F26 keys on it deliberately).
- Read a `/who` backwards over earlier log only as far as you would trust it —
  it observed a level that was already true, but only back to the last swap,
  which is invisible.

**Don't:**

- **Don't reduce a character to one current level.** There isn't one. A filter
  or label that does will silently hide every other loadout — exactly the
  v0.9.3 bug.
- **Don't treat a downward ding as a de-level.** On this server it is
  overwhelmingly a swap. (Genuine de-levels exist and are also unlogged, so the
  two are indistinguishable; the swap reading is the common case.)
- **Don't parse `ClassText` as one class.**
- **Don't build anything on knowing what is equipped.** See ADR-011.

## 6. Open questions, and how to settle them

Neither is answered by the log in hand; both are cheap to test in game.

1. **Does the order in `PAL/MNK/BER` indicate the active loadout, or is it
   fixed?** Every self-`/who` in the log was taken on the same (high-level)
   loadout, so there is no evidence either way. **Test:** swap to a low loadout,
   type `/who`, and see whether the first entry changes. If it tracks the active
   loadout, the swap problem in §2 becomes solvable whenever a player types
   `/who`, and class detection gets much stronger.
2. **Is race per-loadout, or was that a one-off race change?** Observed changing
   Dwarf → Troll with nothing logged. **Test:** `/who` on two different loadouts
   in the same session.

If either is settled, update this page and the ADR that depends on it.
