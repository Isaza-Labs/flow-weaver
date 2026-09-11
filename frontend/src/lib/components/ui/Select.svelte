<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { HTMLSelectAttributes } from 'svelte/elements';
  import { ChevronDown } from 'lucide-svelte';
  import FieldHint from './FieldHint.svelte';

  type $$Props = HTMLSelectAttributes & {
    value?: string | number;
    label?: string;
    hint?: string;
    /** Key into $lib/guides/fields — renders the ⓘ beside the label. */
    help?: string;
    error?: string;
    children: Snippet;
  };

  let {
    value = $bindable(''),
    label,
    hint,
    help,
    error,
    id,
    class: cls = '',
    children,
    ...rest
  }: $$Props = $props();

  const selectId = $derived(id ?? `select-${Math.random().toString(36).slice(2, 9)}`);
  const describedById = $derived(error ? `${selectId}-error` : hint ? `${selectId}-hint` : undefined);
</script>

<div class="flex flex-col gap-1 w-full">
  {#if label}
    <label for={selectId} class="text-xs font-medium text-surface-600-400 inline-flex items-center gap-1">
      {label}
      {#if help}<FieldHint id={help} />{/if}
    </label>
  {/if}
  <div class="relative">
    <select
      id={selectId}
      bind:value
      aria-invalid={error ? 'true' : undefined}
      aria-describedby={describedById}
      class="w-full h-8 appearance-none bg-surface-50-950 border rounded-md text-sm text-surface-900-100 pl-3 pr-8 transition-colors focus:outline-none focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50 {error ? 'border-error-500' : 'border-surface-300-700 focus:border-primary-500'} {cls}"
      {...rest}
    >
      {@render children()}
    </select>
    <span class="absolute right-2 top-1/2 -translate-y-1/2 text-surface-500 pointer-events-none">
      <ChevronDown size={14} />
    </span>
  </div>
  {#if error}
    <span id="{selectId}-error" class="text-xs text-error-400">{error}</span>
  {:else if hint}
    <span id="{selectId}-hint" class="text-xs text-surface-500">{hint}</span>
  {/if}
</div>
