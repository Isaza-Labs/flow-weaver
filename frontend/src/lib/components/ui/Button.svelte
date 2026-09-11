<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { IconComponent } from './icon';

  let {
    variant = 'secondary',
    size = 'md',
    type = 'button',
    href,
    disabled = false,
    loading = false,
    icon,
    iconRight,
    fullWidth = false,
    title,
    onclick,
    children,
  }: {
    variant?: 'primary' | 'secondary' | 'ghost' | 'danger' | 'success';
    size?: 'xs' | 'sm' | 'md';
    type?: 'button' | 'submit' | 'reset';
    href?: string;
    disabled?: boolean;
    loading?: boolean;
    icon?: IconComponent;
    iconRight?: IconComponent;
    fullWidth?: boolean;
    title?: string;
    onclick?: (e: MouseEvent) => void;
    children?: Snippet;
  } = $props();

  const sizeClass = $derived({
    xs: 'h-6 px-2 text-xs gap-1',
    sm: 'h-7 px-2.5 text-xs gap-1.5',
    md: 'h-8 px-3 text-sm gap-1.5',
  }[size]);

  const variantClass = $derived({
    primary: 'bg-primary-500 hover:bg-primary-400 text-white ring-1 ring-primary-600/50 shadow-sm shadow-primary-500/20',
    secondary: 'bg-surface-200-800/70 hover:bg-surface-200-800 text-surface-800-200 ring-1 ring-surface-300-700/80',
    ghost: 'bg-transparent hover:bg-surface-200-800/60 text-surface-700-300 ring-1 ring-transparent',
    danger: 'bg-error-500/10 hover:bg-error-500/20 text-error-300 ring-1 ring-error-500/30',
    success: 'bg-success-500/15 hover:bg-success-500/25 text-success-300 ring-1 ring-success-500/30',
  }[variant]);

  const Icon = $derived(icon);
  const IconRight = $derived(iconRight);
  const iconSize = $derived(size === 'xs' ? 12 : size === 'sm' ? 13 : 14);

  // Game-feel: `active:scale-[0.96]` gives every button a tactile "press"
  // when clicked. `transition-[colors,transform]` keeps both the colour
  // hover transition and the press-spring on the same timing. Honoured by
  // `prefers-reduced-motion` via the global stylesheet.
  const base = 'btn-fw inline-flex items-center justify-center rounded-md font-medium cursor-pointer select-none transition-[background-color,border-color,color,box-shadow,transform] duration-150 active:scale-[0.96] disabled:opacity-50 disabled:cursor-not-allowed disabled:pointer-events-none disabled:active:scale-100 focus:outline-none focus-visible:ring-2 focus-visible:ring-primary-500/50 focus-visible:ring-offset-2 focus-visible:ring-offset-surface-50-950 whitespace-nowrap';
</script>

{#if href}
  <a
    href={disabled ? undefined : href}
    {title}
    tabindex={disabled ? -1 : undefined}
    class="{base} {sizeClass} {variantClass} {fullWidth ? 'w-full' : ''} {disabled ? 'pointer-events-none' : ''}"
    aria-disabled={disabled}
  >
    {#if Icon}<Icon size={iconSize} />{/if}
    {#if children}{@render children()}{/if}
    {#if IconRight}<IconRight size={iconSize} />{/if}
  </a>
{:else}
  <button
    {type}
    {onclick}
    {title}
    disabled={disabled || loading}
    aria-busy={loading}
    class="{base} {sizeClass} {variantClass} {fullWidth ? 'w-full' : ''}"
  >
    {#if loading}
      <span class="inline-block w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
    {:else if Icon}
      <Icon size={iconSize} />
    {/if}
    {#if children}{@render children()}{/if}
    {#if IconRight && !loading}<IconRight size={iconSize} />{/if}
  </button>
{/if}
