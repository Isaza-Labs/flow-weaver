<script lang="ts">
  import type { HTMLTextareaAttributes } from 'svelte/elements';
  import FieldHint from './FieldHint.svelte';

  type $$Props = HTMLTextareaAttributes & {
    value?: string;
    label?: string;
    hint?: string;
    /** Key into $lib/guides/fields — renders the ⓘ beside the label. */
    help?: string;
    error?: string;
    mono?: boolean;
    /**
     * The underlying <textarea>. `bind:this` on this component would hand
     * back the component instance, so bind this instead when the caller
     * needs to drive focus/selection (e.g. the chat page restoring focus
     * after a send).
     */
    element?: HTMLTextAreaElement | null;
  };

  let {
    value = $bindable(''),
    label,
    hint,
    help,
    error,
    mono = false,
    element = $bindable(null),
    id,
    rows = 4,
    class: cls = '',
    ...rest
  }: $$Props = $props();

  const inputId = $derived(id ?? `textarea-${Math.random().toString(36).slice(2, 9)}`);
  const describedById = $derived(error ? `${inputId}-error` : hint ? `${inputId}-hint` : undefined);
</script>

<div class="flex flex-col gap-1 w-full">
  {#if label}
    <label for={inputId} class="text-xs font-medium text-surface-600-400 inline-flex items-center gap-1">
      {label}
      {#if help}<FieldHint id={help} />{/if}
    </label>
  {/if}
  <textarea
    id={inputId}
    bind:value
    bind:this={element}
    {rows}
    aria-invalid={error ? 'true' : undefined}
    aria-describedby={describedById}
    class="w-full bg-surface-50-950 border rounded-md text-sm text-surface-900-100 placeholder:text-surface-500 px-3 py-2 transition-colors focus:outline-none focus:ring-2 focus:ring-primary-500/30 resize-y disabled:opacity-50 {mono ? 'font-mono text-xs' : ''} {error ? 'border-error-500' : 'border-surface-300-700 focus:border-primary-500'} {cls}"
    {...rest}
  ></textarea>
  {#if error}
    <span id="{inputId}-error" class="text-xs text-error-400">{error}</span>
  {:else if hint}
    <span id="{inputId}-hint" class="text-xs text-surface-500">{hint}</span>
  {/if}
</div>
