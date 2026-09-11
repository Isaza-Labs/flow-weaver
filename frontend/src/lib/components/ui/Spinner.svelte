<script lang="ts">
  let { size = 'md', label }: { size?: 'sm' | 'md' | 'lg'; label?: string } = $props();

  const sizeClass = $derived({
    sm: 'w-3 h-3 border-2',
    md: 'w-4 h-4 border-2',
    lg: 'w-6 h-6 border-2',
  }[size]);
</script>

<div class="inline-flex items-center gap-2 text-surface-500" role="status">
  <span class="rounded-full border-current border-t-transparent animate-spin {sizeClass}" aria-hidden="true"></span>
  {#if label}<span class="text-sm">{label}</span>{:else}<span class="sr-only">Loading…</span>{/if}
</div>
