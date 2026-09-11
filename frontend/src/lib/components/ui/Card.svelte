<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    padding = 'md',
    interactive = false,
    elevation = 'flat',
    class: cls = '',
    children,
    onclick,
  }: {
    padding?: 'none' | 'sm' | 'md' | 'lg';
    interactive?: boolean;
    elevation?: 'flat' | 'raised';
    class?: string;
    children: Snippet;
    onclick?: (e: MouseEvent) => void;
  } = $props();

  const padClass = $derived({ none: '', sm: 'p-3', md: 'p-4', lg: 'p-5' }[padding]);

  const base = 'bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg';
  const elev = $derived(elevation === 'raised' ? 'shadow-lg shadow-black/5' : '');
  const hover = 'transition-all duration-150 hover:ring-surface-300-700/80 hover:bg-surface-100-900';
</script>

{#if onclick}
  <button
    type="button"
    {onclick}
    class="block w-full text-left {base} {elev} {hover} {padClass} {cls}"
  >
    {@render children()}
  </button>
{:else}
  <div class="{base} {elev} {interactive ? hover : ''} {padClass} {cls}">
    {@render children()}
  </div>
{/if}
