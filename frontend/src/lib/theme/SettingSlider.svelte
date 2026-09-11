<script lang="ts">
  // One numeric style knob in the theme editor: label, slider, live value
  // readout and a reset. Mirrors ColorField's row anatomy so the Style
  // section reads as a continuation of the palette list.
  import { RotateCcw } from 'lucide-svelte';

  let {
    label,
    hint,
    min,
    max,
    step,
    fallback,
    format = (v: number) => String(v),
    disabled = false,
    value = $bindable(),
  }: {
    label: string;
    hint: string;
    min: number;
    max: number;
    step: number;
    /** The app default the reset returns to. */
    fallback: number;
    format?: (v: number) => string;
    disabled?: boolean;
    value: number;
  } = $props();

  const isDefault = $derived(value === fallback);
</script>

<div class="flex items-center gap-2.5 py-2.5">
  <div class="min-w-0 flex-1">
    <div class="text-xs font-medium text-surface-900-100">{label}</div>
    <div class="text-[10px] text-surface-500 truncate">{hint}</div>
  </div>
  <input
    type="range"
    {min}
    {max}
    {step}
    {disabled}
    bind:value
    aria-label={label}
    class="w-36 shrink-0 accent-[var(--color-primary-500)] cursor-pointer disabled:cursor-not-allowed"
  />
  <span class="w-12 shrink-0 text-right font-mono text-[11px] tabular-nums text-surface-700-300">
    {format(value)}
  </span>
  <button
    type="button"
    onclick={() => (value = fallback)}
    disabled={disabled || isDefault}
    title="Reset to the FlowWeaver default"
    aria-label="Reset {label} to the FlowWeaver default"
    class="shrink-0 w-7 h-7 inline-flex items-center justify-center rounded-md text-surface-500 hover:text-surface-900-100 hover:bg-surface-200-800/60 disabled:opacity-30 disabled:pointer-events-none transition-colors cursor-pointer"
  >
    <RotateCcw size={12} />
  </button>
</div>
