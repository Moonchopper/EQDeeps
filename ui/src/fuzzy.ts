/**
 * Subsequence fuzzy matching for the table search boxes.
 *
 * Typing is the interaction, so this has to be cheap enough to run over every
 * row on every keystroke: one linear pass per candidate, no pre-built index,
 * no allocation beyond the hit itself. A few thousand loot rows is nothing at
 * that cost, which is why the search filters the rows already in hand instead
 * of going back to the server.
 *
 * Scoring rewards what a person means when they type an abbreviation: a
 * literal substring beats everything, then consecutive letters, then letters
 * that start a word, and reaching for a letter across a long gap costs. So
 * "cfc" finds "Cold-Forged Cudgel". Whitespace splits the query into tokens
 * that may match in any order, so "cudgel cold" finds it too.
 *
 * Equal scores keep the order they came in, which is the server's ranking —
 * so a tie between two equally good readings falls back to the bigger number.
 */

export interface FuzzyHit {
  /** Higher is a better reading of what was typed. Only comparable within one query. */
  score: number;
  /** Ascending indices into the haystack that matched, for highlighting. */
  positions: number[];
}

/** The empty query matches everything at a flat score. */
export function fuzzyMatch(text: string, query: string): FuzzyHit | null {
  const tokens = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (tokens.length === 0) {
    return { score: 0, positions: [] };
  }

  const haystack = text.toLowerCase();
  let score = 0;
  const positions = new Set<number>();
  for (const token of tokens) {
    const hit = matchToken(haystack, token);
    if (!hit) {
      return null;
    }
    score += hit.score;
    for (const p of hit.positions) {
      positions.add(p);
    }
  }
  return { score, positions: [...positions].sort((a, b) => a - b) };
}

function matchToken(haystack: string, token: string): FuzzyHit | null {
  // A literal substring is the strongest possible reading of what was typed,
  // and it beats every scattered subsequence by construction.
  const at = haystack.indexOf(token);
  if (at >= 0) {
    const positions: number[] = [];
    for (let i = 0; i < token.length; i++) {
      positions.push(at + i);
    }
    return { score: 1000 - Math.min(at, 100) + (isWordStart(haystack, at) ? 100 : 0), positions };
  }

  const positions: number[] = [];
  let score = 0;
  let cursor = 0;
  let previous = -2;
  for (let i = 0; i < token.length; i++) {
    const found = haystack.indexOf(token[i], cursor);
    if (found < 0) {
      return null;
    }
    if (found === previous + 1) {
      score += 20;
    }
    if (isWordStart(haystack, found)) {
      score += 20;
    }
    // Skipping a long stretch to reach the next letter is a weaker match.
    score -= Math.min(found - cursor, 10);
    positions.push(found);
    previous = found;
    cursor = found + 1;
  }
  return { score, positions };
}

function isWordStart(haystack: string, index: number): boolean {
  if (index === 0) {
    return true;
  }
  const before = haystack.charCodeAt(index - 1);
  const alphanumeric =
    (before >= 97 && before <= 122) || (before >= 48 && before <= 57);
  return !alphanumeric;
}
