// Lightweight user preferences kept in localStorage. Currently tracks:
//   - sidebar collapsed (icon-only) vs expanded
//   - table density (compact / comfortable)
//
// Default state is read at module load so the layout doesn't flash to the
// wrong width before hydration.

import { browser } from '$app/environment';

const SIDEBAR_KEY = 'flowweaver:sidebar:collapsed';
const DENSITY_KEY = 'flowweaver:table:density';
const QUICKLINKS_KEY = 'flowweaver:dashboard:quicklinks';

// Dashboard shortcuts a first-time user sees before customizing. Stored as
// hrefs; the dashboard resolves label + icon from $lib/nav at render time.
const DEFAULT_QUICKLINKS = ['/workflows', '/runs', '/schedules', '/devices'];

// Pages that were replaced, and the page that replaced them. A shortcut saved
// before the rename is read as the new page, so it keeps showing — and can still
// be removed or moved, which works by the stored href.
const RENAMED_QUICKLINKS: Readonly<Record<string, string>> = {
  '/credentials': '/vault',
  '/admin/secrets': '/vault',
};

export type Density = 'compact' | 'comfortable';

function readBool(key: string, fallback: boolean): boolean {
  if (!browser) return fallback;
  const raw = localStorage.getItem(key);
  if (raw === '1') return true;
  if (raw === '0') return false;
  return fallback;
}

function readDensity(): Density {
  if (!browser) return 'comfortable';
  const raw = localStorage.getItem(DENSITY_KEY);
  return raw === 'compact' ? 'compact' : 'comfortable';
}

// An empty stored array is a valid state (user removed every shortcut); only
// a missing key or malformed value falls back to the defaults.
function readQuickLinks(): string[] {
  if (!browser) return [...DEFAULT_QUICKLINKS];
  try {
    const raw = localStorage.getItem(QUICKLINKS_KEY);
    if (raw === null) return [...DEFAULT_QUICKLINKS];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [...DEFAULT_QUICKLINKS];
    const stored = parsed.filter((x): x is string => typeof x === 'string');
    // Credentials and Secrets both pinned become one Vault shortcut.
    const hrefs = [...new Set(stored.map((href) => RENAMED_QUICKLINKS[href] ?? href))];
    // Written back once, so the old hrefs do not linger in storage. A write that
    // fails (quota, blocked storage) costs only that: the migrated list is still
    // what this session uses, rather than falling back to the defaults.
    if (hrefs.length !== stored.length || hrefs.some((h, i) => h !== stored[i])) {
      try {
        localStorage.setItem(QUICKLINKS_KEY, JSON.stringify(hrefs));
      } catch {
        // Keep the in-memory list.
      }
    }
    return hrefs;
  } catch {
    return [...DEFAULT_QUICKLINKS];
  }
}

class PrefsStore {
  sidebarCollapsed = $state<boolean>(readBool(SIDEBAR_KEY, false));
  density = $state<Density>(readDensity());

  setSidebarCollapsed(next: boolean) {
    this.sidebarCollapsed = next;
    if (browser) {
      try { localStorage.setItem(SIDEBAR_KEY, next ? '1' : '0'); } catch {}
    }
  }
  toggleSidebar() { this.setSidebarCollapsed(!this.sidebarCollapsed); }

  setDensity(next: Density) {
    this.density = next;
    if (browser) {
      try { localStorage.setItem(DENSITY_KEY, next); } catch {}
    }
  }
  toggleDensity() { this.setDensity(this.density === 'compact' ? 'comfortable' : 'compact'); }

  // Dashboard quick-access shortcuts (array of hrefs, user-ordered).
  quickLinks = $state<string[]>(readQuickLinks());

  private saveQuickLinks() {
    if (browser) {
      try { localStorage.setItem(QUICKLINKS_KEY, JSON.stringify(this.quickLinks)); } catch {}
    }
  }

  setQuickLinks(next: string[]) {
    this.quickLinks = next;
    this.saveQuickLinks();
  }
  addQuickLink(href: string) {
    if (this.quickLinks.includes(href)) return;
    this.quickLinks = [...this.quickLinks, href];
    this.saveQuickLinks();
  }
  removeQuickLink(href: string) {
    this.quickLinks = this.quickLinks.filter((h) => h !== href);
    this.saveQuickLinks();
  }
  moveQuickLink(href: string, dir: -1 | 1) {
    const i = this.quickLinks.indexOf(href);
    if (i < 0) return;
    const j = i + dir;
    if (j < 0 || j >= this.quickLinks.length) return;
    const next = [...this.quickLinks];
    [next[i], next[j]] = [next[j], next[i]];
    this.quickLinks = next;
    this.saveQuickLinks();
  }
}

export const prefsStore = new PrefsStore();
