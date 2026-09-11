<script lang="ts">
  import type { HTMLInputAttributes } from 'svelte/elements';
  import type { IconComponent } from './icon';
  import { Eye, EyeOff } from 'lucide-svelte';
  import FieldHint from './FieldHint.svelte';

  type $$Props = HTMLInputAttributes & {
    value?: string | number;
    label?: string;
    hint?: string;
    /**
     * Key into the $lib/guides/fields registry. Renders the ⓘ next to the
     * label. Distinct from `hint`, which is the inline helper line rendered
     * *below* the control — the two can be used together.
     */
    help?: string;
    error?: string;
    icon?: IconComponent;
    fullWidth?: boolean;
    revealable?: boolean;
  };

  let {
    value = $bindable(''),
    label,
    hint,
    help,
    error,
    icon,
    fullWidth = true,
    revealable = false,
    type = 'text',
    id,
    class: cls = '',
    ...rest
  }: $$Props = $props();

  const Icon = $derived(icon);
  const inputId = $derived(id ?? `input-${Math.random().toString(36).slice(2, 9)}`);
  // Link the hint/error message to the control for screen readers.
  const describedById = $derived(error ? `${inputId}-error` : hint ? `${inputId}-hint` : undefined);

  // Reveal toggle — only meaningful for password fields. Swaps the rendered
  // type so the user can verify a value they typed/pasted; it never exposes a
  // stored secret (the field holds only what the user entered).
  let revealed = $state(false);
  const showReveal = $derived(revealable && type === 'password');
  const effectiveType = $derived(showReveal && revealed ? 'text' : type);
</script>

<div class="flex flex-col gap-1 {fullWidth ? 'w-full' : ''}">
  {#if label}
    <label for={inputId} class="text-xs font-medium text-surface-600-400 inline-flex items-center gap-1">
      {label}
      {#if help}<FieldHint id={help} />{/if}
    </label>
  {/if}
  <div class="relative">
    {#if Icon}
      <span class="absolute left-2.5 top-1/2 -translate-y-1/2 text-surface-500 pointer-events-none">
        <Icon size={14} />
      </span>
    {/if}
    <input
      id={inputId}
      type={effectiveType}
      bind:value
      aria-invalid={error ? 'true' : undefined}
      aria-describedby={describedById}
      class="w-full h-8 bg-surface-50-950 border rounded-md text-sm text-surface-900-100 placeholder:text-surface-500 transition-colors focus:outline-none focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50 {Icon ? 'pl-8' : 'pl-3'} {showReveal ? 'pr-9' : 'pr-3'} {error ? 'border-error-500' : 'border-surface-300-700 focus:border-primary-500'} {cls}"
      {...rest}
    />
    {#if showReveal}
      <button
        type="button"
        onclick={() => (revealed = !revealed)}
        aria-label={revealed ? 'Hide value' : 'Show value'}
        aria-pressed={revealed}
        class="absolute right-2 top-1/2 -translate-y-1/2 text-surface-500 hover:text-surface-800-200 transition-colors cursor-pointer"
      >
        {#if revealed}<EyeOff size={14} />{:else}<Eye size={14} />{/if}
      </button>
    {/if}
  </div>
  {#if error}
    <span id="{inputId}-error" class="text-xs text-error-400">{error}</span>
  {:else if hint}
    <span id="{inputId}-hint" class="text-xs text-surface-500">{hint}</span>
  {/if}
</div>
