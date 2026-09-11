<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    tone = 'neutral',
    variant = 'tonal',
    size = 'sm',
    mono = false,
    children,
  }: {
    tone?: 'neutral' | 'primary' | 'success' | 'warning' | 'error' | 'info';
    variant?: 'tonal' | 'outline' | 'solid';
    size?: 'xs' | 'sm';
    mono?: boolean;
    children: Snippet;
  } = $props();

  const sizeClass = $derived(size === 'xs' ? 'text-[10px] px-1.5 py-0.5' : 'text-xs px-2 py-0.5');

  // Static class map. Tailwind v4 only emits classes it sees as literal
  // strings at build time, so every tone×variant combination is spelled out
  // here rather than interpolated (`bg-${tone}-500`), which the JIT scanner
  // cannot detect and would silently purge.
  const TONE_VARIANT: Record<string, Record<string, string>> = {
    neutral: {
      tonal: 'bg-surface-500/15 text-surface-700-300 ring-1 ring-inset ring-surface-500/25',
      outline: 'text-surface-700-300 ring-1 ring-inset ring-surface-500/40',
      solid: 'bg-surface-500 text-white',
    },
    primary: {
      tonal: 'bg-primary-500/15 text-primary-300 ring-1 ring-inset ring-primary-500/25',
      outline: 'text-primary-300 ring-1 ring-inset ring-primary-500/40',
      solid: 'bg-primary-500 text-white',
    },
    success: {
      tonal: 'bg-success-500/15 text-success-300 ring-1 ring-inset ring-success-500/25',
      outline: 'text-success-300 ring-1 ring-inset ring-success-500/40',
      solid: 'bg-success-500 text-white',
    },
    warning: {
      tonal: 'bg-warning-500/15 text-warning-300 ring-1 ring-inset ring-warning-500/25',
      outline: 'text-warning-300 ring-1 ring-inset ring-warning-500/40',
      solid: 'bg-warning-500 text-white',
    },
    error: {
      tonal: 'bg-error-500/15 text-error-300 ring-1 ring-inset ring-error-500/25',
      outline: 'text-error-300 ring-1 ring-inset ring-error-500/40',
      solid: 'bg-error-500 text-white',
    },
    info: {
      tonal: 'bg-primary-500/15 text-primary-300 ring-1 ring-inset ring-primary-500/25',
      outline: 'text-primary-300 ring-1 ring-inset ring-primary-500/40',
      solid: 'bg-primary-500 text-white',
    },
  };

  const variantClass = $derived(TONE_VARIANT[tone]?.[variant] ?? TONE_VARIANT.neutral.tonal);
</script>

<span class="inline-flex items-center gap-1 rounded font-medium {sizeClass} {variantClass} {mono ? 'font-mono' : ''}">
  {@render children()}
</span>
