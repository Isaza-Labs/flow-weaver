import type {
  FontBodyKey,
  FontHeadingKey,
  FontMonoKey,
  Palette,
  ThemeColors,
  ThemeSettings,
} from './customTheme';
import { oklchHex } from './ramp';

// The FlowWeaver brand 500s from `_palette.css`, plus a neutral surface base.
// A new theme starts from these so the editor opens on something that already
// looks like the product, and each row's "reset" returns here.
//
// Surface has no single hex in the palette (the built-in themes express it in
// oklch per mode); this is the mid-grey those scales converge on, and the ramp
// generator expands it back into a full neutral scale.
export const BRAND_BASE: Record<Palette, string> = {
  primary: '#5c69ab',
  secondary: '#e07a5a',
  tertiary: '#83c9eb',
  success: '#67bc57',
  warning: '#ffce03',
  error: '#e7577c',
  surface: '#7a7d8a',
};

export const PALETTE_LABEL: Record<Palette, string> = {
  primary: 'Primary',
  secondary: 'Secondary',
  tertiary: 'Tertiary',
  success: 'Success',
  warning: 'Warning',
  error: 'Error',
  surface: 'Surface',
};

export const PALETTE_HINT: Record<Palette, string> = {
  primary: 'Buttons, links, active nav, focus rings.',
  secondary: 'Secondary actions and accents.',
  tertiary: 'Charts and informational highlights.',
  success: 'Completed runs, healthy states.',
  warning: 'Pending queues, degraded states.',
  error: 'Failures, destructive actions.',
  surface: 'Page background, cards, borders, body text.',
};

/** A fresh theme: every palette present, all at brand values. */
export const newThemeColors = (): ThemeColors => ({ ...BRAND_BASE });

// ─── Style settings defaults & labels ───────────────────────────────────
// What each knob falls back to when a theme leaves it unset — these mirror
// the app defaults in `_palette.css` / `app.css`, they don't define them.

export const SETTING_DEFAULTS: Required<ThemeSettings> = {
  roundness: 1,
  ui_scale: 1,
  font_body: 'inter',
  font_heading: 'inherit',
  font_mono: 'jetbrains',
  heading_weight: 650,
};

export const FONT_BODY_LABEL: Record<FontBodyKey, string> = {
  inter: 'Inter (default)',
  system: 'System UI',
  serif: 'Serif',
  mono: 'Monospace',
};

export const FONT_HEADING_LABEL: Record<FontHeadingKey, string> = {
  inherit: 'Same as body',
  inter: 'Inter',
  system: 'System UI',
  serif: 'Serif',
  mono: 'Monospace',
};

export const FONT_MONO_LABEL: Record<FontMonoKey, string> = {
  jetbrains: 'JetBrains Mono (default)',
  system: 'System monospace',
};

export const HEADING_WEIGHTS = [400, 500, 600, 650, 700, 800, 900] as const;

/** A fresh settings map: nothing overridden, everything inherits. */
export const newThemeSettings = (): ThemeSettings => ({});

// ─── Starting points ────────────────────────────────────────────────────
// Curated palettes the editor can start from instead of the brand base.
// Each is a complete seven-colour set (plus optional style hints) chosen so
// the generated ramps read as a designed family, not seven random hues.

export interface ThemePreset {
  key: string;
  label: string;
  hint: string;
  colors: Record<Palette, string>;
  settings?: ThemeSettings;
}

export const THEME_PRESETS: readonly ThemePreset[] = [
  {
    key: 'brand',
    label: 'FlowWeaver',
    hint: 'The stock brand palette — indigo, coral and the signature yellow.',
    colors: { ...BRAND_BASE },
  },
  {
    key: 'midnight',
    label: 'Midnight',
    hint: 'Cool blues and violet on near-black surfaces. NOC-wall material.',
    colors: {
      primary: '#6d8dff',
      secondary: '#9a7bff',
      tertiary: '#4dd0c4',
      success: '#4ade80',
      warning: '#fbbf24',
      error: '#f87171',
      surface: '#252a3d',
    },
  },
  {
    key: 'ocean',
    label: 'Ocean',
    hint: 'Teal-forward with slate neutrals. Calm and cold.',
    colors: {
      primary: '#1789a8',
      secondary: '#14b8a6',
      tertiary: '#67e8f9',
      success: '#34d399',
      warning: '#fcd34d',
      error: '#fb7185',
      surface: '#64748b',
    },
  },
  {
    key: 'forest',
    label: 'Forest',
    hint: 'Mossy greens and warm earth tones.',
    colors: {
      primary: '#3f7d4e',
      secondary: '#a3b18a',
      tertiary: '#d4a373',
      success: '#52b788',
      warning: '#e9c46a',
      error: '#e76f51',
      surface: '#6b705c',
    },
  },
  {
    key: 'sunset',
    label: 'Sunset',
    hint: 'Terracotta and amber over warm greys.',
    colors: {
      primary: '#e76f51',
      secondary: '#f4a261',
      tertiary: '#e9c46a',
      success: '#2a9d8f',
      warning: '#ffb703',
      error: '#d62828',
      surface: '#8a817c',
    },
    settings: { roundness: 1.5 },
  },
  {
    key: 'orchid',
    label: 'Orchid',
    hint: 'Purple and pink with a cyan counterpoint.',
    colors: {
      primary: '#9d4edd',
      secondary: '#d65db1',
      tertiary: '#56cfe1',
      success: '#57cc99',
      warning: '#ffca3a',
      error: '#ff5d8f',
      surface: '#7d7a8c',
    },
  },
  {
    key: 'slate',
    label: 'Slate mono',
    hint: 'Nearly monochrome — colour only where status demands it.',
    colors: {
      primary: '#475569',
      secondary: '#94a3b8',
      tertiary: '#a8b8cf',
      success: '#10b981',
      warning: '#f59e0b',
      error: '#ef4444',
      surface: '#64748b',
    },
    settings: { roundness: 0.5 },
  },
  {
    key: 'contrast',
    label: 'High contrast',
    hint: 'Deep saturated hues for legibility-first setups.',
    colors: {
      primary: '#1d4ed8',
      secondary: '#b45309',
      tertiary: '#0e7490',
      success: '#15803d',
      warning: '#eab308',
      error: '#b91c1c',
      surface: '#52525b',
    },
    settings: { heading_weight: 700 },
  },
];

// ─── Randomiser ─────────────────────────────────────────────────────────

const spread = (base: number, range: number) => base + (Math.random() * 2 - 1) * range;

/**
 * A random but coherent palette: one random brand hue, an offset companion,
 * and status hues held near their conventional slots (green/amber/red) so a
 * surprise theme still reads "ok / pending / failed" at a glance. Built in
 * OKLCh so every roll has comparable perceived lightness and saturation.
 */
export function randomThemeColors(): ThemeColors {
  const brandHue = Math.random() * 360;
  return {
    primary: oklchHex(0.55, spread(0.14, 0.03), brandHue),
    secondary: oklchHex(0.65, spread(0.12, 0.03), brandHue + spread(150, 60)),
    tertiary: oklchHex(0.75, 0.09, brandHue + spread(40, 15)),
    success: oklchHex(0.68, 0.13, spread(147, 12)),
    warning: oklchHex(0.8, 0.15, spread(88, 8)),
    error: oklchHex(0.6, 0.17, spread(24, 8)),
    surface: oklchHex(0.5, spread(0.02, 0.012), brandHue),
  };
}
