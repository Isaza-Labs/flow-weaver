<script lang="ts">
  import { ArrowUp, ArrowDown, ArrowUpDown } from 'lucide-svelte';

  let {
    column,
    sortKey,
    sortDir = 'asc',
    onSort,
    align = 'left',
    children,
  }: {
    column: string;
    sortKey: string | null;
    sortDir?: 'asc' | 'desc';
    onSort: (column: string) => void;
    align?: 'left' | 'right' | 'center';
    children?: import('svelte').Snippet;
  } = $props();

  const active = $derived(sortKey === column);
  const alignClass = $derived(align === 'right' ? '!text-right' : align === 'center' ? '!text-center' : '');
</script>

<th class="sortable {alignClass}" aria-sort={active ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none'}>
  <button
    type="button"
    class="inline-flex items-center gap-1 {align === 'right' ? 'flex-row-reverse' : ''} cursor-pointer hover:opacity-80"
    onclick={() => onSort(column)}
  >
    {#if children}{@render children()}{/if}
    {#if active}
      {#if sortDir === 'asc'}<ArrowUp size={11} />{:else}<ArrowDown size={11} />{/if}
    {:else}
      <ArrowUpDown size={11} class="opacity-40" />
    {/if}
  </button>
</th>
