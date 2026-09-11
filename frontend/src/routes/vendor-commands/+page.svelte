<script lang="ts">
  import { onMount } from 'svelte';
  import { vendorCommands, errorMessage, type VendorCommand } from '$lib/api/client';
  import {
    PageHeader, Card, Button, IconButton, Badge, EmptyState, Alert, Spinner,
    Input, Select, Textarea, Dialog, SearchInput, confirm, toast,
    FieldHint,
  } from '$lib/components/ui';
  import { Terminal, Plus, Trash2, Save, RefreshCw, ChevronDown } from 'lucide-svelte';

  // Device types we ship a baseline for. Adding a new vendor on the
  // backend (DefaultVendorCommandsSeedService.Catalogs) means listing
  // it here too so the dropdown surfaces it. Users can also add their
  // own via the "Add device type" button (persisted per-browser and
  // materialised for real once a command is saved against it).
  const BASELINE_DEVICE_TYPES = [
    'cisco_ios', 'cisco_xe', 'cisco_xr', 'cisco_nxos',
    'juniper_junos', 'arista_eos',
    'nokia_sros', 'nokia_srl',
    'huawei', 'fortinet', 'linux',
  ];

  const CUSTOM_DT_KEY = 'fw:vendor-commands:custom-device-types';

  let list = $state<VendorCommand[]>([]);
  let total = $state(0);
  let loading = $state(true);
  let error = $state('');
  let filterDeviceType = $state<string>('');
  let searchQuery = $state('');
  let customDeviceTypes = $state<string[]>([]);

  // Union of the shipped baseline, any device_type already present in the
  // loaded catalog, and user-added custom types — de-duped and sorted so
  // every known device_type is selectable in the filter and both forms.
  const deviceTypes = $derived.by(() => {
    const set = new Set<string>(BASELINE_DEVICE_TYPES);
    for (const v of list) if (v.device_type) set.add(v.device_type);
    for (const t of customDeviceTypes) if (t) set.add(t);
    return Array.from(set).sort();
  });

  // Client-side search across the loaded page: value, notes, device_type,
  // kind and vendor_family. Composes with the server-side device_type filter.
  const filteredList = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return list;
    return list.filter((v) =>
      v.value.toLowerCase().includes(q) ||
      v.device_type.toLowerCase().includes(q) ||
      v.kind.toLowerCase().includes(q) ||
      (v.vendor_family?.toLowerCase().includes(q) ?? false) ||
      (v.notes?.toLowerCase().includes(q) ?? false),
    );
  });

  let showNewForm = $state(false);
  let newDeviceType = $state<string>('cisco_ios');
  let newKind = $state<'exact' | 'pattern'>('exact');
  let newValue = $state('');
  let newNotes = $state('');
  let newError = $state('');
  let creating = $state(false);

  let expanded = $state<string | null>(null);
  type EditBuf = {
    device_type: string;
    kind: 'exact' | 'pattern';
    value: string;
    notes: string;
  };
  let editBuffer = $state<Record<string, EditBuf>>({});
  let saveLoading = $state<Record<string, boolean>>({});

  // "Add device type" dialog.
  let showAddTypeDialog = $state(false);
  let newTypeName = $state('');
  let addTypeError = $state('');

  // localStorage is client-only — load it in onMount so SSR/hydration stays
  // consistent (server always renders with the baseline list).
  onMount(() => {
    customDeviceTypes = loadCustomDeviceTypes();
    load();
  });

  function loadCustomDeviceTypes(): string[] {
    try {
      const raw = localStorage.getItem(CUSTOM_DT_KEY);
      const arr = raw ? JSON.parse(raw) : [];
      return Array.isArray(arr) ? arr.filter((x): x is string => typeof x === 'string') : [];
    } catch {
      return [];
    }
  }
  function persistCustomDeviceTypes() {
    try {
      localStorage.setItem(CUSTOM_DT_KEY, JSON.stringify(customDeviceTypes));
    } catch {
      /* localStorage unavailable / quota — the in-memory list still works this session */
    }
  }

  function openAddType() {
    newTypeName = '';
    addTypeError = '';
    showAddTypeDialog = true;
  }

  function confirmAddType() {
    const name = newTypeName.trim().toLowerCase();
    if (!name) {
      addTypeError = 'Enter a device_type identifier.';
      return;
    }
    // Match the backend convention: lowercase letters, digits, underscores
    // (e.g. cisco_ios, nokia_srl).
    if (!/^[a-z0-9_]+$/.test(name)) {
      addTypeError = 'Use lowercase letters, digits and underscores only (e.g. cisco_ios).';
      return;
    }
    if (!deviceTypes.includes(name)) {
      customDeviceTypes = [...customDeviceTypes, name];
      persistCustomDeviceTypes();
    }
    // Preselect it in the new-entry form so the user can add a command for it.
    newDeviceType = name;
    showAddTypeDialog = false;
    showNewForm = true;
    toast.success(`Device type "${name}" ready — add a command for it below.`);
  }

  async function load() {
    loading = true;
    error = '';
    try {
      const res = await vendorCommands.list(filterDeviceType || null, 200, 0);
      list = res.data;
      total = res.total;
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load vendor commands');
    } finally {
      loading = false;
    }
  }

  // Reset filter on change. We keep this on the value rather than wiring
  // an `onchange` so the Select stays a controlled component.
  $effect(() => {
    // Only refetch on real changes (filterDeviceType is reactive).
    void filterDeviceType;
    load();
  });

  async function create() {
    newError = '';
    if (!newValue.trim()) {
      newError = 'value is required';
      return;
    }
    creating = true;
    try {
      await vendorCommands.create({
        device_type: newDeviceType.trim(),
        kind: newKind,
        value: newValue.trim(),
        notes: newNotes.trim() || null,
      });
      toast.success(`Added ${newKind} for ${newDeviceType}`);
      newValue = '';
      newNotes = '';
      showNewForm = false;
      await load();
    } catch (e) {
      newError = errorMessage(e);
    } finally {
      creating = false;
    }
  }

  function initEdit(v: VendorCommand) {
    editBuffer = {
      ...editBuffer,
      [v.vendor_command_id]: {
        device_type: v.device_type,
        kind: v.kind,
        value: v.value,
        notes: v.notes ?? '',
      },
    };
  }

  function toggleExpand(v: VendorCommand) {
    if (expanded === v.vendor_command_id) {
      expanded = null;
      return;
    }
    expanded = v.vendor_command_id;
    if (!editBuffer[v.vendor_command_id]) initEdit(v);
  }

  async function save(v: VendorCommand) {
    const buf = editBuffer[v.vendor_command_id];
    if (!buf) return;
    saveLoading = { ...saveLoading, [v.vendor_command_id]: true };
    try {
      await vendorCommands.update(v.vendor_command_id, {
        device_type: buf.device_type.trim(),
        kind: buf.kind,
        value: buf.value.trim(),
        notes: buf.notes.trim() || null,
      });
      toast.success('Vendor command saved');
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t save vendor command');
    } finally {
      saveLoading = { ...saveLoading, [v.vendor_command_id]: false };
    }
  }

  async function remove(v: VendorCommand) {
    if (
      !(await confirm({
        title: 'Delete catalog entry?',
        message: `Remove ${v.kind} '${v.value}' for ${v.device_type}. The validator will warn for this command in future workflow saves.`,
        tone: 'danger',
        confirmLabel: 'Delete',
      }))
    )
      return;
    try {
      await vendorCommands.delete(v.vendor_command_id);
      if (expanded === v.vendor_command_id) expanded = null;
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t delete vendor command');
    }
  }
</script>

<svelte:head><title>Vendor commands · Flow Weaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Vendor commands"
    description="Catalog used by the SSH command validator. Unknown commands surface as warnings on workflow create/update so the agent can fix typos before runs reach the device."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="secondary" icon={Plus} onclick={openAddType}>Add device type</Button>
      <Button variant="primary" icon={Plus} onclick={() => (showNewForm = !showNewForm)}>New entry</Button>
    {/snippet}
  </PageHeader>

  {#if error}
    <Alert tone="error">{error}</Alert>
  {/if}

  <Card padding="sm">
    <div class="grid grid-cols-1 md:grid-cols-3 gap-3 items-end">
      <Select label="Filter by device_type"
      help="vendor_commands.device_type_filter" bind:value={filterDeviceType}>
        <option value="">All device types</option>
        {#each deviceTypes as dt}
          <option value={dt}>{dt}</option>
        {/each}
      </Select>
      <div class="md:col-span-2 flex flex-col gap-1">
        <span class="text-xs font-medium text-surface-600-400">Search</span>
        <SearchInput
          value={searchQuery}
          width="w-full"
          placeholder="Search commands, notes, device_type, kind…"
          onInput={(v) => (searchQuery = v)}
        />
      </div>
    </div>
    <div class="text-xs text-surface-500 mt-2">
      {#if searchQuery.trim()}
        {filteredList.length} match{filteredList.length === 1 ? '' : 'es'} in the {list.length} loaded
        entr{list.length === 1 ? 'y' : 'ies'}{filterDeviceType ? ` for ${filterDeviceType}` : ''}.
      {:else}
        {total} entr{total === 1 ? 'y' : 'ies'} in catalog{filterDeviceType ? ` for ${filterDeviceType}` : ''}.
      {/if}
    </div>
  </Card>

  {#if showNewForm}
    <Card padding="none">
      <header class="flex items-center justify-between gap-3 px-4 h-12 border-b border-surface-200-800">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">New entry <FieldHint id="vendor_commands.new_section" /></h3>
        <Button size="xs" variant="ghost" onclick={() => (showNewForm = false)}>Cancel</Button>
      </header>
      <div class="px-4 py-4 space-y-3">
        {#if newError}<Alert tone="error">{newError}</Alert>{/if}
        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
          <Select label="device_type" help="vendor_commands.device_type" bind:value={newDeviceType}>
            {#each deviceTypes as dt}
              <option value={dt}>{dt}</option>
            {/each}
          </Select>
          <Select label="kind" help="vendor_commands.kind" bind:value={newKind}>
            <option value="exact">exact (literal command)</option>
            <option value="pattern">pattern (regex)</option>
          </Select>
          <div class="md:col-span-2">
            <Input
              label={newKind === 'exact' ? 'Command' : 'Pattern (regex, evaluated case-insensitively)'}
              help="vendor_commands.value"
              bind:value={newValue}
              placeholder={newKind === 'exact' ? 'show version' : '^show\\s+\\S+(\\s+\\S+)*$'}
            />
          </div>
          <div class="md:col-span-2">
            <Textarea label="Notes (optional)" help="vendor_commands.notes" bind:value={newNotes} rows={2} />
          </div>
        </div>
      </div>
      <footer class="flex items-center justify-end gap-2 px-4 py-3 border-t border-surface-200-800 bg-surface-50-950/30">
        <Button variant="ghost" onclick={() => (showNewForm = false)}>Cancel</Button>
        <Button variant="primary" icon={Save} loading={creating} onclick={create}>Add</Button>
      </footer>
    </Card>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Terminal}
        title="No vendor commands match"
        description={filterDeviceType
          ? `Catalog has no entries for ${filterDeviceType} yet. Add one or clear the filter.`
          : 'The catalog is empty — workflows will get a "validation deferred" warning for every ssh command. Click "New entry" to seed your first vendor.'}
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={() => (showNewForm = true)}>New entry</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else if filteredList.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Terminal}
        title="No entries match your search"
        description={`Nothing in the ${list.length} loaded entr${list.length === 1 ? 'y' : 'ies'} matches “${searchQuery.trim()}”. Try a different term or clear the search.`}
      >
        {#snippet actions()}
          <Button variant="ghost" onclick={() => (searchQuery = '')}>Clear search</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <div class="space-y-2">
      {#each filteredList as v (v.vendor_command_id)}
        {@const isExpanded = expanded === v.vendor_command_id}
        {@const buf = editBuffer[v.vendor_command_id]}
        <Card padding="none">
          <div class="flex items-stretch">
            <button
              type="button"
              class="flex-1 flex items-center justify-between gap-3 px-4 h-12 text-left hover:bg-surface-200-800/30 transition-colors {isExpanded ? 'rounded-t-lg' : 'rounded-l-lg'}"
              onclick={() => toggleExpand(v)}
            >
              <div class="flex items-center gap-2 min-w-0">
                <ChevronDown size={14} class="text-surface-500 transition-transform shrink-0 {isExpanded ? 'rotate-0' : '-rotate-90'}" />
                <Badge tone="neutral">{v.device_type}</Badge>
                <Badge tone={v.kind === 'exact' ? 'success' : 'warning'}>{v.kind}</Badge>
                <Badge tone={v.source === 'seed' ? 'neutral' : 'success'}>{v.source}</Badge>
                <span class="font-mono text-sm text-surface-900-100 truncate">{v.value}</span>
                {#if v.notes}
                  <span class="text-xs text-surface-500 truncate hidden lg:inline">— {v.notes}</span>
                {/if}
              </div>
            </button>
            <div class="flex items-center gap-1 px-2 border-l border-surface-200-800/60">
              <IconButton icon={Trash2} label="Delete" variant="danger" onclick={() => remove(v)} />
            </div>
          </div>
          {#if isExpanded && buf}
            <div class="border-t border-surface-200-800 px-4 py-4 space-y-3">
              <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
                <Select label="device_type" help="vendor_commands.device_type" bind:value={buf.device_type}>
                  {#each deviceTypes as dt}
                    <option value={dt}>{dt}</option>
                  {/each}
                </Select>
                <Select label="kind" help="vendor_commands.kind" bind:value={buf.kind}>
                  <option value="exact">exact</option>
                  <option value="pattern">pattern</option>
                </Select>
                <div class="md:col-span-2">
                  <Input
                    label={buf.kind === 'exact' ? 'Command' : 'Pattern (regex)'}
                help="vendor_commands.value"
                    bind:value={buf.value}
                  />
                </div>
                <div class="md:col-span-2">
                  <Textarea label="Notes" help="vendor_commands.notes" bind:value={buf.notes} rows={2} />
                </div>
              </div>
              <div class="flex justify-end">
                <Button
                  variant="primary"
                  icon={Save}
                  loading={saveLoading[v.vendor_command_id]}
                  onclick={() => save(v)}
                >
                  Save changes
                </Button>
              </div>
            </div>
          {/if}
        </Card>
      {/each}
    </div>
    <p class="text-xs text-surface-500">
      {#if searchQuery.trim()}
        {filteredList.length} match{filteredList.length === 1 ? '' : 'es'} · {list.length} of {total} loaded
      {:else}
        {list.length} of {total} entr{total === 1 ? 'y' : 'ies'}
      {/if}
    </p>
  {/if}
</div>

<!-- Add device type Dialog -->
<Dialog bind:open={showAddTypeDialog} title="Add device type" size="sm">
  <div class="space-y-3">
    <p class="text-xs text-surface-500">
      Register a device_type so you can catalog commands for a vendor that isn't in
      the shipped list. It appears in the pickers on this page immediately and becomes
      permanent once you save a command against it.
    </p>
    {#if addTypeError}<Alert tone="error">{addTypeError}</Alert>{/if}
    <Input label="device_type" help="vendor_commands.new_device_type" bind:value={newTypeName} placeholder="e.g. paloalto_panos" />
    <p class="text-xs text-surface-500">Lowercase letters, digits and underscores.</p>
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showAddTypeDialog = false)}>Cancel</Button>
    <Button variant="primary" icon={Plus} onclick={confirmAddType}>Add device type</Button>
  {/snippet}
</Dialog>
