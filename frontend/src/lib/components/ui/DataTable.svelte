<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    children,
    caption,
    class: cls = '',
    sticky = true,
    density = 'comfortable',
  }: {
    children: Snippet;
    /** Visually-hidden table caption for screen readers (describes the table). */
    caption?: string;
    class?: string;
    sticky?: boolean;
    density?: 'compact' | 'comfortable';
  } = $props();
</script>

<div class="w-full overflow-x-auto bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg {sticky ? 'max-h-[70vh] overflow-y-auto' : ''} {cls}" data-density={density}>
  <table class="w-full text-sm border-collapse">
    {#if caption}<caption class="sr-only">{caption}</caption>{/if}
    {@render children()}
  </table>
</div>

<style>
  /* Legacy global table styling — preserved unchanged so that pages
     rendering a bare <table> outside DataTable (subflows, audit, etc.)
     still look right. Density variants below only narrow the rules for
     tables that opt into the DataTable wrapper. */
  :global(table thead tr) {
    background: color-mix(in oklab, var(--color-surface-200-800) 35%, transparent);
  }
  :global(table thead th) {
    padding: 0.5rem 0.875rem;
    text-align: left;
    font-size: 0.6875rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: color-mix(in oklab, currentColor 55%, transparent);
    border-bottom: 1px solid color-mix(in oklab, var(--color-surface-300-700) 60%, transparent);
    white-space: nowrap;
    height: 36px;
  }
  :global(table tbody td) {
    padding: 0.625rem 0.875rem;
    border-bottom: 1px solid color-mix(in oklab, var(--color-surface-200-800) 40%, transparent);
    vertical-align: middle;
  }
  :global(table tbody tr:last-child td) {
    border-bottom: 0;
  }
  :global(table tbody tr) {
    transition: background-color 120ms ease, box-shadow 120ms ease;
    position: relative;
  }
  :global(table tbody tr:hover) {
    background: color-mix(in oklab, var(--color-surface-200-800) 25%, transparent);
    box-shadow: inset 2px 0 0 0 var(--color-primary-500);
  }

  /* Sticky table headers inside DataTable. Background is opaque so rows
     scrolling underneath don't bleed through. */
  :global([data-density] table thead th) {
    position: sticky;
    top: 0;
    z-index: 1;
    background: var(--color-surface-100);
  }
  :global([data-mode='dark'] [data-density] table thead th) {
    background: var(--color-surface-900);
  }

  /* Compact density override — tighter rows for users who want more
     density per page. */
  :global([data-density='compact'] table tbody td) {
    padding: 0.375rem 0.75rem;
    font-size: 0.8125rem;
  }

  /* Selected-row affordance — used by listings with checkbox selection. */
  :global([data-density] table tbody tr[data-selected='true']) {
    background: color-mix(in oklab, var(--color-primary-500) 8%, transparent);
    box-shadow: inset 2px 0 0 0 var(--color-primary-500);
  }

  /* Sortable header — rendered with class `sortable` by SortableTh. */
  :global([data-density] table thead th.sortable) {
    cursor: pointer;
    user-select: none;
  }
  :global([data-density] table thead th.sortable:hover) {
    color: color-mix(in oklab, currentColor 80%, transparent);
  }
</style>
