<script lang="ts">
  import type { IconComponent } from './icon';

  let {
    icon,
    label,
    size = 'sm',
    variant = 'ghost',
    disabled = false,
    loading = false,
    type = 'button',
    href,
    onclick,
  }: {
    icon: IconComponent;
    label: string;
    size?: 'xs' | 'sm' | 'md';
    variant?: 'ghost' | 'secondary' | 'danger' | 'success';
    disabled?: boolean;
    loading?: boolean;
    type?: 'button' | 'submit';
    href?: string;
    onclick?: (e: MouseEvent) => void;
  } = $props();

  const sizeClass = $derived({
    xs: 'w-5 h-5',
    sm: 'w-7 h-7',
    md: 'w-8 h-8',
  }[size]);

  const iconSize = $derived(size === 'xs' ? 12 : size === 'sm' ? 14 : 16);

  const variantClass = $derived({
    ghost: 'text-surface-500 hover:text-surface-800-200 hover:bg-surface-200-800/60',
    secondary: 'bg-surface-200-800 text-surface-700-300 hover:bg-surface-300-700',
    danger: 'text-error-400 hover:bg-error-500/10',
    // "On" look — a green tint so a toggled-on control reads as powered.
    success: 'bg-success-500/15 text-success-400 hover:bg-success-500/25',
  }[variant]);

  const Icon = $derived(icon);
</script>

{#if href}
  <a
    {href}
    aria-label={label}
    title={label}
    class="inline-flex items-center justify-center rounded transition-colors {sizeClass} {variantClass}"
  >
    <Icon size={iconSize} />
  </a>
{:else}
  <button
    {type}
    {onclick}
    disabled={disabled || loading}
    aria-label={label}
    aria-busy={loading}
    title={label}
    class="inline-flex items-center justify-center rounded transition-colors disabled:opacity-50 disabled:cursor-not-allowed {sizeClass} {variantClass}"
  >
    {#if loading}
      <span
        class="inline-block border-2 border-current border-t-transparent rounded-full animate-spin"
        style="width:{iconSize}px;height:{iconSize}px"
      ></span>
    {:else}
      <Icon size={iconSize} />
    {/if}
  </button>
{/if}
