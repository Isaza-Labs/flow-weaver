<script lang="ts">
  // Contextual help for a page, rendered as an ⓘ button next to the title
  // that opens a small popover. The text is NOT passed in per call site —
  // it comes from the central $lib/guides registry, the same source the
  // floating GuideMascot reads. One registry, two surfaces: a hint that
  // says something different from the guide would be worse than no hint.
  import { Info, X } from 'lucide-svelte';
  import type { GuideEntry } from '$lib/guides';

  let { entry, label = 'About this page' }: { entry: GuideEntry; label?: string } = $props();

  let open = $state(false);
  let buttonEl = $state<HTMLButtonElement | null>(null);
  let panelEl = $state<HTMLDivElement | null>(null);

  function toggle() {
    open = !open;
  }

  function close() {
    open = false;
    buttonEl?.focus();
  }

  // Dismiss on outside click / Escape. Bound on window only while open so
  // a page full of hints doesn't keep N listeners alive.
  $effect(() => {
    if (!open) return;

    const onPointerDown = (e: PointerEvent) => {
      const target = e.target as Node;
      if (panelEl?.contains(target) || buttonEl?.contains(target)) return;
      open = false;
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };

    window.addEventListener('pointerdown', onPointerDown, true);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('pointerdown', onPointerDown, true);
      window.removeEventListener('keydown', onKeyDown);
    };
  });
</script>

<span class="relative inline-flex align-middle">
  <button
    bind:this={buttonEl}
    type="button"
    onclick={toggle}
    aria-label={label}
    aria-expanded={open}
    title={label}
    class="inline-flex items-center justify-center w-5 h-5 rounded-full text-surface-500 hover:text-primary-400 hover:bg-surface-200-800/50 focus:outline-none focus:ring-2 focus:ring-primary-500/40 transition-colors"
  >
    <Info size={14} />
  </button>

  {#if open}
    <div
      bind:this={panelEl}
      role="dialog"
      aria-label={label}
      class="absolute left-0 top-7 z-50 w-80 max-w-[calc(100vw-2rem)] rounded-lg border border-surface-300-700 bg-surface-50-950 shadow-xl p-3.5 space-y-2.5 text-left"
    >
      <div class="flex items-start justify-between gap-2">
        <h2 class="text-sm font-semibold text-surface-900-100">{entry.title}</h2>
        <button
          type="button"
          onclick={close}
          aria-label="Close"
          class="text-surface-500 hover:text-surface-900-100 transition-colors shrink-0"
        >
          <X size={14} />
        </button>
      </div>

      <p class="text-xs leading-relaxed text-surface-600-400">{entry.intro}</p>

      {#if entry.tips.length > 0}
        <ul class="space-y-1">
          {#each entry.tips as tip (tip)}
            <li class="flex gap-1.5 text-xs leading-relaxed text-surface-700-300">
              <span class="text-primary-400 shrink-0" aria-hidden="true">·</span>
              <span>{tip}</span>
            </li>
          {/each}
        </ul>
      {/if}

      {#if entry.docsHref}
        <a
          href={entry.docsHref}
          class="inline-block text-xs text-primary-400 hover:text-primary-300 underline"
          onclick={close}
        >
          Full documentation →
        </a>
      {/if}
    </div>
  {/if}
</span>
