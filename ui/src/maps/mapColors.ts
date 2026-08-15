/**
 * EverQuest's map files were coloured for the client's light background. The
 * darkest lines in the corpus are `64,64,64`, which on this app's `--page`
 * (#0f0d0b) is very nearly invisible.
 *
 * So colours are lifted at draw time. The file is never rewritten — the player
 * edits these maps themselves, and an app that "fixed" their colours on disk
 * would be changing their work to suit its own theme.
 */

/** Below this chroma a colour is treated as grey and put on the theme's ramp. */
const GREY_CHROMA = 24;

/**
 * Minimum lightness on dark. Chosen against `--border` (#4d453d, 1.70:1) —
 * map ink has to sit clearly above the app's own rules or the drawing reads as
 * chrome rather than content.
 */
const MIN_LIGHT = 0.44;

interface Hsl {
  h: number;
  s: number;
  l: number;
}

function toHsl(r: number, g: number, b: number): Hsl {
  const rf = r / 255;
  const gf = g / 255;
  const bf = b / 255;
  const max = Math.max(rf, gf, bf);
  const min = Math.min(rf, gf, bf);
  const l = (max + min) / 2;
  const d = max - min;

  if (d === 0) {
    return { h: 0, s: 0, l };
  }

  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h: number;

  if (max === rf) {
    h = ((gf - bf) / d + (gf < bf ? 6 : 0)) / 6;
  } else if (max === gf) {
    h = ((bf - rf) / d + 2) / 6;
  } else {
    h = ((rf - gf) / d + 4) / 6;
  }

  return { h, s, l };
}

function hueToRgb(p: number, q: number, t: number): number {
  let x = t;
  if (x < 0) x += 1;
  if (x > 1) x -= 1;
  if (x < 1 / 6) return p + (q - p) * 6 * x;
  if (x < 1 / 2) return q;
  if (x < 2 / 3) return p + (q - p) * (2 / 3 - x) * 6;
  return p;
}

function fromHsl({ h, s, l }: Hsl): string {
  if (s === 0) {
    const v = Math.round(l * 255);
    return `rgb(${v},${v},${v})`;
  }

  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;

  return (
    `rgb(${Math.round(hueToRgb(p, q, h + 1 / 3) * 255)},` +
    `${Math.round(hueToRgb(p, q, h) * 255)},` +
    `${Math.round(hueToRgb(p, q, h - 1 / 3) * 255)})`
  );
}

/**
 * The colour to draw a map line in.
 *
 * Hue and saturation are kept, because mapmakers use colour to mean something —
 * red for zone lines, green for water, and a palette the player has learned to
 * read. Only lightness moves, and only upward, so the drawing stays recognisably
 * itself while becoming visible on a dark page.
 *
 * Greys are pushed a little further than colours: a grey wall carries no meaning
 * in its hue and is nearly always the structure the player is trying to see.
 */
export function forDarkBackground(r: number, g: number, b: number): string {
  const chroma = Math.max(r, g, b) - Math.min(r, g, b);
  const hsl = toHsl(r, g, b);
  const floor = chroma < GREY_CHROMA ? MIN_LIGHT + 0.06 : MIN_LIGHT;

  return hsl.l >= floor ? fromHsl(hsl) : fromHsl({ ...hsl, l: floor });
}

export function asIs(r: number, g: number, b: number): string {
  return `rgb(${r},${g},${b})`;
}

/** Memoised, because a map has few distinct colours and many segments. */
export function colorCache(lift: boolean): (r: number, g: number, b: number) => string {
  const cache = new Map<number, string>();

  return (r, g, b) => {
    const key = (r << 16) | (g << 8) | b;
    let hit = cache.get(key);
    if (hit === undefined) {
      hit = lift ? forDarkBackground(r, g, b) : asIs(r, g, b);
      cache.set(key, hit);
    }
    return hit;
  };
}
