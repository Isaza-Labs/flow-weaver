<script lang="ts">
  import type { IconComponent } from './icon';
  import { animate, prefersReducedMotion } from '$lib/anim';

  let {
    label,
    value,
    icon,
    href,
    tone = 'neutral',
    sublabel,
  }: {
    label: string;
    value: string | number;
    icon?: IconComponent;
    href?: string;
    tone?: 'neutral' | 'primary' | 'success' | 'warning' | 'error';
    sublabel?: string;
  } = $props();

  const numericTarget = $derived.by<number | null>(() => {
    if (typeof value === 'number' && Number.isFinite(value)) return value;
    return null;
  });

  let valueEl = $state<HTMLDivElement | null>(null);
  let lastShown = $state(0);

  $effect(() => {
    if (numericTarget == null || !valueEl) return;
    const target = numericTarget;
    if (prefersReducedMotion()) {
      valueEl.textContent = String(target);
      lastShown = target;
      return;
    }
    const obj = { n: lastShown };
    animate(obj, {
      n: target,
      duration: 600,
      ease: 'outCubic',
      onUpdate: () => {
        if (valueEl) valueEl.textContent = String(Math.round(obj.n));
      },
      onComplete: () => {
        if (valueEl) valueEl.textContent = String(target);
        lastShown = target;
      },
    });
  });

  const toneClass = $derived({
    neutral: 'text-surface-900-100',
    primary: 'text-primary-300',
    success: 'text-success-300',
    warning: 'text-warning-300',
    error: 'text-error-300',
  }[tone]);

  const iconBg = $derived({
    neutral: 'bg-surface-200-800/60 text-surface-500',
    primary: 'bg-primary-500/15 text-primary-300 ring-1 ring-primary-500/30',
    success: 'bg-success-500/15 text-success-300 ring-1 ring-success-500/30',
    warning: 'bg-warning-500/15 text-warning-300 ring-1 ring-warning-500/30',
    error: 'bg-error-500/15 text-error-300 ring-1 ring-error-500/30',
  }[tone]);

  const Icon = $derived(icon);
  const base = 'group bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg p-4 transition-all duration-150';
  const hover = $derived(href ? 'hover:ring-surface-300-700 hover:bg-surface-100-900 cursor-pointer' : '');
</script>

{#snippet body()}
  <div class="flex items-start justify-between gap-3">
    <div class="min-w-0">
      <div class="text-[10px] font-semibold text-surface-500 uppercase tracking-wider">{label}</div>
      <div
        bind:this={valueEl}
        class="text-2xl font-semibold tracking-tight {toneClass} mt-1.5 tabular-nums leading-none"
      >{numericTarget != null ? lastShown : value}</div>
      {#if sublabel}
        <div class="text-xs text-surface-500 mt-2">{sublabel}</div>
      {/if}
    </div>
    {#if Icon}
      <div class="w-9 h-9 rounded-md flex items-center justify-center shrink-0 transition-transform duration-150 group-hover:scale-105 {iconBg}">
        <Icon size={16} />
      </div>
    {/if}
  </div>
{/snippet}

{#if href}
  <a {href} class="block {base} {hover}">{@render body()}</a>
{:else}
  <div class={base}>{@render body()}</div>
{/if}
