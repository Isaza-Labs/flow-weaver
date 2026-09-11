<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { devices, errorMessage, type Device } from '$lib/api/client';
  import { SearchInput, Spinner } from '$lib/components/ui';

  let {
    selected = $bindable<string[]>([]),
    environment = '',
  }: { selected?: string[]; environment?: string } = $props();

  let deviceList = $state<Device[]>([]);
  let loading = $state(true);
  let error = $state('');
  let search = $state('');

  // A device declares which environments may dispatch to it; the executor
  // drops the rest and refuses the run when that leaves nothing. Surface it
  // here so the selection can't silently resolve to zero targets.
  // An empty `environment` prop means "no environment context" (the snippet
  // test dialog), where nothing is filtered.
  const envKey = $derived(
    environment.toLowerCase() === 'qa'
      ? 'allow_qa'
      : environment.toLowerCase() === 'production'
        ? 'allow_production'
        : environment
          ? 'allow_draft'
          : '',
  );
  const isTargetable = (d: Device) =>
    !envKey || d[envKey as 'allow_draft' | 'allow_qa' | 'allow_production'] === true;

  const filteredDevices = $derived.by(() => {
    const q = search.trim().toLowerCase();
    if (!q) return deviceList;
    return deviceList.filter(
      (d) =>
        d.name.toLowerCase().includes(q) ||
        (d.ip_address || '').toLowerCase().includes(q) ||
        (d.site || '').toLowerCase().includes(q),
    );
  });

  const blockedCount = $derived(deviceList.filter((d) => !isTargetable(d)).length);

  // Already-selected devices that this environment would drop. They stay
  // unselectable-but-removable so an inherited selection (editing a
  // schedule, reopening the run dialog) can be cleaned up rather than
  // trapped.
  const selectedButBlocked = $derived(
    deviceList.filter((d) => !isTargetable(d) && selected.includes(d.id)),
  );

  // Select All operates on the *currently filtered* list — matches user
  // intent — minus anything this environment can't reach.
  const selectableFiltered = $derived(filteredDevices.filter(isTargetable));
  const allFilteredSelected = $derived(
    selectableFiltered.length > 0 && selectableFiltered.every((d) => selected.includes(d.id)),
  );

  onMount(async () => {
    try {
      const res = await devices.list(200, 0);
      deviceList = res.data;
    } catch (e) {
      error = errorMessage(e);
          toast.fromError(e, 'Action failed');
    } finally {
      loading = false;
    }
  });

  function toggleDevice(id: string) {
    if (selected.includes(id)) selected = selected.filter((s) => s !== id);
    else selected = [...selected, id];
  }

  function dropBlocked() {
    const ids = new Set(selectedButBlocked.map((d) => d.id));
    selected = selected.filter((s) => !ids.has(s));
  }

  function toggleAllFiltered() {
    const ids = selectableFiltered.map((d) => d.id);
    if (allFilteredSelected) {
      selected = selected.filter((s) => !ids.includes(s));
    } else {
      const next = new Set(selected);
      for (const id of ids) next.add(id);
      selected = [...next];
    }
  }
</script>

<div class="space-y-2">
  {#if error}
    <p class="text-xs text-error-400">{error}</p>
  {/if}

  {#if loading}
    <div class="px-1 py-2"><Spinner size="sm" label="Loading devices…" /></div>
  {:else}
    {#if envKey && blockedCount > 0}
      <div class="rounded-md border border-warning-500/40 bg-warning-500/10 px-2.5 py-2 space-y-1">
        <p class="text-[11px] text-warning-300">
          This workflow runs in the <strong>{environment}</strong> environment.
          {blockedCount} device{blockedCount === 1 ? " doesn't" : "s don't"} allow it, so
          {blockedCount === 1 ? 'it is' : 'they are'} not selectable here.
        </p>
        <p class="text-[10px] text-surface-500">
          Light up the <strong>{environment}</strong> icon in the Environments column on the
          <a href="/devices" class="underline hover:text-primary-300">Devices</a> page, or run this
          workflow from an environment they already allow.
        </p>
      </div>
    {/if}

    {#if selectedButBlocked.length > 0}
      <div class="rounded-md border border-error-500/40 bg-error-500/10 px-2.5 py-2 flex items-start gap-2">
        <p class="text-[11px] text-error-300 flex-1">
          {selectedButBlocked.length} selected device{selectedButBlocked.length === 1 ? '' : 's'}
          {selectedButBlocked.length === 1 ? "doesn't" : "don't"} allow <strong>{environment}</strong>
          and would be dropped — the run will be rejected.
        </p>
        <button
          type="button"
          onclick={dropBlocked}
          class="text-[10px] underline text-error-300 hover:text-error-400 shrink-0"
        >
          Remove them
        </button>
      </div>
    {/if}

    <SearchInput bind:value={search} placeholder="Search devices…" width="w-full" />

    <label class="flex items-center gap-2 px-2 py-1.5 rounded-md hover:bg-surface-200-800/40 cursor-pointer transition-colors">
      <input
        type="checkbox"
        checked={allFilteredSelected}
        onchange={toggleAllFiltered}
        class="w-3.5 h-3.5 rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-2 focus:ring-primary-500/30"
      />
      <span class="text-xs font-medium text-surface-700-300">
        Select all{search.trim() ? ' (filtered)' : ''} ({selectableFiltered.length})
      </span>
    </label>

    <div class="max-h-60 overflow-y-auto space-y-0.5">
      {#each filteredDevices as device (device.id)}
        {@const blocked = !isTargetable(device)}
        <label
          class="flex items-center gap-2 px-2 py-1.5 rounded-md transition-colors {blocked
            ? 'opacity-50'
            : 'hover:bg-surface-200-800/40 cursor-pointer'}"
        >
          <input
            type="checkbox"
            checked={selected.includes(device.id)}
            disabled={blocked && !selected.includes(device.id)}
            onchange={() => toggleDevice(device.id)}
            class="w-3.5 h-3.5 rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-2 focus:ring-primary-500/30 disabled:cursor-not-allowed"
          />
          <div class="flex-1 min-w-0">
            <span class="text-xs text-surface-900-100 block truncate">
              {device.name}
              {#if blocked}<span class="text-[10px] text-warning-300 ml-1">· no {environment}</span>{/if}
            </span>
            <span class="text-[10px] text-surface-500 block truncate">
              {device.ip_address || 'No IP'}
              {#if device.site} · {device.site}{/if}
              {#if device.platform} · {device.platform}{/if}
            </span>
          </div>
        </label>
      {/each}
      {#if filteredDevices.length === 0}
        <p class="text-xs text-surface-500 px-2 py-3 text-center">No devices match</p>
      {/if}
    </div>

    {#if selected.length > 0}
      <p class="text-[10px] text-primary-300 px-1">{selected.length} device{selected.length === 1 ? '' : 's'} selected</p>
    {/if}
  {/if}
</div>
