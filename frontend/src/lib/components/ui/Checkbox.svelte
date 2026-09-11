<script lang="ts">
  import { Check, Minus } from 'lucide-svelte';
  import FieldHint from './FieldHint.svelte';

  let {
    checked = $bindable(false),
    indeterminate = false,
    disabled = false,
    label,
    // When true, `label` is used only as the accessible name (aria) and NOT
    // rendered as visible text — for bare checkboxes in dense contexts like
    // table rows. Default false: a labelled checkbox shows its text.
    labelHidden = false,
    help,
    ariaLabelledby,
    onChange,
  }: {
    checked?: boolean;
    indeterminate?: boolean;
    disabled?: boolean;
    label?: string;
    labelHidden?: boolean;
    /**
     * Key into $lib/guides/fields. Rendered as an ⓘ *beside* the control
     * rather than inside it — the checkbox itself is a <button>, and the
     * hint is one too, so nesting them would be invalid markup.
     */
    help?: string;
    ariaLabelledby?: string;
    onChange?: (next: boolean) => void;
  } = $props();

  function toggle(e: Event) {
    if (disabled) return;
    e.stopPropagation();
    checked = !checked;
    onChange?.(checked);
  }

  const showText = $derived(!!label && !labelHidden);
</script>

{#snippet boxVisual()}
  <span
    class="inline-flex items-center justify-center w-4 h-4 shrink-0 rounded border transition-colors
      {checked || indeterminate
        ? 'bg-primary-500 border-primary-500 text-white'
        : 'bg-surface-50-950 border-surface-300-700 group-hover:border-primary-400'}"
  >
    {#if indeterminate}
      <Minus size={11} />
    {:else if checked}
      <Check size={11} />
    {/if}
  </span>
{/snippet}

{#if showText}
  <span class="inline-flex items-center gap-1">
    <!-- One focusable control: clicking the box OR the text toggles. -->
    <button
      type="button"
      role="checkbox"
      aria-checked={indeterminate ? 'mixed' : checked}
      {disabled}
      onclick={toggle}
      class="group inline-flex items-center gap-2 text-left cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
    >
      {@render boxVisual()}
      <span class="text-sm text-surface-800-200 leading-tight">{label}</span>
    </button>
    {#if help}<FieldHint id={help} />{/if}
  </span>
{:else}
  <button
    type="button"
    role="checkbox"
    aria-checked={indeterminate ? 'mixed' : checked}
    aria-label={ariaLabelledby ? undefined : label}
    aria-labelledby={ariaLabelledby}
    {disabled}
    onclick={toggle}
    class="group inline-flex items-center justify-center w-4 h-4 shrink-0 rounded border transition-colors cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed
      {checked || indeterminate
        ? 'bg-primary-500 border-primary-500 text-white'
        : 'bg-surface-50-950 border-surface-300-700 hover:border-primary-400'}"
  >
    {#if indeterminate}
      <Minus size={11} />
    {:else if checked}
      <Check size={11} />
    {/if}
  </button>
{/if}
