<script lang="ts">
  // Per-control help: a small ⓘ next to one field, toggle, column or section.
  // Same affordance and popover language as the page-level Hint in
  // PageHeader, one size down, and fed from the same $lib/guides module —
  // so "what is this?" always looks and behaves the same anywhere in the app.
  //
  // Text comes from the registry by key, never as a prop, so the copy is
  // reviewable in one place instead of scattered across 40 route files.
  //
  // The panel is rendered into <body> and positioned with fixed coordinates
  // rather than absolutely inside the trigger. That is not a style choice:
  // most of these hints live inside a Dialog body (overflow-y-auto) or a
  // DataTable (overflow-x-auto), and an absolutely-positioned panel is
  // clipped by both. Fixed + portal escapes the clip, and lets the panel
  // flip when it would otherwise spill off the right edge or the bottom.
  import { Info } from 'lucide-svelte';
  import { findFieldHint } from '$lib/guides/fields';

  let { id }: { id: string } = $props();

  const entry = $derived(findFieldHint(id));

  const PANEL_W = 256; // matches w-64
  const GAP = 6; // breathing room between trigger and panel
  const EDGE = 8; // minimum distance from any viewport edge

  let open = $state(false);
  let buttonEl = $state<HTMLButtonElement | null>(null);
  let panelEl = $state<HTMLElement | null>(null);
  let top = $state(0);
  let left = $state(0);

  function place() {
    if (!buttonEl) return;
    const r = buttonEl.getBoundingClientRect();

    let x = r.left;
    if (x + PANEL_W > window.innerWidth - EDGE) x = window.innerWidth - PANEL_W - EDGE;
    left = Math.max(EDGE, x);

    // Default below the trigger; flip above when the panel would run off the
    // bottom and there is more room up top. Height is only known once the
    // panel has rendered, so the first frame lands below and the effect
    // corrects it — imperceptible, and always correct by the time it settles.
    const h = panelEl?.offsetHeight ?? 0;
    const below = r.bottom + GAP;
    top = h && below + h > window.innerHeight - EDGE && r.top - GAP - h > EDGE
      ? r.top - GAP - h
      : below;
  }

  // Reposition while open: a scroll inside a dialog or a table moves the
  // trigger out from under a panel that is no longer anchored to it.
  $effect(() => {
    if (!open) return;
    place();
    const onScroll = () => place();
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onScroll);
    return () => {
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onScroll);
    };
  });

  // Re-place once the panel exists and its height is measurable.
  $effect(() => {
    if (open && panelEl) place();
  });

  $effect(() => {
    if (!open) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') open = false;
    };
    const onPointerDown = (e: PointerEvent) => {
      if (!buttonEl?.contains(e.target as Node)) open = false;
    };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('pointerdown', onPointerDown, true);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('pointerdown', onPointerDown, true);
    };
  });

  // Moves the node to <body> so no ancestor's overflow can clip it.
  function portal(node: HTMLElement) {
    document.body.appendChild(node);
    return {
      destroy() {
        node.remove();
      },
    };
  }
</script>

{#if entry}
  <button
    bind:this={buttonEl}
    type="button"
    onclick={(e) => {
      e.preventDefault();
      e.stopPropagation();
      open = !open;
    }}
    onmouseenter={() => (open = true)}
    onmouseleave={() => (open = false)}
    onfocus={() => (open = true)}
    onblur={() => (open = false)}
    aria-label={`What is ${entry.label}?`}
    aria-expanded={open}
    class="inline-flex items-center justify-center w-4 h-4 align-middle rounded-full text-surface-500 hover:text-primary-400 focus:outline-none focus:ring-2 focus:ring-primary-500/40 transition-colors"
  >
    <Info size={12} />
  </button>

  {#if open}
    <!-- pointer-events-none: the panel must never swallow a click meant for
         the control it is explaining, and it has nothing interactive in it. -->
    <div
      bind:this={panelEl}
      use:portal
      role="tooltip"
      style="top: {top}px; left: {left}px; width: {PANEL_W}px;"
      class="fixed z-[100] max-w-[calc(100vw-1rem)] rounded-lg border border-surface-300-700 bg-surface-50-950 shadow-xl p-2.5 text-left pointer-events-none"
    >
      <span class="block text-xs font-semibold text-surface-900-100">{entry.label}</span>
      <span class="block text-[11px] leading-relaxed text-surface-600-400 mt-1">{entry.help}</span>
      {#if entry.detail}
        <span class="block text-[11px] leading-relaxed text-surface-500 mt-1.5 pt-1.5 border-t border-surface-200-800">
          {entry.detail}
        </span>
      {/if}
    </div>
  {/if}
{/if}
