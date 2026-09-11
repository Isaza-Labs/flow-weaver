import type { Action } from 'svelte/action';

const FOCUSABLE =
  'a[href],button:not([disabled]),textarea:not([disabled]),input:not([disabled]),select:not([disabled]),[tabindex]:not([tabindex="-1"])';

/**
 * Traps Tab focus within `node` while `active` is true, focuses the first
 * focusable element on activation, and restores focus to the previously
 * focused element on deactivation.
 *
 * The Dialog primitive ships its own trap; use this for ad-hoc overlays such
 * as the mobile navigation drawer. The node should have `tabindex="-1"` so it
 * can receive focus when it contains no focusable children.
 */
export const focusTrap: Action<HTMLElement, boolean> = (node, active = true) => {
  let previouslyFocused: HTMLElement | null = null;

  function visibleFocusables(): HTMLElement[] {
    return Array.from(node.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
      (el) => el.offsetParent !== null,
    );
  }

  function onKeydown(e: KeyboardEvent) {
    if (e.key !== 'Tab') return;
    const els = visibleFocusables();
    if (els.length === 0) {
      e.preventDefault();
      node.focus();
      return;
    }
    const first = els[0];
    const last = els[els.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }

  function activate() {
    if (previouslyFocused) return;
    previouslyFocused = document.activeElement as HTMLElement;
    node.addEventListener('keydown', onKeydown);
    // Defer so the node is laid out/visible before we move focus into it.
    queueMicrotask(() => (visibleFocusables()[0] ?? node).focus());
  }

  function deactivate() {
    if (!previouslyFocused) return;
    node.removeEventListener('keydown', onKeydown);
    previouslyFocused.focus?.();
    previouslyFocused = null;
  }

  if (active) activate();
  return {
    update(now: boolean) {
      if (now) activate();
      else deactivate();
    },
    destroy() {
      deactivate();
    },
  };
};
