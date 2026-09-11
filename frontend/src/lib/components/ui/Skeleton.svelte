<script lang="ts">
  let {
    variant = 'text',
    count = 1,
    columns = 4,
  }: {
    variant?: 'text' | 'row' | 'card' | 'table' | 'stat';
    count?: number;
    columns?: number;
  } = $props();
  const heights: Record<'text' | 'row' | 'card' | 'stat', string> = {
    text: 'h-4',
    row: 'h-12',
    card: 'h-24',
    stat: 'h-20',
  };
</script>

{#if variant === 'table'}
  <div class="w-full bg-surface-100-900/70 ring-1 ring-surface-200-800/80 rounded-lg overflow-hidden">
    <div class="border-b border-surface-200-800/60 px-3.5 h-9 flex items-center gap-3">
      {#each Array(columns) as _, i (i)}
        <div class="h-3 rounded bg-surface-200-800/50 animate-pulse" style="width: {[28, 18, 12, 14, 16, 20][i % 6]}%"></div>
      {/each}
    </div>
    {#each Array(count) as _, i (i)}
      <div class="px-3.5 h-12 flex items-center gap-3 border-b border-surface-200-800/40 last:border-b-0">
        {#each Array(columns) as _, j (j)}
          <div class="h-3 rounded bg-surface-200-800/40 animate-pulse" style="width: {[24, 20, 14, 12, 18, 22][(i + j) % 6]}%; animation-delay: {(i * 60) + (j * 25)}ms"></div>
        {/each}
      </div>
    {/each}
  </div>
{:else}
  {#each Array(count) as _, i (i)}
    <div
      class="animate-pulse bg-surface-200-800/40 rounded {heights[variant]} {variant === 'card' || variant === 'row' || variant === 'stat' ? 'w-full' : 'w-3/4'} {i > 0 ? 'mt-2' : ''}"
    ></div>
  {/each}
{/if}
