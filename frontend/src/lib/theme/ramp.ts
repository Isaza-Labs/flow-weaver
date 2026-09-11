// Turns ONE base colour into the 11-shade palette Skeleton/Tailwind expect
// (50…950 plus the contrast tokens), so a theme editor can be a row of colour
// pickers instead of a 77-field form.
//
// The interpolation runs in OKLCh, not HSL. HSL's lightness is not
// perceptual: sliding it on a blue and on a yellow produces ramps that read
// as completely different strengths, and mid-tones drift muddy. OKLCh keeps
// hue and perceived chroma stable while only lightness moves, which is what
// makes a generated ramp look like a designed one.
//
// Three properties worth relying on:
//   - the base colour appears verbatim in the output. Someone who picks
//     #5c69ab must find #5c69ab in the ramp, or the picker feels broken.
//   - it lands in the shade its own lightness belongs to (`anchorFor`), not
//     always 500. Forcing a near-black base into the 500 slot puts it between
//     two much lighter shades and the ramp stops being a ramp — which is
//     exactly what a dark `surface` base does, the most likely thing anyone
//     picks. Anchoring keeps the ramp monotonic for every input.
//   - every shade is inside the sRGB gamut. Saturated hues can't hold their
//     chroma at the ends of the ramp, so chroma is tapered and then reduced
//     until the colour actually fits (see `toGamut`).

export const SHADES = [50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950] as const;
export type Shade = (typeof SHADES)[number];

// Target OKLab lightness per shade. Strictly decreasing, which is what makes
// the anchoring below safe: a base whose lightness is nearest to slot k
// necessarily falls between slot k-1 and k+1, so substituting it never
// reorders the ramp.
const LIGHTNESS: Record<Shade, number> = {
  50: 0.965,
  100: 0.925,
  200: 0.86,
  300: 0.79,
  400: 0.705,
  500: 0.62,
  600: 0.545,
  700: 0.47,
  800: 0.4,
  900: 0.33,
  950: 0.26,
};

// Chroma multiplier per shade. Near-white and near-black cannot carry the
// chroma of a mid-tone — holding it flat makes the light end look bleached
// and the dark end muddy.
const CHROMA_SCALE: Record<Shade, number> = {
  50: 0.28,
  100: 0.42,
  200: 0.6,
  300: 0.78,
  400: 0.92,
  500: 1,
  600: 0.98,
  700: 0.92,
  800: 0.82,
  900: 0.7,
  950: 0.58,
};

export interface Rgb {
  r: number;
  g: number;
  b: number;
}

// ─── sRGB ↔ OKLCh ──────────────────────────────────────────────────────

export function parseHex(hex: string): Rgb | null {
  const m = /^#([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  return { r: ((n >> 16) & 255) / 255, g: ((n >> 8) & 255) / 255, b: (n & 255) / 255 };
}

function toHex({ r, g, b }: Rgb): string {
  const ch = (v: number) =>
    Math.round(Math.min(1, Math.max(0, v)) * 255)
      .toString(16)
      .padStart(2, '0');
  return `#${ch(r)}${ch(g)}${ch(b)}`;
}

const toLinear = (c: number) => (c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
const toSrgb = (c: number) => (c <= 0.0031308 ? c * 12.92 : 1.055 * Math.pow(c, 1 / 2.4) - 0.055);

interface Oklch {
  l: number;
  c: number;
  h: number;
}

// Björn Ottosson's OKLab matrices.
function rgbToOklch({ r, g, b }: Rgb): Oklch {
  const lr = toLinear(r);
  const lg = toLinear(g);
  const lb = toLinear(b);

  const l = Math.cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
  const m = Math.cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
  const s = Math.cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);

  const L = 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s;
  const A = 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s;
  const B = 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s;

  return { l: L, c: Math.hypot(A, B), h: Math.atan2(B, A) };
}

function oklchToRgb({ l, c, h }: Oklch): Rgb {
  const A = c * Math.cos(h);
  const B = c * Math.sin(h);

  const l_ = l + 0.3963377774 * A + 0.2158037573 * B;
  const m_ = l - 0.1055613458 * A - 0.0638541728 * B;
  const s_ = l - 0.0894841775 * A - 1.291485548 * B;

  const L = l_ * l_ * l_;
  const M = m_ * m_ * m_;
  const S = s_ * s_ * s_;

  return {
    r: toSrgb(4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S),
    g: toSrgb(-1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S),
    b: toSrgb(-0.0041960863 * L - 0.7034186147 * M + 1.707614701 * S),
  };
}

const inGamut = ({ r, g, b }: Rgb) =>
  r >= -0.0001 && r <= 1.0001 && g >= -0.0001 && g <= 1.0001 && b >= -0.0001 && b <= 1.0001;

// Walks chroma down until the colour fits in sRGB, keeping lightness and hue.
// Clipping the channels instead would shift the hue — a bright red clipped at
// the top end drifts orange, and the ramp stops looking like one colour.
function toGamut(target: Oklch): Rgb {
  const direct = oklchToRgb(target);
  if (inGamut(direct)) return direct;

  let lo = 0;
  let hi = target.c;
  let best = oklchToRgb({ ...target, c: 0 });
  for (let i = 0; i < 18; i++) {
    const mid = (lo + hi) / 2;
    const candidate = oklchToRgb({ ...target, c: mid });
    if (inGamut(candidate)) {
      best = candidate;
      lo = mid;
    } else {
      hi = mid;
    }
  }
  return best;
}

// ─── Ramp ──────────────────────────────────────────────────────────────

export type Ramp = Record<Shade, string>;

/**
 * The shade a base colour belongs to — the one whose target lightness is
 * closest to its own. A mid-tone brand colour lands on 500 as you'd expect;
 * a near-black surface lands on 950.
 */
export function anchorFor(baseHex: string): Shade {
  const rgb = parseHex(baseHex);
  if (!rgb) return 500;
  const { l } = rgbToOklch(rgb);
  let best: Shade = 500;
  let bestDistance = Infinity;
  for (const shade of SHADES) {
    const distance = Math.abs(l - LIGHTNESS[shade]);
    if (distance < bestDistance) {
      bestDistance = distance;
      best = shade;
    }
  }
  return best;
}

/**
 * 11 shades from one base hex. Returns null when the hex can't be parsed —
 * callers treat that as "leave this palette inherited" rather than guessing.
 */
export function buildRamp(baseHex: string): Ramp | null {
  const rgb = parseHex(baseHex);
  if (!rgb) return null;
  const base = rgbToOklch(rgb);
  const anchor = anchorFor(baseHex);

  const ramp = {} as Ramp;
  for (const shade of SHADES) {
    if (shade === anchor) {
      // Verbatim, no round-trip: OKLCh→sRGB→hex can land a byte off the input.
      ramp[shade] = toHex(rgb);
      continue;
    }
    // Chroma is expressed relative to the anchor, so the base keeps exactly
    // the saturation it was picked with and the rest taper around it. Scaling
    // against a fixed 500 instead would over-saturate the mid-tones whenever
    // the anchor sits at an end of the ramp.
    const chroma = (base.c * CHROMA_SCALE[shade]) / CHROMA_SCALE[anchor];
    ramp[shade] = toHex(toGamut({ l: LIGHTNESS[shade], c: chroma, h: base.h }));
  }
  return ramp;
}

/**
 * One OKLCh colour as hex, gamut-mapped. Hue in degrees. Used by the preset
 * randomiser, which thinks in perceptual terms for the same reason the ramp
 * generator does: equal-lightness picks read as one family.
 */
export function oklchHex(l: number, c: number, hDeg: number): string {
  return toHex(toGamut({ l, c, h: (hDeg * Math.PI) / 180 }));
}

/**
 * WCAG relative luminance, for deciding what text a shade can carry.
 */
export function luminance(hex: string): number {
  const rgb = parseHex(hex);
  if (!rgb) return 0;
  return 0.2126 * toLinear(rgb.r) + 0.7152 * toLinear(rgb.g) + 0.0722 * toLinear(rgb.b);
}

/**
 * Which end of the ramp reads on a given shade: 'dark' text on light fills,
 * 'light' text on dark ones. 0.4 luminance is where flipping stops improving
 * contrast for the mid-tones this generator produces.
 */
export function contrastEndFor(hex: string): 'dark' | 'light' {
  return luminance(hex) > 0.4 ? 'dark' : 'light';
}

/**
 * WCAG 2.x contrast ratio between two colours, 1–21. The editor uses it to
 * warn — not block — when a palette's filled-button combination lands under
 * the 4.5:1 AA threshold.
 */
export function contrastRatio(a: string, b: string): number {
  const la = luminance(a);
  const lb = luminance(b);
  const [hi, lo] = la > lb ? [la, lb] : [lb, la];
  return (hi + 0.05) / (lo + 0.05);
}
