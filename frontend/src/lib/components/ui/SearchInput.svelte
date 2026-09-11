<script lang="ts">
  import { Search, X } from 'lucide-svelte';

  let {
    value = $bindable(''),
    placeholder = 'Search…',
    width = 'w-64',
    onInput,
  }: {
    value?: string;
    placeholder?: string;
    width?: string;
    onInput?: (v: string) => void;
  } = $props();

  function handleInput(e: Event) {
    const v = (e.target as HTMLInputElement).value;
    value = v;
    onInput?.(v);
  }

  function clear() {
    value = '';
    onInput?.('');
  }
</script>

<div class="relative {width}">
  <span class="absolute left-2.5 top-1/2 -translate-y-1/2 text-surface-500 pointer-events-none">
    <Search size={14} />
  </span>
  <input
    type="text"
    role="searchbox"
    aria-label={placeholder}
    {value}
    {placeholder}
    oninput={handleInput}
    class="w-full h-8 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm text-surface-900-100 placeholder:text-surface-500 pl-8 pr-8 transition-colors focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30"
  />
  {#if value}
    <button
      type="button"
      onclick={clear}
      aria-label="Clear search"
      class="absolute right-2 top-1/2 -translate-y-1/2 text-surface-500 hover:text-surface-800-200 transition-colors"
    >
      <X size={14} />
    </button>
  {/if}
</div>
