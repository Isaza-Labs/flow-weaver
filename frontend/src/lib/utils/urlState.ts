// Helpers to read/write listing-page state (search, tab, pagination, sort)
// from the current URL's query string. Centralising it means every list page
// behaves the same way: F5 keeps state, the URL is shareable, and the
// back/forward buttons traverse filter history.

import { browser } from '$app/environment';
import { goto } from '$app/navigation';
import { page } from '$app/state';

export type UrlValue = string | number | undefined | null;

export function readUrlParam(key: string): string | null {
  if (!browser) return null;
  return page.url.searchParams.get(key);
}

export function readUrlInt(key: string, fallback = 0): number {
  const raw = readUrlParam(key);
  if (raw === null) return fallback;
  const n = Number.parseInt(raw, 10);
  return Number.isFinite(n) ? n : fallback;
}

/**
 * Replace the current URL's query string with the given params. Keys whose
 * value is undefined / null / '' / 0 are dropped (so the URL stays clean).
 * Uses `replaceState` so it doesn't pollute history on every keystroke.
 */
export function setUrlParams(params: Record<string, UrlValue>, opts: { history?: boolean } = {}): void {
  if (!browser) return;
  const url = new URL(page.url);
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '' || value === 0) {
      url.searchParams.delete(key);
    } else {
      url.searchParams.set(key, String(value));
    }
  }
  // keepFocus avoids stealing focus from search inputs while typing.
  void goto(url.pathname + url.search + url.hash, {
    keepFocus: true,
    noScroll: true,
    replaceState: !opts.history,
  });
}
