// Turns a saved theme (a handful of base colours) into the CSS custom
// properties Skeleton reads, and gets them onto the page.
//
// The built-in themes ship as static CSS under `$lib/themes/`. A user theme
// can't: it exists only in the database, so its variables are generated at
// runtime and injected into one <style> element. Both end up as
// `[data-theme='…'] { --color-… }` rules, so everything downstream — the mode
// toggle, the `surface-100-900` pair utilities, Skeleton's components — works
// on a custom theme exactly as on a factory one.

import { SHADES, buildRamp, contrastEndFor } from './ramp';

// Palettes a theme may override. Anything omitted keeps the FlowWeaver brand
// value from `_palette.css`, so a one-colour theme is valid.
export const PALETTES = [
  'primary',
  'secondary',
  'tertiary',
  'success',
  'warning',
  'error',
  'surface',
] as const;
export type Palette = (typeof PALETTES)[number];

export type ThemeColors = Partial<Record<Palette, string>>;

// ─── Style settings ─────────────────────────────────────────────────────
// Non-colour knobs a theme may set. Every one is optional; an absent key
// inherits the app default, so a colours-only theme behaves exactly as
// before settings existed. The server validates the same keys and ranges
// (ThemeService.ValidateSettings) — these types mirror that contract.

export const FONT_BODY_KEYS = ['inter', 'system', 'serif', 'mono'] as const;
export const FONT_HEADING_KEYS = ['inherit', 'inter', 'system', 'serif', 'mono'] as const;
export const FONT_MONO_KEYS = ['jetbrains', 'system'] as const;

export type FontBodyKey = (typeof FONT_BODY_KEYS)[number];
export type FontHeadingKey = (typeof FONT_HEADING_KEYS)[number];
export type FontMonoKey = (typeof FONT_MONO_KEYS)[number];

export interface ThemeSettings {
  /** Corner radius multiplier over the app defaults. 0 = square, 2 = round. */
  roundness?: number;
  /** Root font-size multiplier — everything rem-based follows. 0.85–1.15. */
  ui_scale?: number;
  font_body?: FontBodyKey;
  font_heading?: FontHeadingKey;
  font_mono?: FontMonoKey;
  /** Heading font-weight, 300–900. The app default is 650. */
  heading_weight?: number;
}

// Only stacks that need no extra webfont: Inter and JetBrains Mono are the
// two the bundle already loads, everything else is system-safe. A free-text
// font field would mean loading arbitrary remote fonts from a shared theme.
const SANS_STACKS: Record<Exclude<FontHeadingKey, 'inherit'>, string> = {
  inter: "'Inter', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
  system: "ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif",
  serif: "Georgia, 'Iowan Old Style', 'Times New Roman', Times, serif",
  mono: "'JetBrains Mono', ui-monospace, 'SF Mono', Menlo, Consolas, monospace",
};
const MONO_STACKS: Record<FontMonoKey, string> = {
  jetbrains: "'JetBrains Mono', ui-monospace, 'SF Mono', Menlo, monospace",
  system: "ui-monospace, 'SF Mono', Menlo, Consolas, 'Liberation Mono', monospace",
};

// Tailwind v4's default radius scale (rem). Utilities like `rounded-md`
// resolve to `var(--radius-md)`, so restating these scaled on the theme's
// [data-theme] rule reshapes every corner in the app — including the
// components that never heard of Skeleton's --radius-base.
const RADIUS_SCALE: Record<string, number> = {
  'radius-xs': 0.125,
  'radius-sm': 0.25,
  'radius-md': 0.375,
  'radius-lg': 0.5,
  'radius-xl': 0.75,
  'radius-2xl': 1,
  'radius-3xl': 1.5,
  'radius-base': 0.375, // Skeleton's tokens, kept in step
  'radius-container': 0.5,
};

const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));

/**
 * The CSS declarations one theme's settings add on top of its colours. Same
 * shape as `themeVars` so the two merge into one rule. `font-size` is the one
 * real property in the map: it must live on the `[data-theme]` element (the
 * <html> tag) for rem-based sizing to follow it.
 */
export function settingsVars(settings: ThemeSettings | undefined): Record<string, string> {
  const vars: Record<string, string> = {};
  if (!settings) return vars;

  if (typeof settings.roundness === 'number') {
    const factor = clamp(settings.roundness, 0, 2);
    for (const [token, rem] of Object.entries(RADIUS_SCALE)) {
      vars[`--${token}`] = `${Math.round(rem * factor * 1000) / 1000}rem`;
    }
  }
  if (typeof settings.ui_scale === 'number') {
    vars['font-size'] = `calc(100% * ${clamp(settings.ui_scale, 0.85, 1.15)})`;
  }
  if (settings.font_body && settings.font_body in SANS_STACKS) {
    // Both tokens: --font-sans is what app.css puts on <html>, and
    // --base-font-family is what Skeleton's base styles read.
    vars['--font-sans'] = SANS_STACKS[settings.font_body];
    vars['--base-font-family'] = SANS_STACKS[settings.font_body];
  }
  if (settings.font_heading && settings.font_heading !== 'inherit' && settings.font_heading in SANS_STACKS) {
    vars['--heading-font-family'] = SANS_STACKS[settings.font_heading];
  }
  if (settings.font_mono && settings.font_mono in MONO_STACKS) {
    vars['--font-mono'] = MONO_STACKS[settings.font_mono];
  }
  if (typeof settings.heading_weight === 'number') {
    vars['--heading-font-weight'] = String(Math.round(clamp(settings.heading_weight, 300, 900)));
  }
  return vars;
}

/** `data-theme` value for a saved theme. Must match the prefix `_palette.css` matches on. */
export const customThemeId = (id: string) => `fw-custom-${id}`;
export const isCustomThemeId = (theme: string) => theme.startsWith('fw-custom-');

/**
 * The CSS custom properties for one theme, as a plain map. Used directly as
 * inline styles on the editor's preview pane, which is why this returns a map
 * rather than a CSS string: the preview scopes the theme to one subtree
 * without touching the document.
 */
export function themeVars(colors: ThemeColors): Record<string, string> {
  const vars: Record<string, string> = {};
  for (const palette of PALETTES) {
    const base = colors[palette];
    if (!base) continue;
    const ramp = buildRamp(base);
    if (!ramp) continue;

    for (const shade of SHADES) vars[`--color-${palette}-${shade}`] = ramp[shade];

    // Skeleton resolves readable text through these. The pair is fixed (the
    // ends of this palette's own ramp); per-shade picks flip based on how
    // light the fill actually came out, so a pale brand colour gets dark text
    // on its 600 instead of unreadable white.
    vars[`--color-${palette}-contrast-dark`] = ramp[950];
    vars[`--color-${palette}-contrast-light`] = ramp[50];
    for (const shade of SHADES) {
      vars[`--color-${palette}-contrast-${shade}`] =
        contrastEndFor(ramp[shade]) === 'dark'
          ? `var(--color-${palette}-contrast-dark)`
          : `var(--color-${palette}-contrast-light)`;
    }
  }

  // The body backdrop is a token, not a utility class, so it has to be
  // restated whenever surface moves — otherwise the page keeps the previous
  // theme's background behind correctly-themed panels.
  if (colors.surface) {
    vars['--body-background-color'] = 'var(--color-surface-50)';
    vars['--body-background-color-dark'] = 'var(--color-surface-950)';
  }
  return vars;
}

const declarations = (vars: Record<string, string>) =>
  Object.entries(vars)
    .map(([k, v]) => `${k}:${v}`)
    .join(';');

/** Inline `style` attribute value — scopes a theme to one element subtree. */
export const themeStyleAttr = (colors: ThemeColors, settings?: ThemeSettings) =>
  declarations({ ...themeVars(colors), ...settingsVars(settings) });

/** One `[data-theme='…'] { … }` rule. */
export function themeRule(id: string, colors: ThemeColors, settings?: ThemeSettings): string {
  return `[data-theme='${customThemeId(id)}']{${declarations({
    ...themeVars(colors),
    ...settingsVars(settings),
  })}}`;
}

const STYLE_ID = 'fw-custom-themes';
const CACHE_KEY = 'flowweaver:custom-theme-css';

/**
 * Replaces the injected stylesheet with rules for every theme passed in, and
 * caches the CSS so the pre-paint script in `app.html` can apply it on the
 * next load before hydration. Without that cache a custom theme flashes the
 * default palette on every navigation to the app.
 */
export function applyCustomThemes(
  // `settings` arrives as the API's loose map; settingsVars type-checks every
  // key before emitting anything, so a malformed value degrades to "inherit".
  themes: { theme_id: string; colors: ThemeColors; settings?: Record<string, unknown> | null }[],
): void {
  if (typeof document === 'undefined') return;
  const css = themes
    .map((t) => themeRule(t.theme_id, t.colors, (t.settings ?? undefined) as ThemeSettings | undefined))
    .join('\n');

  let style = document.getElementById(STYLE_ID) as HTMLStyleElement | null;
  if (!style) {
    style = document.createElement('style');
    style.id = STYLE_ID;
    document.head.appendChild(style);
  }
  style.textContent = css;

  try {
    localStorage.setItem(CACHE_KEY, css);
  } catch {
    // Quota or disabled storage — the themes still work this session, they
    // just flash on the next load.
  }
}
