<script lang="ts">
  import type { Device } from '$lib/api/client';
  import { Input, FieldHint } from '$lib/components/ui';
  import { clickOutside } from '$lib/utils/clickOutside';
  import { X, Check, Search } from 'lucide-svelte';

  interface Props {
    devices: Device[];
    selected: string[];
    loading?: boolean;
    onChange: (ids: string[]) => void;
  }

  let { devices, selected, loading = false, onChange }: Props = $props();

  let filter = $state('');
  let open = $state(false);

  const selectedSet = $derived(new Set(selected));

  // Cap the dropdown to a manageable count — inventories with hundreds of
  // devices still need to search by text rather than scroll a giant list.
  const DROPDOWN_LIMIT = 30;

  const filtered = $derived.by(() => {
    const q = filter.trim().toLowerCase();
    const matches = devices.filter((d) => {
      if (!q) return true;
      return (
        d.name.toLowerCase().includes(q) ||
        (d.ip_address ?? '').toLowerCase().includes(q) ||
        (d.site ?? '').toLowerCase().includes(q) ||
        (d.role ?? '').toLowerCase().includes(q) ||
        (d.vendor ?? '').toLowerCase().includes(q)
      );
    });
    return matches.slice(0, DROPDOWN_LIMIT);
  });

  const totalMatches = $derived.by(() => {
    const q = filter.trim().toLowerCase();
    if (!q) return devices.length;
    return devices.filter((d) =>
      d.name.toLowerCase().includes(q) ||
      (d.ip_address ?? '').toLowerCase().includes(q) ||
      (d.site ?? '').toLowerCase().includes(q) ||
      (d.role ?? '').toLowerCase().includes(q) ||
      (d.vendor ?? '').toLowerCase().includes(q),
    ).length;
  });

  function toggle(id: string) {
    const next = new Set(selectedSet);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    onChange([...next]);
  }

  function remove(id: string) {
    onChange(selected.filter((s) => s !== id));
  }

  function clearAll() {
    onChange([]);
  }

  function deviceById(id: string): Device | undefined {
    return devices.find((d) => d.id === id);
  }

  function chipLabel(id: string): string {
    const d = deviceById(id);
    if (!d) return id.slice(0, 8) + '…';
    return d.ip_address ? `${d.name} · ${d.ip_address}` : d.name;
  }
</script>

<div class="space-y-2">
  <div class="flex items-center justify-between">
    <label for="device-picker-filter" class="text-xs font-medium text-surface-700-300">
      Devices <FieldHint id="pools.members" /> <span class="text-surface-500">· {selected.length} selected</span>
    </label>
    {#if selected.length > 0}
      <button
        type="button"
        class="text-[11px] text-surface-500 hover:text-error-300"
        onclick={clearAll}
      >
        Clear all
      </button>
    {/if}
  </div>

  {#if selected.length > 0}
    <div class="flex flex-wrap gap-1.5 p-2 rounded-md border border-surface-300-700 bg-surface-50-950">
      {#each selected as id (id)}
        <span class="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-primary-500/15 ring-1 ring-primary-500/30 text-xs text-primary-200">
          <span class="truncate max-w-[240px]">{chipLabel(id)}</span>
          <button
            type="button"
            class="hover:text-error-300"
            onclick={() => remove(id)}
            aria-label="Remove device"
          >
            <X size={12} />
          </button>
        </span>
      {/each}
    </div>
  {/if}

  <div class="relative" use:clickOutside={() => (open = false)}>
    <Input
      id="device-picker-filter"
      bind:value={filter}
      placeholder={loading ? 'Loading devices…' : 'Search devices by name, IP, site, role…'}
      icon={Search}
      onfocus={() => (open = true)}
      onkeydown={(e: KeyboardEvent) => { if (e.key === 'Escape') open = false; }}
      disabled={loading}
    />

    {#if open}
      <div class="mt-1 max-h-64 overflow-y-auto rounded-md border border-surface-300-700 bg-surface-100-900 shadow-lg">
        {#if devices.length === 0}
          <div class="p-3 text-xs text-surface-500">
            {loading ? 'Loading…' : 'No devices yet.'}
          </div>
        {:else if filtered.length === 0}
          <div class="p-3 text-xs text-surface-500">No devices match “{filter}”.</div>
        {:else}
          <ul class="divide-y divide-surface-200-800/50">
            {#each filtered as d (d.id)}
              {@const isSelected = selectedSet.has(d.id)}
              <li>
                <button
                  type="button"
                  class="w-full px-3 py-2 text-left hover:bg-surface-200-800/40 flex items-center gap-2 transition-colors"
                  onclick={() => toggle(d.id)}
                >
                  <span class="w-4 h-4 rounded border border-surface-400-600 flex items-center justify-center shrink-0 {isSelected ? 'bg-primary-500 border-primary-500' : ''}">
                    {#if isSelected}<Check size={12} class="text-white" />{/if}
                  </span>
                  <div class="flex-1 min-w-0">
                    <div class="text-sm text-surface-900-100 truncate">
                      {d.name}
                      {#if d.ip_address}
                        <span class="text-surface-500 font-mono ml-1">{d.ip_address}</span>
                      {/if}
                    </div>
                    <div class="text-[11px] text-surface-500 truncate">
                      {[d.vendor, d.platform, d.site, d.role].filter(Boolean).join(' · ') || '—'}
                    </div>
                  </div>
                </button>
              </li>
            {/each}
          </ul>
          {#if totalMatches > filtered.length}
            <div class="p-2 text-center text-[11px] text-surface-500 border-t border-surface-200-800/50">
              Showing {filtered.length} of {totalMatches}. Refine the search to see more.
            </div>
          {/if}
        {/if}
        <div class="p-2 border-t border-surface-200-800/50 flex justify-end">
          <button
            type="button"
            class="text-xs text-surface-600-400 hover:text-surface-900-100"
            onclick={() => (open = false)}
          >
            Close
          </button>
        </div>
      </div>
    {/if}
  </div>
</div>
