<script lang="ts">
  import { Input } from '$lib/components/ui';
  import { X, Search } from 'lucide-svelte';

  // A searchable multi-select: type to filter real, existing values (device
  // roles / pools / devices), click (or Enter) to add them as chips — no free
  // typing, so no typos or invented values.
  //
  // Presentational only: the parent supplies `options` (loaded from the API)
  // and owns `values` (two-way bound); `onChange` fires after add/remove so the
  // parent can persist. A value present in `values` but missing from `options`
  // (e.g. a device deleted after the grant was made) still renders as a
  // removable chip.
  type Option = { value: string; label: string };

  let {
    label,
    options = [],
    values = $bindable([]),
    onChange,
    mono = false,
    emptyHint = 'None available',
  }: {
    label: string;
    options?: Option[];
    values: string[];
    onChange?: () => void;
    mono?: boolean;
    emptyHint?: string;
  } = $props();

  let query = $state('');
  let open = $state(false);

  // Not-yet-selected options, filtered by the search query.
  const available = $derived(options.filter((o) => !values.includes(o.value)));
  const filtered = $derived(
    query.trim() === ''
      ? available
      : available.filter((o) => o.label.toLowerCase().includes(query.trim().toLowerCase())),
  );

  function labelFor(v: string): string {
    const found = options.find((o) => o.value === v);
    if (found) return found.label;
    return mono && v.length > 12 ? `${v.slice(0, 8)}…` : v;
  }

  function pick(v: string) {
    if (!values.includes(v)) {
      values = [...values, v];
      onChange?.();
    }
    query = '';
  }

  function remove(v: string) {
    values = values.filter((x) => x !== v);
    onChange?.();
  }

  function onKey(e: KeyboardEvent) {
    if (e.key === 'Enter') {
      e.preventDefault();
      if (filtered.length > 0) pick(filtered[0].value);
    } else if (e.key === 'Escape') {
      open = false;
      query = '';
    }
  }
</script>

<div>
  <span class="block text-xs uppercase tracking-wide text-surface-500 mb-1">{label}</span>

  {#if values.length > 0}
    <div class="flex flex-wrap items-center gap-1.5 mb-1">
      {#each values as v (v)}
        <span
          class="inline-flex items-center gap-1 bg-surface-100-900 border border-surface-300-700 rounded-full px-2 py-0.5 text-xs {mono
            ? 'font-mono'
            : ''}"
        >
          {labelFor(v)}
          <button type="button" class="hover:text-error-400" onclick={() => remove(v)} aria-label="Remove">
            <X size={10} />
          </button>
        </span>
      {/each}
    </div>
  {/if}

  <div class="relative">
    <Input
      icon={Search}
      bind:value={query}
      placeholder={options.length === 0 ? emptyHint : 'Search…'}
      disabled={options.length === 0}
      onfocus={() => (open = true)}
      onblur={() => (open = false)}
      onkeydown={onKey}
    />
    {#if open && options.length > 0}
      <!-- onmousedown + preventDefault keeps focus on the input so the list
           doesn't blur-close before the click registers. -->
      <div
        class="absolute z-20 mt-1 w-full max-h-52 overflow-auto rounded-md border border-surface-300-700 bg-surface-50-950 shadow-lg"
      >
        {#if filtered.length === 0}
          <div class="px-2.5 py-2 text-xs text-surface-500">
            {available.length === 0 ? 'All added' : 'No matches'}
          </div>
        {:else}
          {#each filtered as o (o.value)}
            <button
              type="button"
              class="block w-full text-left px-2.5 py-1.5 text-sm text-surface-900-100 hover:bg-primary-500/10 {mono
                ? 'font-mono text-xs'
                : ''}"
              onmousedown={(e) => {
                e.preventDefault();
                pick(o.value);
              }}
            >{o.label}</button>
          {/each}
        {/if}
      </div>
    {/if}
  </div>
</div>
