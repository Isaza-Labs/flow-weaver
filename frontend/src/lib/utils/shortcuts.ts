// Global keyboard-shortcut registry. Components register handlers by id;
// the layout listens on `keydown` once and dispatches. Keeps the
// matching logic in one place so we don't sprinkle modifier-key checks
// across the codebase.

import { browser } from '$app/environment';

export type ShortcutHandler = (event: KeyboardEvent) => void;

export interface Shortcut {
  /** Identifier used for deregistration. */
  id: string;
  /** Display label for the command palette / help screen. */
  label: string;
  /** Lowercase key as reported by KeyboardEvent.key (e.g. 'k', '/', 'enter'). */
  key: string;
  ctrlOrMeta?: boolean;
  shift?: boolean;
  alt?: boolean;
  /** When true the handler fires even if the focus is inside an input. */
  allowInInput?: boolean;
  handler: ShortcutHandler;
}

const registry = new Map<string, Shortcut>();

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  const tag = target.tagName;
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
  if (target.isContentEditable) return true;
  return false;
}

function matches(s: Shortcut, e: KeyboardEvent): boolean {
  if (e.key.toLowerCase() !== s.key.toLowerCase()) return false;
  const ctrlOrMeta = e.ctrlKey || e.metaKey;
  if (!!s.ctrlOrMeta !== ctrlOrMeta) return false;
  if (!!s.shift !== e.shiftKey) return false;
  if (!!s.alt !== e.altKey) return false;
  return true;
}

export function registerShortcut(s: Shortcut): () => void {
  registry.set(s.id, s);
  return () => registry.delete(s.id);
}

export function unregisterShortcut(id: string): void {
  registry.delete(id);
}

export function listShortcuts(): Shortcut[] {
  return Array.from(registry.values());
}

function onKeydown(e: KeyboardEvent) {
  const editable = isEditableTarget(e.target);
  for (const s of registry.values()) {
    if (editable && !s.allowInInput) continue;
    if (matches(s, e)) {
      e.preventDefault();
      s.handler(e);
      return;
    }
  }
}

let installed = false;
export function installShortcutListener(): () => void {
  if (!browser || installed) return () => {};
  installed = true;
  window.addEventListener('keydown', onKeydown);
  return () => {
    installed = false;
    window.removeEventListener('keydown', onKeydown);
  };
}
