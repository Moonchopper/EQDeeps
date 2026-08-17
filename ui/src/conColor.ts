/**
 * How a mob "cons" to a player of a given level: the six colours the game
 * paints a /consider in, from green (trivial) to red (do not).
 *
 * The bands are the classic ones. Yellow and red are fixed offsets — one or
 * two levels above you is yellow, three or more is red — while the green and
 * light-blue boundaries below you widen as you level, so the table is keyed
 * on the player's level. This is the widely reproduced transcription of the
 * client's behaviour (the emulator community's, checked against play); it is
 * used here as a reading aid on the Bestiary, not as a measurement, and a
 * boundary a level off in either direction changes nothing that is counted.
 * The one thing it does not do is guess your level: on EQ Legends a character
 * carries several (docs/domain/eq-legends-loadouts.md), so the level is
 * whatever the caller was told, and the caller says which.
 */
export type Con = "green" | "lightblue" | "blue" | "white" | "yellow" | "red";

/**
 * Per player-level ceiling: the largest (most negative) level difference that
 * still cons blue, and the one that still cons light blue; anything below the
 * second is green. Rows are inclusive upper bounds on the player's level.
 */
const BELOW: ReadonlyArray<readonly [ceiling: number, green: number, lightBlue: number | null]> = [
  [8, -4, null], // no light blue this low: green or blue
  [9, -6, -4],
  [13, -7, -5],
  [15, -7, -5],
  [17, -8, -6],
  [21, -9, -7],
  [25, -10, -8],
  [29, -11, -9],
  [31, -12, -9],
  [33, -13, -10],
  [37, -14, -11],
  [41, -16, -12],
  [45, -17, -13],
  [49, -18, -14],
  [53, -19, -15],
  [55, -20, -15],
  [57, -21, -16],
  [59, -22, -16],
  [61, -23, -17],
  [63, -24, -17],
  [65, -25, -18],
  [67, -26, -18],
  [69, -27, -19],
  [Infinity, -28, -19],
];

export function conOf(playerLevel: number, mobLevel: number): Con {
  const diff = mobLevel - playerLevel;
  if (diff === 0) return "white";
  if (diff >= 3) return "red";
  if (diff > 0) return "yellow";

  const row = BELOW.find(([ceiling]) => playerLevel <= ceiling) ?? BELOW[BELOW.length - 1];
  const [, green, lightBlue] = row;
  if (diff <= green) return "green";
  if (lightBlue !== null && diff <= lightBlue) return "lightblue";
  return "blue";
}

/** The word a player would use for the colour. */
export const CON_WORD: Record<Con, string> = {
  green: "green",
  lightblue: "light blue",
  blue: "blue",
  white: "even",
  yellow: "yellow",
  red: "red",
};
