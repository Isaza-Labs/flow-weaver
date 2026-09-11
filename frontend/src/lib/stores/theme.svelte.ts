// Persistent theme + mode store.
//
// FlowWeaver ships seven built-in brand themes — each carries the same
// FlowWeaver palette (primary indigo, brand yellow, etc.) but differs in
// chrome: surface palette, radii, body atmosphere, heading treatment.
// All are mode-aware (light/dark via data-mode), so the mode toggle stays
// independent from the theme picker.
//
// On top of those, users author their own at /themes. Those live in the
// database and their CSS is generated at runtime from a handful of base
// colours ($lib/theme/customTheme.ts), so the picker's list is built-ins +
// whatever the API returns.
//
// Both attributes are mirrored to <html data-theme="..." data-mode="..."> and
// persisted to localStorage. The pre-paint script in app.html applies the
// same attributes before hydration so the page never flashes the wrong style.

import { browser } from '$app/environment';
import { applyCustomThemes, isCustomThemeId, customThemeId } from '$lib/theme/customTheme';
import { themes as themesApi, type Theme } from '$lib/api/client';

export type BuiltinThemeName =
  | 'fw-editorial'
  | 'fw-aurora'
  | 'fw-brutalist'
  | 'fw-wordmark'
  | 'fw-pastel'
  | 'fw-cyber'
  | 'fw-material';

// A saved theme's id is `fw-custom-<uuid>`, so the union stays open.
export type ThemeName = BuiltinThemeName | (string & {});
export type Mode = 'light' | 'dark';

export interface ThemeOption {
  id: ThemeName;
  label: string;
  description: string;
  // Set for user-authored themes; absent on the built-in ones. Carries what
  // the picker needs to show ownership and offer editing.
  custom?: { themeId: string; isShared: boolean; canEdit: boolean };
}

export const BUILTIN_THEMES: readonly ThemeOption[] = [
  { id: 'fw-editorial', label: 'Editorial',
    description: 'Minimal, monochrome, hairline borders. Linear/Vercel.' },
  { id: 'fw-aurora', label: 'Aurora Glass',
    description: 'Gradient mesh + frosted glass surfaces. Stripe/Arc.' },
  { id: 'fw-brutalist', label: 'Brutalist',
    description: 'Yellow-forward, hard edges, 800-weight headings.' },
  { id: 'fw-wordmark', label: 'Wordmark',
    description: 'Brand duotone — grey body + yellow headings.' },
  { id: 'fw-pastel', label: 'Soft Pastel',
    description: 'Warm cream surfaces, large radii, coral accents. Notion-like.' },
  { id: 'fw-cyber', label: 'Cyber Neon',
    description: 'Dark indigo+sky with neon glow. Synthwave.' },
  { id: 'fw-material', label: 'Material Tonal',
    description: 'Tonal indigo surfaces, pill buttons. Material You.' },
];

const BUILTIN_IDS = BUILTIN_THEMES.map((t) => t.id);

const MODE_KEY = 'flowweaver:mode';
const THEME_KEY = 'flowweaver:theme';

const DEFAULT_THEME: ThemeName = 'fw-editorial';

function readMode(): Mode {
  if (!browser) return 'dark';
  const saved = localStorage.getItem(MODE_KEY);
  if (saved === 'light' || saved === 'dark') return saved;
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function readTheme(): ThemeName {
  if (!browser) return DEFAULT_THEME;
  const saved = localStorage.getItem(THEME_KEY);
  if (!saved) return DEFAULT_THEME;
  // A custom id can't be validated here — the themes haven't loaded yet.
  // `loadCustom` drops it later if it turns out to be gone.
  if (isCustomThemeId(saved)) return saved;
  return (BUILTIN_IDS as string[]).includes(saved) ? (saved as ThemeName) : DEFAULT_THEME;
}

class ThemeStore {
  mode = $state<Mode>(readMode());
  theme = $state<ThemeName>(readTheme());

  // User-authored themes, loaded once per session after auth.
  custom = $state<Theme[]>([]);
  customLoaded = $state(false);

  // What the picker renders: the seven built-ins, then anything saved.
  get available(): ThemeOption[] {
    return [
      ...BUILTIN_THEMES,
      ...this.custom.map((t) => ({
        id: customThemeId(t.theme_id),
        label: t.name,
        description: t.description ?? (t.is_shared ? 'Shared theme' : 'Your theme'),
        custom: { themeId: t.theme_id, isShared: t.is_shared, canEdit: t.can_edit },
      })),
    ];
  }

  /**
   * Fetches saved themes and injects their CSS. Safe to call repeatedly —
   * the editor calls it after every save so the picker and the live page
   * pick up the change without a reload.
   */
  async loadCustom(): Promise<void> {
    if (!browser) return;
    try {
      const res = await themesApi.list();
      this.custom = res.data;
      applyCustomThemes(res.data);
      this.customLoaded = true;

      // The selected theme may have been deleted, or unshared by an admin,
      // between sessions. Its id survives in localStorage and would leave the
      // app on an unstyled `data-theme` nobody defines, so fall back.
      if (isCustomThemeId(this.theme) && !res.data.some((t) => customThemeId(t.theme_id) === this.theme)) {
        this.setTheme(DEFAULT_THEME);
      }
    } catch {
      // Offline or a 401 during boot — the built-ins still work, and a
      // previously cached custom theme is already painted by app.html.
    }
  }

  setMode(next: Mode) {
    this.mode = next;
    if (browser) {
      document.documentElement.setAttribute('data-mode', next);
      try {
        localStorage.setItem(MODE_KEY, next);
      } catch {}
    }
  }

  toggleMode() {
    this.setMode(this.mode === 'dark' ? 'light' : 'dark');
  }

  setTheme(next: ThemeName) {
    this.theme = next;
    if (browser) {
      document.documentElement.setAttribute('data-theme', next);
      try {
        localStorage.setItem(THEME_KEY, next);
      } catch {}
    }
  }
}

export const themeStore = new ThemeStore();
