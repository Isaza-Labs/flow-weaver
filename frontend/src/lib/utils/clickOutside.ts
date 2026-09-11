import type { Action } from 'svelte/action';

/**
 * Calls `handler` on a pointer press outside `node`.
 *
 * Apply this to a container that INCLUDES the trigger button so that clicking
 * the trigger to toggle the popover doesn't immediately re-open it. Listens on
 * `pointerdown` in the capture phase so it fires even when inner handlers call
 * `stopPropagation`, and after the opening click has already settled (the
 * trigger's own pointerdown happens before this action mounts).
 */
export const clickOutside: Action<HTMLElement, () => void> = (node, handler) => {
  let cb = handler;
  function onPointer(e: Event) {
    if (cb && node.isConnected && !node.contains(e.target as Node)) cb();
  }
  document.addEventListener('pointerdown', onPointer, true);
  return {
    update(next: () => void) {
      cb = next;
    },
    destroy() {
      document.removeEventListener('pointerdown', onPointer, true);
    },
  };
};
