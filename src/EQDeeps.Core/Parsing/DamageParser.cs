using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Damage-stream grammars: melee hits, avoidance attempts, spell direct damage,
/// damage over time, damage shields, and absorbs. All scanning is keyword-anchored
/// ordinal string search — no regex — because every line is untrusted input and
/// grammar sloppiness (missing periods, empty attackers, truncation) is normal.
/// </summary>
public static class DamageParser
{
    /// <summary>
    /// Mutable one-line lookbehind owned by the session's parser instance: old-style
    /// EMU logs announce criticals on a separate preceding line ("X scores a critical
    /// hit!") that applies to X's next melee hit.
    /// </summary>
    public sealed class State
    {
        public string? PendingEmuCritAttacker;
    }

    private static readonly string[] Schools =
    [
        "fire", "cold", "magic", "poison", "disease", "corruption",
        "chromatic", "prismatic", "unresistable", "physical", "non-melee",
    ];

    /// <summary>
    /// Parses a damage-stream line. Returns null with <paramref name="consumed"/> true
    /// for lines this family recognizes but deliberately records nothing for
    /// (successful defender ripostes, EMU critical-announcement lines).
    /// </summary>
    public static DamageEvent? Parse(string action, ParserOptions options, State state, out bool consumed)
    {
        consumed = false;
        if (action.Length < 10)
        {
            return null;
        }

        // EMU old-style crit announcements: remember the attacker, emit nothing.
        if (options.EmuMode && TryEmuCritAnnouncement(action, state))
        {
            consumed = true;
            return null;
        }

        // Split off a trailing "(...)"; known modifiers become flags, anything
        // else (EMU spell names like "(Earthquake)", rune names) is kept raw.
        var body = action;
        var modifiers = HitModifiers.None;
        string? trailingParen = null;
        if (body.Length > 0 && body[^1] == ')')
        {
            var open = body.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0)
            {
                var inner = body[(open + 2)..^1];
                if (ModifierParser.TryParse(inner, out modifiers))
                {
                    body = body[..open];
                }
                else
                {
                    trailingParen = inner;
                    modifiers = HitModifiers.None;
                    body = body[..open];
                }
            }
        }

        // EMU pet-owner annotation: "<pet> (Owner: <player>) <rest of line>".
        string? ownerSubject = null;
        string? ownerName = null;
        var ownerAt = body.IndexOf(" (Owner: ", StringComparison.Ordinal);
        if (ownerAt > 0)
        {
            var close = body.IndexOf(')', ownerAt);
            if (close > 0)
            {
                ownerSubject = body[..ownerAt];
                ownerName = body[(ownerAt + 9)..close];
                body = ownerSubject + body[(close + 1)..];
            }
        }

        var evt = ParseBody(body, trailingParen, modifiers, options, state, ref consumed);
        if (evt is not null)
        {
            consumed = true;
            if (ownerName is not null && ownerSubject is not null)
            {
                var subject = Names.CapitalizeFirst(ownerSubject);
                if (evt.Attacker == subject)
                {
                    evt = evt with { AttackerOwner = ownerName };
                }
                else if (evt.Defender == subject)
                {
                    evt = evt with { DefenderOwner = ownerName };
                }
            }
        }

        return evt;
    }

    private static DamageEvent? ParseBody(
        string body, string? trailingParen, HitModifiers modifiers,
        ParserOptions options, State state, ref bool consumed)
    {
        if (body.IndexOf(" tries to ", StringComparison.Ordinal) > 0 ||
            body.StartsWith("You try to ", StringComparison.Ordinal))
        {
            return ParseAvoidance(body, modifiers, options, ref consumed);
        }

        if (body.IndexOf(" magical skin absorbs the damage of ", StringComparison.Ordinal) > 0)
        {
            return ParseSkinAbsorb(body, modifiers, options);
        }

        if (TryParseShieldAbsorb(body, options, out var shieldEvt))
        {
            return shieldEvt;
        }

        if (TryParseTaken(body, modifiers, options, out var takenEvt))
        {
            return takenEvt;
        }

        if (body.Contains(" points of ", StringComparison.Ordinal) ||
            body.Contains(" by non-melee for ", StringComparison.Ordinal))
        {
            return ParseHit(body, trailingParen, modifiers, options, state);
        }

        return null;
    }

    // ---- avoidance ---------------------------------------------------------

    private static DamageEvent? ParseAvoidance(
        string body, HitModifiers modifiers, ParserOptions options, ref bool consumed)
    {
        string attacker;
        int restStart;
        if (body.StartsWith("You try to ", StringComparison.Ordinal))
        {
            attacker = options.PlayerName;
            restStart = "You try to ".Length;
        }
        else
        {
            var i = body.IndexOf(" tries to ", StringComparison.Ordinal);
            attacker = Names.CapitalizeFirst(body[..i]);
            restStart = i + " tries to ".Length;
        }

        var verbEnd = body.IndexOf(' ', restStart);
        if (verbEnd < 0)
        {
            return null;
        }

        var subType = MeleeVerbs.SubTypeOf(body[restStart..verbEnd]);
        if (subType is null)
        {
            return null;
        }

        var comma = body.IndexOf(", but ", verbEnd, StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        var defender = Names.Resolve(body[(verbEnd + 1)..comma], options);
        var outcome = body.AsSpan(comma + ", but ".Length);

        // A successful riposte by the defender is not an attempt record; the
        // riposte's damage arrives on its own hit line tagged (Riposte).
        if (outcome.Contains("riposte", StringComparison.Ordinal))
        {
            consumed = true;
            return null;
        }

        DamageKind kind;
        if (outcome.Contains("dodge", StringComparison.Ordinal))
        {
            kind = DamageKind.Dodge;
        }
        else if (outcome.Contains("parr", StringComparison.Ordinal))
        {
            kind = DamageKind.Parry;
        }
        else if (outcome.Contains("block", StringComparison.Ordinal))
        {
            kind = DamageKind.Block;
        }
        else if (outcome.Contains("INVULNERABLE", StringComparison.Ordinal))
        {
            kind = DamageKind.Invulnerable;
        }
        else if (outcome.Contains("absorbs the blow", StringComparison.Ordinal))
        {
            kind = DamageKind.Absorb;
        }
        else if (outcome.Contains("miss", StringComparison.Ordinal))
        {
            kind = DamageKind.Miss;
        }
        else
        {
            return null;
        }

        return new DamageEvent(attacker, defender, 0, kind, subType, modifiers);
    }

    // ---- absorbs -----------------------------------------------------------

    private static DamageEvent? ParseSkinAbsorb(string body, HitModifiers modifiers, ParserOptions options)
    {
        // "<defender>'s magical skin absorbs the damage of <attacker>'s thorns."
        const string Anchor = " magical skin absorbs the damage of ";
        var at = body.IndexOf(Anchor, StringComparison.Ordinal);
        var defenderPart = body[..at];
        string defender;
        if (Names.IsYour(defenderPart))
        {
            defender = options.PlayerName;
        }
        else if (defenderPart.EndsWith("'s", StringComparison.Ordinal))
        {
            defender = defenderPart[..^2];
        }
        else
        {
            return null;
        }

        var source = body[(at + Anchor.Length)..];
        var poss = source.LastIndexOf("'s ", StringComparison.Ordinal);
        string? attacker = poss > 0 ? Names.CapitalizeFirst(source[..poss]) : null;
        return new DamageEvent(attacker, defender, 0, DamageKind.Absorb, null, modifiers);
    }

    private static bool TryParseShieldAbsorb(string body, ParserOptions options, out DamageEvent? evt)
    {
        evt = null;

        // EMU: "The Spellshield absorbed 132 of 162 points of damage"
        if (body.StartsWith("The Spellshield absorbed ", StringComparison.Ordinal))
        {
            evt = new DamageEvent(null, options.PlayerName, 0, DamageKind.Absorb, null);
            return true;
        }

        // "Leela has shielded herself from 658 points of damage."
        var i = body.IndexOf(" has shielded ", StringComparison.Ordinal);
        if (i > 0 && body.Contains(" from ", StringComparison.Ordinal) &&
            body.Contains(" points of damage", StringComparison.Ordinal))
        {
            evt = new DamageEvent(null, Names.Resolve(body[..i], options), 0, DamageKind.Absorb, null);
            return true;
        }

        return false;
    }

    // ---- "has taken" (DoT / environmental) ---------------------------------

    private static bool TryParseTaken(string body, HitModifiers modifiers, ParserOptions options, out DamageEvent? evt)
    {
        evt = null;
        string defender;
        int amountStart;

        var i = body.IndexOf(" has taken ", StringComparison.Ordinal);
        if (i > 0)
        {
            defender = Names.Resolve(body[..i], options);
            amountStart = i + " has taken ".Length;
        }
        else if ((i = body.IndexOf(" have taken ", StringComparison.Ordinal)) > 0 &&
                 body.AsSpan(0, i).EndsWith("You", StringComparison.Ordinal))
        {
            // Covers both "You have taken ..." and the EMU immolation form where
            // a flavor sentence precedes: "You are immolated by X.  You have taken ..."
            defender = options.PlayerName;
            amountStart = i + " have taken ".Length;
        }
        else
        {
            return false;
        }

        if (!TryReadAmount(body, amountStart, out var amount, out var afterAmount))
        {
            return false;
        }

        var rest = body.AsSpan(afterAmount);

        // EMU generic: "... You have taken 179 points of damage."
        if (rest.StartsWith(" points of damage", StringComparison.Ordinal))
        {
            evt = new DamageEvent(null, defender, amount, DamageKind.DamageOverTime, null, modifiers);
            return true;
        }

        if (!rest.StartsWith(" damage", StringComparison.Ordinal))
        {
            return false;
        }

        rest = rest[" damage".Length..];

        if (rest.StartsWith(" from your ", StringComparison.Ordinal))
        {
            var spell = TrimSentence(rest[" from your ".Length..]);
            evt = new DamageEvent(options.PlayerName, defender, amount, DamageKind.DamageOverTime, spell, modifiers);
            return true;
        }

        if (rest.StartsWith(" from ", StringComparison.Ordinal))
        {
            var seg = rest[" from ".Length..];
            var by = seg.LastIndexOf(" by ", StringComparison.Ordinal);
            if (by < 0)
            {
                // "You have taken 2354 damage from Flashbroil Singe III." — caster
                // gone; the incumbent attributes the damage to the spell name.
                var spell = TrimSentence(seg);
                evt = new DamageEvent(spell, defender, amount, DamageKind.Other, spell, modifiers);
                return true;
            }

            var first = seg[..by].ToString();
            var second = TrimSentence(seg[(by + " by ".Length)..]);

            // Live order is "from <spell> by <attacker>"; old EMU logs flip it.
            var (spellName, attackerName) = options.EmuMode ? (second, first) : (first, second);
            string? attacker = attackerName.Length == 0 ? null : Names.Resolve(attackerName, options);
            evt = new DamageEvent(attacker, defender, amount, DamageKind.DamageOverTime, spellName, modifiers);
            return true;
        }

        if (rest.StartsWith(" by ", StringComparison.Ordinal))
        {
            // "Lawlstryke has taken 216717 damage by Wisp Explosion." — the spell
            // itself is the attacker (environmental / caster unknown).
            var spell = TrimSentence(rest[" by ".Length..]);
            evt = new DamageEvent(spell, defender, amount, DamageKind.Other, spell, modifiers, AttackerIsSpell: true);
            return true;
        }

        return false;
    }

    // ---- hits: melee, spell DD, damage shields -----------------------------

    private static DamageEvent? ParseHit(
        string body, string? trailingParen, HitModifiers modifiers, ParserOptions options, State state)
    {
        // "<defender> was hit by non-melee for 6734 points of damage." /
        // "You were hit by non-melee for 16 damage" (falling damage etc.)
        var nmAt = body.IndexOf(" hit by non-melee for ", StringComparison.Ordinal);
        if (nmAt > 0)
        {
            var subject = body[..nmAt];
            if (subject.EndsWith(" was", StringComparison.Ordinal))
            {
                subject = subject[..^4];
            }
            else if (subject.EndsWith(" were", StringComparison.Ordinal))
            {
                subject = subject[..^5];
            }

            if (!TryReadAmount(body, nmAt + " hit by non-melee for ".Length, out var nmAmount, out _))
            {
                return null;
            }

            return new DamageEvent(null, Names.Resolve(subject, options), nmAmount, DamageKind.DirectDamage, null, modifiers);
        }

        const string PointsOf = " points of ";
        var poAt = body.IndexOf(PointsOf, StringComparison.Ordinal);
        if (poAt <= 0)
        {
            return null;
        }

        var forAt = body.LastIndexOf(" for ", poAt, StringComparison.Ordinal);
        if (forAt < 0 ||
            !TryReadAmount(body, forAt + " for ".Length, out var amount, out var afterAmount) ||
            afterAmount != poAt)
        {
            return null;
        }

        var tail = body.AsSpan(poAt + PointsOf.Length);
        var left = body[..forAt];

        if (tail.StartsWith("damage", StringComparison.Ordinal))
        {
            return ParseMelee(left, amount, modifiers, options, state);
        }

        // "<school> damage[ by <spell>]"
        var dmgAt = tail.IndexOf(" damage", StringComparison.Ordinal);
        if (dmgAt <= 0)
        {
            return null;
        }

        var school = tail[..dmgAt].ToString();
        if (Array.IndexOf(Schools, school) < 0)
        {
            return null;
        }

        var afterDamage = tail[(dmgAt + " damage".Length)..];
        string? spell = null;
        if (afterDamage.StartsWith(" by ", StringComparison.Ordinal))
        {
            spell = TrimSentence(afterDamage[" by ".Length..]);
        }
        else if (school == "non-melee")
        {
            // Damage-shield grammar: "<defender> is pierced by <owner>'s thorns for N ..."
            var ds = ParseDamageShield(left, amount, modifiers, options);
            if (ds is not null)
            {
                return ds;
            }

            // Otherwise an EMU/proc DD line; a trailing "(<Spell>)" names the spell.
            spell = trailingParen;
        }

        // Spell DD: "<attacker> hit <defender> for ..." — "hit" is invariant here.
        string attackerPart;
        string defenderPart;
        if (left.StartsWith("You hit ", StringComparison.Ordinal))
        {
            attackerPart = "You";
            defenderPart = left["You hit ".Length..];
        }
        else
        {
            var hitAt = left.IndexOf(" hit ", StringComparison.Ordinal);
            if (hitAt <= 0)
            {
                return null;
            }

            attackerPart = left[..hitAt];
            defenderPart = left[(hitAt + " hit ".Length)..];
        }

        if (defenderPart.Length == 0)
        {
            return null;
        }

        return new DamageEvent(
            Names.Resolve(attackerPart, options),
            Names.Resolve(defenderPart, options),
            amount,
            DamageKind.DirectDamage,
            spell,
            modifiers);
    }

    private static DamageEvent? ParseMelee(
        string left, uint amount, HitModifiers modifiers, ParserOptions options, State state)
    {
        string attacker;
        string? subType;
        string defenderPart;

        if (left.StartsWith("You ", StringComparison.Ordinal))
        {
            var verbEnd = left.IndexOf(' ', 4);
            if (verbEnd < 0)
            {
                return null;
            }

            subType = MeleeVerbs.SubTypeOf(left[4..verbEnd]);
            if (subType is null)
            {
                return null;
            }

            attacker = options.PlayerName;
            defenderPart = left[(verbEnd + 1)..];
        }
        else
        {
            // Scan for the third-person s-form verb; names may contain commas and
            // multiple words, so never split naively on spaces.
            var found = -1;
            var verbLen = 0;
            var wordStart = 0;
            for (var i = 0; i <= left.Length; i++)
            {
                if (i == left.Length || left[i] == ' ')
                {
                    if (wordStart > 0 && i > wordStart && MeleeVerbs.IsSForm(left.AsSpan(wordStart, i - wordStart)))
                    {
                        found = wordStart;
                        verbLen = i - wordStart;
                        break;
                    }

                    wordStart = i + 1;
                }
            }

            if (found < 0 || found + verbLen >= left.Length)
            {
                return null;
            }

            attacker = Names.CapitalizeFirst(left[..(found - 1)]);
            subType = MeleeVerbs.SubTypeOf(left.Substring(found, verbLen));
            defenderPart = left[(found + verbLen + 1)..];
        }

        // Frenzy phrases as "frenzies on <defender>".
        if (subType == "Frenzies" && defenderPart.StartsWith("on ", StringComparison.Ordinal))
        {
            defenderPart = defenderPart[3..];
        }

        if (defenderPart.Length == 0)
        {
            return null;
        }

        // Old-style EMU crit announced on the preceding line.
        if (options.EmuMode && state.PendingEmuCritAttacker == attacker)
        {
            modifiers |= HitModifiers.Critical;
            state.PendingEmuCritAttacker = null;
        }

        return new DamageEvent(
            attacker, Names.Resolve(defenderPart, options), amount, DamageKind.Melee, subType, modifiers);
    }

    private static DamageEvent? ParseDamageShield(
        string left, uint amount, HitModifiers modifiers, ParserOptions options)
    {
        // With owner:    "<defender> is pierced by <owner>'s thorns"
        // Ownerless:     "YOU are chilled to the bone" / "<defender> was chilled ..."
        var verbAt = FindLinkingVerb(left, out var linkLen);
        if (verbAt <= 0)
        {
            return null;
        }

        var defender = Names.Resolve(left[..verbAt], options);
        var clause = left.AsSpan(verbAt + linkLen);

        var byAt = clause.LastIndexOf(" by ", StringComparison.Ordinal);
        if (byAt < 0)
        {
            return new DamageEvent(null, defender, amount, DamageKind.DamageShield, null, modifiers);
        }

        var ownerPart = clause[(byAt + " by ".Length)..];
        string? attacker;
        var spaceAt = ownerPart.IndexOf(' ');
        var firstWord = spaceAt < 0 ? ownerPart : ownerPart[..spaceAt];
        if (Names.IsYour(firstWord))
        {
            attacker = options.PlayerName;
        }
        else
        {
            var poss = ownerPart.LastIndexOf("'s ", StringComparison.Ordinal);
            attacker = poss > 0 ? Names.CapitalizeFirst(ownerPart[..poss].ToString()) : null;
        }

        return new DamageEvent(attacker, defender, amount, DamageKind.DamageShield, null, modifiers);
    }

    private static int FindLinkingVerb(string text, out int length)
    {
        foreach (var verb in (ReadOnlySpan<string>)[" is ", " are ", " was ", " were "])
        {
            var at = text.IndexOf(verb, StringComparison.Ordinal);
            if (at > 0)
            {
                length = verb.Length;
                return at;
            }
        }

        length = 0;
        return -1;
    }

    // ---- shared helpers ----------------------------------------------------

    private static bool TryEmuCritAnnouncement(string action, State state)
    {
        var scores = action.IndexOf(" scores a critical hit!", StringComparison.Ordinal);
        if (scores > 0)
        {
            state.PendingEmuCritAttacker = Names.CapitalizeFirst(action[..scores]);
            return true;
        }

        var lands = action.IndexOf(" lands a Crippling Blow!", StringComparison.Ordinal);
        if (lands > 0)
        {
            state.PendingEmuCritAttacker = Names.CapitalizeFirst(action[..lands]);
            return true;
        }

        return false;
    }

    private static bool TryReadAmount(string text, int start, out uint amount, out int end)
    {
        amount = 0;
        end = start;
        ulong value = 0;
        var i = start;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            value = value * 10 + (uint)(text[i] - '0');
            if (value > uint.MaxValue)
            {
                return false;
            }

            i++;
        }

        if (i == start)
        {
            return false;
        }

        amount = (uint)value;
        end = i;
        return true;
    }

    private static string TrimSentence(ReadOnlySpan<char> text)
    {
        text = text.TrimEnd();
        if (text.Length > 0 && (text[^1] == '.' || text[^1] == '!'))
        {
            text = text[..^1];
        }

        return text.ToString();
    }
}
