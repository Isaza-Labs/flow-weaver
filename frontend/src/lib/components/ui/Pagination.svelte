<script lang="ts">
  import { ChevronLeft, ChevronRight } from 'lucide-svelte';

  let {
    offset,
    pageSize,
    total,
    label = 'item',
    onPrev,
    onNext,
  }: {
    offset: number;
    pageSize: number;
    total: number;
    label?: string;
    onPrev: () => void;
    onNext: () => void;
  } = $props();

  const start = $derived(total === 0 ? 0 : offset + 1);
  const end = $derived(Math.min(offset + pageSize, total));
  const hasPrev = $derived(offset > 0);
  const hasNext = $derived(end < total);
</script>

<div class="flex items-center justify-between gap-3 text-xs text-surface-500">
  <div>
    {#if total === 0}
      No {label}s
    {:else}
      Showing <span class="font-medium text-surface-700-300 tabular-nums">{start}–{end}</span>
      of <span class="font-medium text-surface-700-300 tabular-nums">{total}</span>
    {/if}
  </div>
  <div class="flex items-center gap-1">
    <button
      type="button"
      onclick={onPrev}
      disabled={!hasPrev}
      aria-label="Previous page"
      class="inline-flex items-center justify-center w-7 h-7 rounded text-surface-700-300 hover:bg-surface-200-800/60 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
    >
      <ChevronLeft size={14} />
    </button>
    <button
      type="button"
      onclick={onNext}
      disabled={!hasNext}
      aria-label="Next page"
      class="inline-flex items-center justify-center w-7 h-7 rounded text-surface-700-300 hover:bg-surface-200-800/60 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
    >
      <ChevronRight size={14} />
    </button>
  </div>
</div>
