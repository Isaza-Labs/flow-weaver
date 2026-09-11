<script lang="ts">
  import type { Snippet } from 'svelte';
  let { children }: { children: Snippet } = $props();
</script>

<kbd class="inline-flex items-center justify-center min-w-[1.25rem] px-1.5 h-5 rounded text-[10px] font-mono font-medium bg-surface-200-800 text-surface-700-300 border border-surface-300-700">
  {@render children()}
</kbd>
