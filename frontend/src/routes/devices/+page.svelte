<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import {
    devices, credentials, errorMessage,
    type Device, type Credential,
  } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, IconButton, EmptyState,
    Alert, Spinner, Input, Select, Dialog, SearchInput, confirm, FieldHint,
  } from '$lib/components/ui';
  import {
    Server, Plus, Trash2, Pencil, Save, KeyRound, AlertTriangle,
    Hammer, FlaskConical, Rocket,
  } from 'lucide-svelte';

  type DeviceFormFields = {
    name: string; ip_address: string; platform: string; vendor: string;
    site: string; role: string; credential_id: string;
    // Pinned SSH host key. Blank = unpinned; the backend then pins whatever
    // the first successful connect presents (trust-on-first-use), and blanking
    // an existing pin re-arms that. Only surfaced in the edit dialog — at
    // create time nobody has connected yet, so there is no key to paste.
    expected_ssh_host_key_fingerprint: string;
  };
  const emptyForm: DeviceFormFields = {
    name: '', ip_address: '', platform: '', vendor: '',
    site: '', role: '', credential_id: '', expected_ssh_host_key_fingerprint: '',
  };

  // GUID of all zeros — what the backend stores when a Device row has
  // no credential assigned (Device.CredentialId is Guid, not Guid?).
  // The SSH handler treats this as "no credential" as of Phase 2; the
  // UI surfaces the same semantics so users aren't confused by a blank
  // dropdown vs "00000…". Empty string from the <select> maps to this
  // on the wire — see normalizeCredentialId below.
  const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

  let deviceList = $state<Device[]>([]);
  let credentialList = $state<Credential[]>([]);
  let loading = $state(true);
  let error = $state('');
  let showForm = $state(false);
  let creating = $state(false);
  let newDevice = $state<DeviceFormFields>({ ...emptyForm });

  // Edit-dialog state. `editing` holds the device we're editing so we can
  // fall back to it if the user needs to retry after a server error.
  let showEdit = $state(false);
  let editing = $state<Device | null>(null);
  let editForm = $state<DeviceFormFields>({ ...emptyForm });
  let editSaving = $state(false);
  let editError = $state<string | null>(null);

  // Client-side name filter. Kept client-side (not a backend query
  // param) because the list is already paged at 50 rows and the
  // response is small; round-tripping for every keystroke would
  // feel laggier than filtering in memory.
  let nameFilter = $state('');

  // Bulk-assign state. Multi-select via checkboxes in the table; the
  // action button opens a dialog with the credential picker and applies
  // the chosen id to every selected device. Useful when a NetBox sync
  // dropped 50 devices without credentials and we'd rather not edit
  // each one by hand.
  let selectedIds = $state<Set<string>>(new Set());
  let showBulk = $state(false);
  let bulkCredentialId = $state('');
  let bulkSaving = $state(false);
  let bulkError = $state<string | null>(null);

  const credentialMap = $derived(new Map(credentialList.map(c => [c.credential_id, c])));
  const devicesMissingCredential = $derived(
    deviceList.filter(d => !d.credential_id || d.credential_id === EMPTY_GUID).length,
  );

  // Filter applies to name / ip / site / role — most of the columns a
  // human scans. Match is substring, case-insensitive, because the
  // operator typing "node-l" wants every device whose name contains
  // that fragment regardless of the casing NetBox happens to use.
  const filteredDevices = $derived.by(() => {
    const q = nameFilter.trim().toLowerCase();
    if (!q) return deviceList;
    return deviceList.filter(d => {
      const hay = [d.name, d.ip_address, d.site, d.role]
        .filter(Boolean)
        .map(s => s!.toLowerCase());
      return hay.some(h => h.includes(q));
    });
  });

  onMount(async () => {
    await Promise.all([loadDevices(), loadCredentials()]);
  });

  async function loadDevices() {
    loading = true;
    error = '';
    try {
      const res = await devices.list();
      deviceList = res.data;
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load devices');
    } finally {
      loading = false;
    }
  }

  async function loadCredentials() {
    try {
      const res = await credentials.list();
      credentialList = res.data;
    } catch (e) {
      // Non-fatal — the rest of the page still works, just without the
      // credential dropdown populated. Log via toast so the admin sees it.
      toast.fromError(e, 'Couldn’t load credentials (picker will be empty)');
    }
  }

  // Empty string from the <select> means "(none)"; translate that to
  // EMPTY_GUID so the PUT body explicitly unsets the credential. A
  // populated value flows through as-is.
  function normalizeCredentialId(raw: string): string {
    return raw && raw.trim() ? raw : EMPTY_GUID;
  }

  function credentialLabel(id: string | null | undefined): string {
    if (!id || id === EMPTY_GUID) return '—';
    const c = credentialMap.get(id);
    if (!c) return `unknown (${id.slice(0, 8)}…)`;
    return c.name;
  }

  async function createDevice(e: Event) {
    e.preventDefault();
    if (creating) return;

    creating = true;
    error = '';
    try {
      await devices.create({
        ...newDevice,
        credential_id: normalizeCredentialId(newDevice.credential_id),
      });
      newDevice = { ...emptyForm };
      showForm = false;
      await loadDevices();
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t create device');
    } finally {
      creating = false;
    }
  }

  function openEdit(d: Device) {
    editing = d;
    editForm = {
      name: d.name ?? '',
      ip_address: d.ip_address ?? '',
      platform: d.platform ?? '',
      vendor: d.vendor ?? '',
      site: d.site ?? '',
      role: d.role ?? '',
      credential_id: d.credential_id && d.credential_id !== EMPTY_GUID ? d.credential_id : '',
      expected_ssh_host_key_fingerprint: d.expected_ssh_host_key_fingerprint ?? '',
    };
    editError = null;
    showEdit = true;
  }

  async function saveEdit(e: Event) {
    e.preventDefault();
    if (!editing || editSaving) return;
    editSaving = true;
    editError = null;
    try {
      await devices.update(editing.id, {
        ...editForm,
        credential_id: normalizeCredentialId(editForm.credential_id),
      });
      toast.success(`Updated ${editForm.name || editing.name}`);
      showEdit = false;
      editing = null;
      await loadDevices();
    } catch (err) {
      editError = errorMessage(err);
      toast.fromError(err, 'Couldn’t save device');
    } finally {
      editSaving = false;
    }
  }

  // Environments a run may dispatch to this device from. Independent
  // toggles — any combination is valid. Turning all three off parks the
  // device: no run can target it, and the executor says so rather than
  // resolving to an empty fan-out.
  //
  // One distinct icon per environment so a row is readable at a glance
  // without reading three labels: hammer = still being built, flask = the
  // QA lab (the icon this control has always used), rocket = live.
  const ENVIRONMENTS = [
    { key: 'allow_draft', label: 'draft', icon: Hammer },
    { key: 'allow_qa', label: 'qa', icon: FlaskConical },
    { key: 'allow_production', label: 'production', icon: Rocket },
  ] as const;

  type EnvKey = (typeof ENVIRONMENTS)[number]['key'];

  async function toggleEnvironment(d: Device, key: EnvKey) {
    try {
      await devices.update(d.id, { [key]: !d[key] });
      await loadDevices();
    } catch (e) {
      toast.fromError(e, 'Couldn’t update environments');
    }
  }

  async function deleteDevice(d: Device) {
    if (!(await confirm({ title: `Delete ${d.name}?`, message: 'This cannot be undone.', tone: 'danger', confirmLabel: 'Delete' }))) return;
    try {
      await devices.delete(d.id);
      selectedIds.delete(d.id);
      selectedIds = new Set(selectedIds);
      await loadDevices();
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t delete device');
    }
  }

  function toggleSelection(id: string) {
    if (selectedIds.has(id)) selectedIds.delete(id);
    else selectedIds.add(id);
    selectedIds = new Set(selectedIds);
  }

  function toggleSelectAll() {
    // Scope "select all" to what's currently visible. With a filter
    // active, selecting 3 out of 500 means "these 3", not "every row in
    // every row". This mirrors how GitHub / Linear handle bulk select
    // under an active filter.
    const visibleIds = filteredDevices.map(d => d.id);
    const allVisibleSelected = visibleIds.length > 0
      && visibleIds.every(id => selectedIds.has(id));
    if (allVisibleSelected) {
      for (const id of visibleIds) selectedIds.delete(id);
    } else {
      for (const id of visibleIds) selectedIds.add(id);
    }
    selectedIds = new Set(selectedIds);
  }

  function openBulk() {
    if (selectedIds.size === 0) return;
    bulkCredentialId = '';
    bulkError = null;
    showBulk = true;
  }

  async function runBulkAssign() {
    if (bulkSaving || selectedIds.size === 0) return;
    bulkSaving = true;
    bulkError = null;
    const target = normalizeCredentialId(bulkCredentialId);
    const ids = Array.from(selectedIds);
    // Best-effort batch — one PUT per device. If N/M fail we surface the
    // count so the admin can retry only the outliers; we don't abort
    // mid-batch because partial success still moves the needle.
    let ok = 0;
    let failed = 0;
    for (const id of ids) {
      try {
        await devices.update(id, { credential_id: target });
        ok++;
      } catch {
        failed++;
      }
    }
    bulkSaving = false;
    showBulk = false;
    selectedIds = new Set();
    if (failed === 0) {
      toast.success(`Assigned credential to ${ok} device${ok === 1 ? '' : 's'}`);
    } else {
      toast.fromError(
        new Error(`${failed} failed`),
        `Assigned ${ok}/${ids.length} devices — ${failed} failed`,
      );
    }
    await loadDevices();
  }
</script>

<svelte:head><title>Devices · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader title="Devices" description="The fleet your workflows operate against.">
    {#snippet actions()}
      {#if selectedIds.size > 0}
        <Button variant="secondary" icon={KeyRound} onclick={openBulk}>
          Assign credential ({selectedIds.size})
        </Button>
      {/if}
      <Button variant="primary" icon={Plus} onclick={() => (showForm = !showForm)}>
        {showForm ? 'Cancel' : 'New device'}
      </Button>
    {/snippet}
  </PageHeader>

  {#if error}
    <Alert tone="error" dismissible onDismiss={() => (error = '')}>{error}</Alert>
  {/if}

  {#if !loading && devicesMissingCredential > 0}
    <Alert tone="warning">
      <div class="flex items-start gap-2">
        <AlertTriangle size={14} class="mt-0.5 shrink-0" />
        <div class="text-xs">
          <strong>{devicesMissingCredential}</strong> device{devicesMissingCredential === 1 ? '' : 's'}
          {devicesMissingCredential === 1 ? 'has' : 'have'} no credential assigned. SSH / NETCONF
          workflows against them will fail with <code class="font-mono">credential 00000000-… not found</code>.
          Edit the device{devicesMissingCredential === 1 ? '' : 's'} or bulk-assign from the table
          selection. Create credentials at <a href="/credentials" class="underline font-medium">/credentials</a>.
        </div>
      </div>
    </Alert>
  {/if}

  {#if showForm}
    <Card>
      <form onsubmit={createDevice} class="space-y-4">
        <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
          <Input label="Name" help="devices.name" bind:value={newDevice.name} required placeholder="core-sw-01" />
          <Input label="IP address" help="devices.ip" bind:value={newDevice.ip_address} required placeholder="10.0.0.1" />
          <Input label="Platform" help="devices.platform" bind:value={newDevice.platform} placeholder="cisco_ios" />
          <Input label="Vendor" help="devices.vendor" bind:value={newDevice.vendor} placeholder="Cisco" />
          <Input label="Site" help="devices.site" bind:value={newDevice.site} placeholder="DC1" />
          <Input label="Role" help="devices.role" bind:value={newDevice.role} placeholder="core" />
          <Select label="Credential" help="devices.credential" bind:value={newDevice.credential_id}>
            <option value="">(none)</option>
            {#each credentialList as c (c.credential_id)}
              <option value={c.credential_id}>{c.name} ({c.type})</option>
            {/each}
          </Select>
        </div>
        <div class="flex justify-end gap-2">
          <Button variant="ghost" onclick={() => (showForm = false)} disabled={creating}>Cancel</Button>
          <Button variant="primary" type="submit" loading={creating} disabled={!newDevice.name || !newDevice.ip_address}>Create device</Button>
        </div>
      </form>
    </Card>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading devices…" /></div>
  {:else if deviceList.length === 0}
    <Card padding="none">
      <EmptyState icon={Server} title="No devices yet" description="Add your first device to start orchestrating workflows.">
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={() => (showForm = true)}>Add device</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <div class="flex items-center justify-between gap-2">
      <SearchInput
        bind:value={nameFilter}
        placeholder="Filter by name, IP, site or role…"
        width="w-80"
      />
      {#if nameFilter}
        <span class="text-xs text-surface-500">
          {filteredDevices.length} / {deviceList.length} match{filteredDevices.length === 1 ? '' : 'es'}
        </span>
      {/if}
    </div>

    {#if filteredDevices.length === 0}
      <Card padding="none">
        <EmptyState
          icon={Server}
          title="No devices match that filter"
          description={`Nothing contains "${nameFilter}" in its name, IP, site or role.`}
        >
          {#snippet actions()}
            <Button variant="ghost" onclick={() => (nameFilter = '')}>Clear filter</Button>
          {/snippet}
        </EmptyState>
      </Card>
    {:else}
    <DataTable>
      <thead>
        <tr>
          <th style="width:2rem"><FieldHint id="devices.select_all" />
            <input
              type="checkbox"
              aria-label="Select all visible devices"
              checked={filteredDevices.length > 0
                && filteredDevices.every(d => selectedIds.has(d.id))}
              indeterminate={filteredDevices.some(d => selectedIds.has(d.id))
                && !filteredDevices.every(d => selectedIds.has(d.id))}
              onchange={toggleSelectAll}
            />
          </th>
          <th>Name</th>
          <th>IP</th>
          <th>Platform</th>
          <th>Vendor</th>
          <th>Site</th>
          <th>Role</th>
          <th>Credential</th>
          <th><span class="inline-flex items-center gap-1">Environments <FieldHint id="devices.environments" /></span></th>
          <th class="!text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each filteredDevices as device (device.id)}
          {@const missingCred = !device.credential_id || device.credential_id === EMPTY_GUID}
          <tr>
            <td>
              <input
                type="checkbox"
                aria-label={`Select ${device.name}`}
                checked={selectedIds.has(device.id)}
                onchange={() => toggleSelection(device.id)}
              />
            </td>
            <td class="font-medium text-surface-900-100">{device.name}</td>
            <td class="font-mono text-xs text-surface-700-300">{device.ip_address ?? '—'}</td>
            <td class="text-surface-700-300">{device.platform ?? '—'}</td>
            <td class="text-surface-700-300">{device.vendor ?? '—'}</td>
            <td class="text-surface-700-300">{device.site ?? '—'}</td>
            <td class="text-surface-700-300">{device.role ?? '—'}</td>
            <td>
              {#if missingCred}
                <span class="inline-flex items-center gap-1 text-[11px] text-warning-500">
                  <AlertTriangle size={10} /> none
                </span>
              {:else}
                <span class="inline-flex items-center gap-1 text-surface-700-300 text-xs">
                  <KeyRound size={10} class="text-surface-500" />
                  {credentialLabel(device.credential_id)}
                </span>
              {/if}
            </td>
            <td>
              <div class="inline-flex items-center gap-1">
                {#each ENVIRONMENTS as env (env.key)}
                  {@const on = device[env.key] === true}
                  <IconButton
                    icon={env.icon}
                    label={on
                      ? `Stop ${env.label} runs from targeting ${device.name}`
                      : `Allow ${env.label} runs to target ${device.name}`}
                    variant={on ? 'success' : 'ghost'}
                    onclick={() => toggleEnvironment(device, env.key)}
                  />
                {/each}
              </div>
              {#if !device.allow_draft && !device.allow_qa && !device.allow_production}
                <span class="flex items-center gap-1 text-[10px] text-warning-300 mt-0.5">
                  <AlertTriangle size={10} /> parked
                </span>
              {/if}
            </td>
            <td class="text-right">
              <div class="inline-flex items-center gap-1">
                <IconButton icon={Pencil} label={`Edit ${device.name}`} onclick={() => openEdit(device)} />
                <IconButton icon={Trash2} label={`Delete ${device.name}`} variant="danger" onclick={() => deleteDevice(device)} />
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
    <p class="text-xs text-surface-500">
      {#if nameFilter}
        Showing {filteredDevices.length} of {deviceList.length} device{deviceList.length === 1 ? '' : 's'}
      {:else}
        {deviceList.length} device{deviceList.length === 1 ? '' : 's'}
      {/if}
      {#if selectedIds.size > 0}
        · {selectedIds.size} selected
      {/if}
    </p>
    {/if}
  {/if}
</div>

<Dialog bind:open={showEdit} title={editing ? `Edit ${editing.name}` : 'Edit device'} size="lg">
  <form id="device-edit-form" onsubmit={saveEdit} class="space-y-4">
    {#if editError}
      <Alert tone="error">{editError}</Alert>
    {/if}
    {#if editing && (!editForm.credential_id || editForm.credential_id === EMPTY_GUID)}
      <Alert tone="warning">
        <div class="flex items-start gap-2 text-xs">
          <AlertTriangle size={12} class="mt-0.5 shrink-0" />
          <div>
            No credential assigned. SSH / NETCONF workflows against this device will fail.
            Pick one below or create it at <a href="/credentials" class="underline">/credentials</a>.
          </div>
        </div>
      </Alert>
    {/if}
    <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
      <Input label="Name" help="devices.name" bind:value={editForm.name} required disabled={editSaving} placeholder="core-sw-01" />
      <Input label="IP address" help="devices.ip" bind:value={editForm.ip_address} disabled={editSaving} placeholder="10.0.0.1" />
      <Input label="Platform" help="devices.platform" bind:value={editForm.platform} disabled={editSaving} placeholder="cisco_ios" />
      <Input label="Vendor" help="devices.vendor" bind:value={editForm.vendor} disabled={editSaving} placeholder="Cisco" />
      <Input label="Site" help="devices.site" bind:value={editForm.site} disabled={editSaving} placeholder="DC1" />
      <Input label="Role" help="devices.role" bind:value={editForm.role} disabled={editSaving} placeholder="core" />
      <Select label="Credential" help="devices.credential" bind:value={editForm.credential_id} disabled={editSaving}>
        <option value="">(none)</option>
        {#each credentialList as c (c.credential_id)}
          <option value={c.credential_id}>{c.name} ({c.type})</option>
        {/each}
      </Select>
    </div>

    <div class="space-y-1">
      <Input
        label="SSH host key fingerprint"
        help="devices.host_key_fingerprint"
        bind:value={editForm.expected_ssh_host_key_fingerprint}
        disabled={editSaving}
        placeholder="SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU"
      />
      <p class="text-xs opacity-70">
        {#if editing?.expected_ssh_host_key_fingerprint}
          Pinned — an ssh step refuses to connect if this device presents a different
          key. Clear the field only after a legitimate key rotation; the next connect
          re-pins whatever it sees.
        {:else}
          Not pinned yet. The first successful ssh step pins the key it observes; you
          can also paste it now (<code>ssh-keygen -lf</code> output, or the
          <code>host_key_fingerprint</code> an ssh step reports).
        {/if}
      </p>
    </div>
  </form>

  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showEdit = false)} disabled={editSaving}>Cancel</Button>
    <Button
      variant="primary"
      icon={Save}
      loading={editSaving}
      disabled={!editForm.name}
      onclick={() => (document.getElementById('device-edit-form') as HTMLFormElement | null)?.requestSubmit()}
    >
      Save changes
    </Button>
  {/snippet}
</Dialog>

<Dialog bind:open={showBulk} title={`Assign credential to ${selectedIds.size} device${selectedIds.size === 1 ? '' : 's'}`} size="md">
  <div class="space-y-4 p-1">
    {#if bulkError}
      <Alert tone="error">{bulkError}</Alert>
    {/if}
    <p class="text-sm text-surface-700-300">
      Sets <code class="font-mono text-xs">credential_id</code> on every selected device.
      One <code class="font-mono text-xs">PUT /api/device/&lcub;id&rcub;</code> per device; partial
      failures are reported at the end.
    </p>
    <Select label="Credential" help="devices.bulk_credential" bind:value={bulkCredentialId} disabled={bulkSaving}>
      <option value="">(none — unset credential)</option>
      {#each credentialList as c (c.credential_id)}
        <option value={c.credential_id}>{c.name} ({c.type})</option>
      {/each}
    </Select>
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showBulk = false)} disabled={bulkSaving}>Cancel</Button>
    <Button variant="primary" icon={Save} loading={bulkSaving} onclick={runBulkAssign}>
      Apply to {selectedIds.size} device{selectedIds.size === 1 ? '' : 's'}
    </Button>
  {/snippet}
</Dialog>
