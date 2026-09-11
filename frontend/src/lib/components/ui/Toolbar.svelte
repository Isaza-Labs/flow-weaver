<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    left,
    right,
    children,
  }: {
    left?: Snippet;
    right?: Snippet;
    children?: Snippet;
  } = $props();
</script>

<div class="flex items-center justify-between gap-3 flex-wrap">
  <div class="flex items-center gap-2 flex-wrap">
    {#if left}{@render left()}{/if}
    {#if children}{@render children()}{/if}
  </div>
  {#if right}
    <div class="flex items-center gap-2 flex-wrap">{@render right()}</div>
  {/if}
</div>
