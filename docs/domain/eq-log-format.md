# EverQuest Log Format — Domain Reference

This is the distilled knowledge of what EverQuest log files contain and how to interpret them. It was extracted from the reference implementation (EQLogParser, `d:\git\EQLogParser`) — chiefly its parsers (`EQLogParser/src/parsing/`) and parser unit tests (`EQLogParser.Wpf.Test/src/parsing/`), which contain hundreds of real captured lines. **When this document is ambiguous, the reference implementation's parsers and tests are the authority.** All example lines below are real.

## 1. Files

- EQ writes one log per character per server into `<EQ install>\Logs\`: `eqlog_<Character>_<server>.txt` (e.g., `eqlog_Kizant_xegony.txt`). Character and server should be parsed from the filename. The strict live-server pattern used for archiving decisions in the reference app: `^eqlog_([a-zA-Z]+)_([a-zA-Z]+)(?!.*\d).*\.txt$` — EMU servers and copies may deviate (`.log`, digits, suffixes), so opening arbitrary files must be tolerated.
- Logging toggles with `/log` in game. The game **appends forever** with a shared write handle: files reach multiple GB. Parsers must open with `FileShare.ReadWrite | FileShare.Delete`.
- Users/tools truncate or rotate logs at will; the game recreates the file and keeps writing. Tailing must survive truncation (`length < last position`) and delete/rename.
- Archived logs are commonly gzipped; reading `.gz` transparently is expected.
- Encoding is ASCII/ANSI in practice; treat as single-byte, tolerate stray bytes.

## 2. Line structure

Every line:

```
[Day Mon DD HH:MM:SS YYYY] <message>
```

Example: `[Sun Oct 08 20:07:10 2023] Test tells the guild, 'hello'`

- The timestamp prefix is **fixed-width (27 chars including the trailing space)**: `[` + 24-char ctime-style date + `] `. The message body starts at index 27.
- **Resolution is 1 second.** Many lines share a timestamp; per-second bucketing is the natural finest grain for time series. Consecutive lines usually share timestamps — the reference parser skips re-parsing the date when chars [1..25) match the previous line (a big win; do the same or better).
- Timestamps are **local time** with no zone info, and can go backwards (DST, clock changes). Treat as monotonic-ish; don't crash on regressions.
- Lines shorter than ~28 chars are noise; skip.
- **Glitch: two entries on one physical line.** The game occasionally concatenates a second `[timestamp] ...` entry onto the same line. Detect by probing for `[` + a plausible `]` at the expected offset mid-line and split/recurse.

## 3. Message taxonomy

Everything after the timestamp is one of: **chat**, **combat/game event**, or **noise**. Chat detection should run first (cheap prefix/keyword scan) because chat lines can contain arbitrary player text that would otherwise confuse combat grammars (e.g., someone *saying* "you have been slain").

### 3.1 Chat

Channel grammars (sender first token; "You" for self; the `, in an unknown tongue,` variant can appear in any quoted channel; quoted text may be empty):

| Channel | Example |
|---|---|
| say | `Test says, 'hello'` |
| ooc | `Test says out of character, 'hello'` |
| auction | `Test auctions, 'hello'` |
| shout | `Test shouts, 'hello'` |
| group | `Test tells the group, 'hello'` |
| guild | `Test tells the guild, 'hello'` |
| raid | `Test tells the raid, 'hello'` |
| fellowship | `Test tells the fellowship, 'hello'` |
| tell (received) | `Test tells you, 'hello'` |
| tell (sent) | `You told Test, 'hello'` |
| custom channel | `Test tells Test.test:34, 'hello'` — channel name `test.test`, member number after `:` |
| tell-window echo | `Test -> Test2: hello` |

Notes:
- Senders can be cross-server: `Server.Name` (e.g., `Firiona.Bob tells you, ...` — the character name is the part **after** the dot; the reference parser extracts the post-dot segment as the player name, and its tests assert `Test.test2 tells you` → sender `test2`). Normalize/display accordingly.
- NPC speech uses the same `says, '...'` grammar; `Test says 'My leader is hello'` (no comma) is a **pet leader** line, not chat — see §5.
- Chat lines terminate processing — never fall through to combat parsing.

### 3.2 Melee damage and avoidance

Success:

```
Astralx crushes Sontalak for 126225 points of damage. (Strikethrough Critical)
Useless crushes an abyssal terror for 9022 points of damage.
You crush Ogna, Artisan of War for 20581 points of damage. (Lucky Critical)
Nniki pierces an ice giant for 101810 points of damage. (Critical)
An ice giant bashes Shmid for 39969 points of damage. (Riposte Strikethrough)
Susarrak the Crusader claws Villette for 27699 points of damage. (Strikethrough Wild Rampage)
```

- Verb encodes the skill: hit/slash/crush/pierce/kick/bash/bite/claw/smash/punch/smite/reave/cleave/maul/gore/sting/frenzy/backstab/strike/… First/second person uses the bare verb (`You crush`), third person the s-form (`crushes`). Attacker = everything before the verb; defender = between verb and ` for `; amount before `points of damage`.
- NPC names include articles and get capitalized in subject position: normalize `an abyssal terror` ↔ `An abyssal terror` to one identity. Names may contain commas (`Ogna, Artisan of War`), backticks (`Vulak\`Aerr`), and multiple words — **find the verb, don't split naively on spaces.**

Avoidance (attempt lines):

```
Drogbaa tries to slash Whirlrender Scout, but misses! (Strikethrough)
Test One Hundred Three tries to punch Kazint, but Kazint dodges!
Test One Hundred Three tries to punch YOU, but YOU dodge!
You try to crush a primal guardian, but a primal guardian parries!
An ancient warden tries to hit Reisil, but Reisil blocks with his shield!
A windchill sprite tries to smash YOU, but YOU block with your staff!
Tolzol tries to crush Dendritic Golem, but Dendritic Golem is INVULNERABLE!
A failed reclaimer tries to punch YOU, but YOUR magical skin absorbs the blow!
You try to crush a Kar`Zok soldier, but miss! (Riposte Strikethrough)
```

- Outcomes: miss, dodge, parry, block (with shield/staff/…), riposte, INVULNERABLE, absorb (rune). Each is an "attempt" record with type = the outcome and subtype = the skill (normalized to the s-form, e.g. `Punches`).
- Quirk from the reference tests: a *successful riposte by the defender* (`...but YOU riposte!` / `...but Fllint ripostes!`) is **not** recorded as a damage/attempt record — the riposte's damage arrives as its own hit line tagged `(Riposte)`. Meanwhile `(Riposte)` as a *modifier on a hit* means the hit **was** a riposte; and on an NPC's hit line `(Riposte Strikethrough)` means the NPC struck through the player's riposte — the reference treats the modifier as strikethrough-dominant there (IsRiposte=false, IsStrikethrough=true). Preserve these semantics; they matter for riposte-rate denominators.

### 3.3 Spell direct damage (DD)

```
Sonozen hit Jortreva the Crusader for 38948 points of fire damage by Burst of Flames. (Lucky Critical Twincast)
You hit a treant for 1633489 points of magic damage by Chromospheric Vortex Rk. II. (Lucky Critical)
Piemastaj hit Boss for 176000 points of unresistable damage by Elemental Conversion VI.
```

- Grammar: `<attacker> hit <defender> for <N> points of <school> damage by <Spell Name>.` School ∈ fire/cold/magic/poison/disease/corruption/chromatic/prismatic/unresistable/non-melee. Spell name is the subtype.
- Spell names contain rank suffixes (`Rk. II`, `Rk. III`, roman numerals) — keep the full string as identity but expect UIs to abbreviate.

### 3.4 Damage over time (DoT)

```
Dovhesi has taken 173674 damage from Curse of the Shrine by Grendish the Crusader.
Grendish the Crusader has taken 1003231 damage from Pyre of Klraggek Rk. III by Atvar. (Lucky Critical)
You have taken 4852 damage from Nectar of Misery by Commander Gartik.
A gnoll has taken 108790 damage from your Mind Coil Rk. II.
```

- Grammar: `<defender> has taken <N> damage from <Spell> by <attacker>.` The self-cast variant: `...damage from your <Spell>.` (attacker = you).
- Caster-less variants (caster dead/departed): `You have taken 2354 damage from Flashbroil Singe III.` — attacker unknown; the reference attributes to the spell name itself. `Lawlstryke has taken 216717 damage by Wisp Explosion.` — environmental/spell-as-attacker (flag `AttackerIsSpell`).
- Malformed but real: `Goratoar has taken 18724 damage from Slicing Energy by .` (empty attacker) — must not crash.
- Old EMU ordering flips spell and caster: `Pixtt Invi Mal has taken 189 damage from Goanna by Tuyen's Chant of Fire.` (attacker Goanna, spell Tuyen`s…). EMU mode is a parse-time setting.

### 3.5 Damage shields (DS) and other non-melee

```
Tantor is pierced by Tolzol's thorns for 6718 points of non-melee damage.
Honvar is tormented by Reisil's frost for 7809 points of non-melee damage.
A failed reclaimer is pierced by YOUR thorns for 193 points of non-melee damage.
Test One Hundred Three is burned by YOUR flames for 5224 points of non-melee damage.
YOU are chilled to the bone for 2700 points of non-melee damage!
A dendridic shard was chilled to the bone for 410 points of non-melee damage.
Demonstrated Depletion was hit by non-melee for 6734 points of damage.
You were hit by non-melee for 16 damage
```

- `<defender> is <verbed> by <owner>'s <element> for N points of non-melee damage.` = damage shield; attacker = shield owner. Verbs: pierced/burned/tormented/chilled…
- Ownerless forms (`was chilled to the bone`, `hit by non-melee`) have unknown attacker (falling damage, environment). Note the last example lacks `points of` and the period — grammar sloppiness is normal.

### 3.6 Modifiers (the parenthesized suffix)

A trailing `(...)` on damage/heal lines carries space-separated modifiers:

`Critical`, `Lucky`, `Twincast`, `Flurry`, `Riposte`, `Strikethrough`, `Wild Rampage`, `Rampage`, `Assassinate`, `Headshot`, `Slay Undead`, `Finishing Blow`, `Double Bow Shot`, `Locked` (and combinations: `(Lucky Critical Twincast)`, `(Strikethrough Wild Rampage)`, `(Riposte Strikethrough)`).

- Encode as a bitmask on the record. `Lucky` implies critical treatment but is tracked separately (lucky hits are excluded from crit averages — see metrics doc).
- Twincast on a **DoT tick** does not double the way DD does; the metrics doc covers rate math.
- Old-style EMU crits are a **separate preceding line**: `Vorgash scores a critical hit!` / `Arilyn lands a Crippling Blow!(244)` followed by the normal hit line (`Vorgash hits a target for 780 points of damage.`) — requires one-line lookbehind state in EMU mode.
- Special-attack modifiers (Assassinate, Headshot, Slay Undead, Finishing Blow, Bane) also classify the damage for validity filtering (users can exclude them from parses; see metrics doc).

### 3.7 Heals

Real captured examples (comment block in the reference `HealingLineParser`):

```
Fllint healed Foob for 11820 hit points by Blessing of the Ancients III.
Kuvani healed Tolzol over time for 11000 hit points by Spirit of the Wood XXXIV.
Kuvani healed Foob over time for 9409 (11000) hit points by Spirit of the Wood XXXIV.
Foob's promised interposition is fulfilled Foob healed himself for 44238 hit points by Promised Interposition Heal V. (Lucky Critical)
Tolzol healed itself for 548 hit points.
Piemastaj`s pet has been healed for 15000 hit points by Enhanced Theft of Essence Effect X.
Findawenye healed Piemastaj`s pet for 2823 (78079) hit points by Mending Splash Rk. III. (Critical)
Nylenne has been healed over time for 8211 hit points by Roar of the Lion 6.
You have been healed over time for 1063 (8211) hit points by Roar of the Lion 6.
Your ward heals you as it breaks! You healed Niktaza for 8970 (86306) hit points by Healing Ward. (Critical)
```

- Keyword: ` healed ` (or `has/have been healed`). `over time` marks HoT ticks vs direct heals.
- **Overheal notation:** `for <landed> (<potential>) hit points` — landed amount actually healed; parenthesized is the full roll. Overheal = potential − landed. Absence of parens means fully landed.
- A prefixed flavor sentence can precede the heal on the same line (`Your ward heals you as it breaks! You healed…`, `Rowanoak is soothed by… Farzi healed Rowanoak…`) — find the ` healed ` anchor, don't anchor to line start.
- Healer may be absent (`X has been healed … by <Spell>.`) — heal attributed to spell with unknown healer; `itself/himself/herself` marks self-heals.
- Heal targets can be pets (`Piemastaj\`s pet`).

### 3.8 Deaths

```
You have slain a rockborn!
Kzerk has been slain by Strangle`s pet!
An armed flyer has been slain by Renewingx!
You have been slain by an armed flyer!
Kizante`s pet was slain by a rockborn!
An ice giant died.
```

- `slain by` / `died.` grammars produce death records (who died, killer if stated). `<NPC> died.` closes fights. Note possessive-pet victims (`Strangle\`s pet has been slain`) and that the reference deliberately does **not** emit damage records for slain lines (they're death events, not hits).

### 3.9 Spell casting activity

```
Tolzol begins casting Ardent Elixir Rk. II.
Foob begins to cast a spell. <Spell Name>          (older format)
Sonozen begins singing Aria of Absolution.         (bard songs)
Your Burst of Flames spell is interrupted.
Sonozen's casting is interrupted!
Soandso's spell fizzles!
A ghoul's spell lands on you.                       ("lands on you" — receiving a buff/debuff)
Your Spirit of Wolf spell has worn off.             (wear-off)
```

- "Begins casting/singing" → cast record (who, what). "Lands on you/other" and "has worn off" messages are how received buffs/debuffs are tracked — but these messages carry the spell's *lands-on text*, not its name, so resolution requires the **spell database** (§6): a longest-prefix match from lands-on/wear-off message → candidate spells.
- Zoning: `LOADING, PLEASE WAIT...` and `Welcome to EverQuest!` mark zone transitions (used to close fights, trigger archive points, and split sessions). `You have entered <Zone Name>.` names the zone.

### 3.10 Taunts

```
Goodurden has captured liquid shadow's attention!
You capture a slithering adder's attention!
Foob failed to taunt Doomshade.
A war beast is focused on attacking Rorcal due to an improved taunt.
```

Taunt success/failure records (tank analytics), not damage.

### 3.11 Absorbs / runes

```
Fllint's magical skin absorbs the damage of Firethorn's thorns.
YOUR magical skin absorbs the damage of Herald of the Outer Brood's thorns.
The Spellshield absorbed 132 of 162 points of damage        (EMU)
Leela has shielded herself from 658 points of damage. (Manaskin)
Gaber (Owner: Claus) has shielded itself from 116 points of damage. (Rune II)
```

Absorb records: zero-damage attempts against the defender (counts toward defensive stats). Note the EMU pet-owner annotation `(Owner: Claus)` — also appears on EMU damage lines: `Lobekn (Owner: Bulron) hit a wan ghoul knight for 311 points of non-melee damage. (Earthquake)` — parse the owner out for pet attribution.

### 3.12 Misc events worth capturing

- **Loot:** `--You have looted a Cold-Forged Cudgel from Queen Dracnia's corpse.--` (also `<Player> has looted…`); master-looter and "left on corpse/chest" variants; currency splits (`You receive 12 platinum … as your split`).
- **Random rolls:** two-line pairs — `**A Magic Die is rolled by <Player>.` then `**It could have been any number from 0 to 100, but this time it turned up a 87.`
- **Resists:** `Your target resisted the <Spell> spell.` / `<NPC> resisted your <Spell>!` — resist analytics per spell/NPC.
- **Mez break:** `<NPC> has been awakened by <Player>.`
- **/who output:** bracketed roster lines `[60 High Priest] Soandso (High Elf) <Guild Name>` — a rich source for class detection and player verification (also anonymous `[ANONYMOUS] Soandso`).
- **Raid/group membership:** `Soandso has joined the raid.`, `You have joined the group.`, `Soandso is now the leader of your raid.` — identity signals (see §5).
- **Pet leader:** `Gobaber says 'My leader is Piemastaj'` — definitive pet→owner mapping (produced by targeting the pet and using /pet leader).
- **Discipline/activated abilities:** `<Player> activates <Ability>.`
- **Experience:** modern servers log the level-progress delta — `You gain experience! (5.472%)` / `You gain party experience! (1.812%)` — while classic servers only announce `You gain experience!!` (no number). AA points are separate and carry a running total: `You have gained an ability point!  You now have 2 ability points.` Beware `You gain a rune for N points of absorption.`, which shares the `You gain ` prefix but is an absorb line.

## 4. Player self-reference

The log is written from one character's perspective; that character's name never appears in first person. Grammars use `You/YOU/you`, `your/YOUR`, `yourself/himself/herself/itself`. Every record must replace these with the character name derived from the filename. Second-person verb forms differ (`You crush` vs `crushes`) — grammar tables need both.

## 5. Identity: player vs NPC vs pet vs merc

There is no explicit tag on names. The reference implementation's layered heuristics (validated over years) are:

1. **Verified players** — names seen doing player-only things: chatting in player channels, being in your group/raid (`has joined the raid`), appearing in /who output, being the log owner, `Targeted (Player): <name>`. Persist per server; the set grows over time.
2. **Known NPCs** — names in the shipped NPC list (see §6), names with articles (`a`/`an`/`the` prefix), names appearing in `<NPC> died./has been slain` as victims of players, multi-word lowercase names.
3. **Pets** — possessive references (`Piemastaj's pet`), pet-leader lines (definitive), game-generated pet name patterns (e.g., `Xobtik`, `Jobekn` — consonant-pattern names in the shipped petnames list), EMU `(Owner: X)` annotations, and heals/buffs cast on `X's pet`. Map pet → owner for damage attribution; unmapped pets attribute to "Unknown Pet Owner" until resolved.
4. **Mercs** — hireling NPCs on the player's side; detected from merc-specific lines; treated as friendlies (their heals/damage count in raid totals under their own name).
5. **Class detection** — accumulate evidence over time (spells cast are class-specific via the spell DB, /who lines state class, epic weapon procs, discipline use). The reference tracks class *per player per time range* with confidence, because mercs and character swaps mean a name's class can change mid-log. Ambiguity is normal; expose class as best-effort.
6. **Cross-server names** — `Name.Server` appears in chat and occasionally combat; fold into identity handling.

Fights are keyed by NPC name (see metrics doc); misclassifying a player as an NPC creates phantom fights, so the reference deletes any "fight" whose key later becomes a verified player. Plan for the same correction flow.

## 6. Reference data files

The reference app ships plain-text data files (Apache 2.0 — attribution required if copied; they live in `EQLogParser/EQLogParser/data/`):

| File | Contents / purpose |
|---|---|
| `spells.txt` | Full spell DB: name, class availability, lands-on-you / lands-on-other / wear-off message texts, resist type, ranks. Enables: resolving "lands on"/"worn off" messages to spells (longest-prefix match over message-text tries), class inference from casts, proc identification. |
| `oldspells.txt` | Historic spell data for TLP/era logs. |
| `npcs.txt` | Known NPC names (disambiguates player-vs-NPC). |
| `petnames.txt` | Game-generated pet name list. |
| `procs.txt`, `itemspells.txt` | Proc/item-spell names (don't imply caster class). |
| `titles.txt` | Player title strings to strip from /who parsing. |
| `adpsMeter.txt` | Crit/damage-modifying buff catalog (ADPS awareness, P2). |

Copying these (with NOTICE attribution) is far cheaper than regenerating; they update with EQ expansions, so design for easy replacement.

## 7. Hostile-input rule

Chat lines contain arbitrary player-authored text, including text that mimics combat grammars. Beyond classifying chat first, parsers must treat every line as untrusted input: bounded backtracking (or no regex at all on hot paths), length limits, and no throw-on-malformed (empty attacker, missing periods, truncated lines mid-write are all real).
