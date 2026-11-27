<script lang="ts">
  import { onMount } from 'svelte';
  import {
    inventorySources,
    integrations,
    secrets,
    errorMessage,
    type InventorySource,
    type InventorySyncResult,
    type Integration,
    type NetBoxSourceConfig,
    type Secret,
  } from '$lib/api/client';
  import { authStore } from '$lib/stores/auth.svelte';
  import {
    PageHeader, Card, Button, IconButton, Badge, EmptyState, Alert, Spinner,
    Input, Select, Checkbox, confirm, toast, formatRelative, formatDateTime,
  } from '$lib/components/ui';
  import { DatabaseZap, Plus, Pencil, Trash2, Save, RefreshCw, FlaskConical } from 'lucide-svelte';

  // NetBox inventory sources and their device sync (ported from Nashira).
  //
  // The token never lives in the source: `config.token_secret_ref` holds a
  // ${secret:...} reference that the backend resolves at sync time. Reads are
  // inventory.read (viewer); create/edit/sync are inventory.manage (operator);
  // attaching a token, or moving base_url while one is set, is admin-only —
  // the API enforces all of it, this page only hides what would be refused.

  const role = $derived(authStore.session?.role ?? 'viewer');
  const isAdmin = $derived(role === 'admin');
  const canManage = $derived(role === 'admin' || role === 'operator');

  type AuthMode = 'none' | 'integration' | 'secret' | 'token' | 'ref';
  const INTEGRATION_REF = /^\$\{secret:integration:([^:}]+):token\}$/;
  const SECRET_REF = /^\$\{secret:secret:([^:}]+):value\}$/;
  const MASKED = '***';

  type Form = {
    name: string;
    description: string;
    baseUrl: string;
    siteFilter: string;
    allowPrivate: boolean;
    enabled: boolean;
    authMode: AuthMode;
    integrationId: string;
    secretName: string;
    newToken: string;
    rawRef: string;
    // The reference as stored when the form opened, to tell "unchanged" apart.
    originalRef: string | null;
  };

  let list = $state<InventorySource[]>([]);
  let loading = $state(true);
  let error = $state('');

  let integrationList = $state<Integration[]>([]);
  let secretList = $state<Secret[]>([]);

  let formOpen = $state(false);
  let editingId = $state<string | null>(null);
  let form = $state<Form>(emptyForm());
  let formError = $state('');
  let saving = $state(false);

  let busy = $state<Record<string, 'dry' | 'sync' | undefined>>({});
  let lastResult = $state<Record<string, InventorySyncResult | undefined>>({});

  onMount(() => {
    load();
    integrations.list(200, 0).then((r) => (integrationList = r.data ?? []), () => {});
  });

  $effect(() => {
    // The secrets API is admin-only; others pick an integration or a reference.
    if (isAdmin) secrets.list().then((s) => (secretList = s ?? []), () => {});
  });

  async function load() {
    loading = true;
    error = '';
    try {
      list = (await inventorySources.list(200, 0)).data ?? [];
    } catch (e) {
      error = errorMessage(e);
    } finally {
      loading = false;
    }
  }

  function emptyForm(): Form {
    return {
      name: '', description: '', baseUrl: '', siteFilter: '', allowPrivate: false, enabled: true,
      authMode: 'none', integrationId: '', secretName: '', newToken: '', rawRef: '', originalRef: null,
    };
  }

  function cfgOf(s: InventorySource): NetBoxSourceConfig {
    return (s.config ?? {}) as unknown as NetBoxSourceConfig;
  }

  function isNetBox(s: InventorySource): boolean {
    return (s.type ?? '').toLowerCase() === 'netbox';
  }

  function authLabel(ref: string | null | undefined): string {
    if (!ref) return 'no token';
    if (ref === MASKED) return 'legacy token';
    const i = INTEGRATION_REF.exec(ref);
    if (i) {
      const hit = integrationList.find((x) => x.id === i[1] || x.name === i[1]);
      return `integration ${hit?.name ?? i[1]}`;
    }
    const s = SECRET_REF.exec(ref);
    if (s) return `secret ${s[1]}`;
    return 'secret reference';
  }

  function openCreate() {
    editingId = null;
    form = emptyForm();
    formError = '';
    formOpen = true;
  }

  function openEdit(s: InventorySource) {
    const c = cfgOf(s);
    const ref = c.token_secret_ref ?? null;
    const f = emptyForm();
    f.name = s.name;
    f.description = s.description ?? '';
    f.baseUrl = c.base_url ?? '';
    f.siteFilter = c.site_filter ?? '';
    f.allowPrivate = c.allow_private_network === true;
    f.enabled = s.enabled;
    f.originalRef = ref;
    if (ref === MASKED) f.authMode = 'token';
    else if (ref && INTEGRATION_REF.test(ref)) {
      f.authMode = 'integration';
      f.integrationId = INTEGRATION_REF.exec(ref)![1];
    } else if (ref && SECRET_REF.test(ref)) {
      f.authMode = 'secret';
      f.secretName = SECRET_REF.exec(ref)![1];
    } else if (ref) {
      f.authMode = 'ref';
      f.rawRef = ref;
    }
    editingId = s.id;
    form = f;
    formError = '';
    formOpen = true;
  }

  // Picking an integration also offers its base URL, the usual case being the
  // NetBox integration that already has a working token.
  function onIntegrationPicked() {
    const hit = integrationList.find((x) => x.id === form.integrationId);
    if (hit) {
      if (!form.baseUrl.trim()) form.baseUrl = hit.base_url;
      if (hit.allow_private_network) form.allowPrivate = true;
    }
  }

  function secretNameFor(name: string): string {
    const slug = name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
    return `netbox-${slug || 'source'}-token`.slice(0, 64).replace(/[_-]+$/, '');
  }

  // Returns the token_secret_ref to send ('' clears it). A pasted token is
  // stored in the Vault first so only the reference travels with the source.
  async function resolveTokenRef(): Promise<string> {
    switch (form.authMode) {
      case 'none':
        return '';
      case 'integration':
        if (!form.integrationId) throw new Error('Choose the integration whose token the sync should use.');
        return `\${secret:integration:${form.integrationId}:token}`;
      case 'secret':
        if (!form.secretName) throw new Error('Choose the secret that holds the API token.');
        return `\${secret:secret:${form.secretName}:value}`;
      case 'ref': {
        const v = form.rawRef.trim();
        if (!v.startsWith('${secret:')) throw new Error('The reference must look like ${secret:secret:<name>:value}.');
        return v;
      }
      case 'token': {
        const token = form.newToken.trim();
        if (!token) {
          if (form.originalRef) return form.originalRef; // keep what is stored
          throw new Error('Paste the API token, or pick another authentication option.');
        }
        const name = secretNameFor(form.name);
        const existing = secretList.find((s) => s.name === name);
        if (existing) await secrets.update(existing.secret_id, { value: token });
        else
          await secrets.create({
            name,
            value: token,
            description: `NetBox API token for inventory source "${form.name.trim()}"`,
          });
        return `\${secret:secret:${name}:value}`;
      }
    }
  }

  async function save() {
    if (!form.name.trim()) return void (formError = 'Name is required.');
    if (!form.baseUrl.trim()) return void (formError = 'Base URL is required.');
    formError = '';
    saving = true;
    try {
      const tokenRef = isAdmin ? await resolveTokenRef() : (form.originalRef ?? '');
      const config: NetBoxSourceConfig = {
        base_url: form.baseUrl.trim(),
        site_filter: form.siteFilter.trim() || null,
        allow_private_network: form.allowPrivate,
      };
      if (tokenRef) config.token_secret_ref = tokenRef;
      const body = {
        name: form.name.trim(),
        type: 'netbox',
        description: form.description.trim() || null,
        config: config as unknown as Record<string, unknown>,
        sync_mode: 'manual',
        enabled: form.enabled,
      };
      if (editingId) await inventorySources.update(editingId, body);
      else await inventorySources.create(body);
      toast.success(editingId ? 'Source saved' : `Source "${body.name}" created`);
      formOpen = false;
      await load();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function remove(s: InventorySource) {
    const ok = await confirm({
      title: `Delete source "${s.name}"?`,
      message: 'The source is removed. Devices it imported stay in the inventory.',
      tone: 'danger',
      confirmLabel: 'Delete',
    });
    if (!ok) return;
    try {
      await inventorySources.delete(s.id);
      if (editingId === s.id) formOpen = false;
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t delete source');
    }
  }

  function summary(r: InventorySyncResult): string {
    return `${r.created} created · ${r.updated} updated · ${r.unchanged} unchanged · ${r.skipped} skipped (${r.total} in NetBox)`;
  }

  async function runSync(s: InventorySource, dryRun: boolean) {
    if (!dryRun) {
      const ok = await confirm({
        title: `Sync "${s.name}"?`,
        message:
          'Devices are imported from NetBox: new ones are created, existing ones (matched by NetBox id, then by name) are updated. Credentials, environments and SSH fingerprints set here are kept. Nothing is deleted.',
        confirmLabel: 'Sync',
      });
      if (!ok) return;
    }
    busy = { ...busy, [s.id]: dryRun ? 'dry' : 'sync' };
    try {
      const r = await inventorySources.sync(s.id, dryRun);
      lastResult = { ...lastResult, [s.id]: r };
      if (dryRun) toast.info(`Dry run: ${summary(r)}`);
      else {
        toast.success(`Sync complete: ${summary(r)}`);
        await load();
      }
    } catch (e) {
      toast.fromError(e, dryRun ? 'Dry run failed' : 'Sync failed');
      if (!dryRun) await load();
    } finally {
      busy = { ...busy, [s.id]: undefined };
    }
  }
</script>

<svelte:head><title>Inventory · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Inventory sources"
    description="Import devices from NetBox. A sync creates the devices it does not know and updates the ones it does — matched by NetBox id, then by name — and never deletes anything."
  >
    {#snippet actions()}
      {#if canManage}
        <Button size="sm" variant="primary" icon={Plus} onclick={openCreate}>New source</Button>
      {/if}
    {/snippet}
  </PageHeader>

  {#if error}<Alert tone="error">{error}</Alert>{/if}

  {#if formOpen}
    <Card>
      <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 mb-3">
        {editingId ? 'Edit source' : 'New NetBox source'}
      </h3>
      {#if formError}<div class="mb-3"><Alert tone="error">{formError}</Alert></div>{/if}
      <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Input label="Name" help="inventory.name" bind:value={form.name} placeholder="netbox-prod" />
        <Input label="Base URL" help="inventory.base_url" bind:value={form.baseUrl} placeholder="https://netbox.example.com" hint="Without /api — the sync calls /api/dcim/devices/." />
        <Input label="Site filter (optional)" help="inventory.site_filter" bind:value={form.siteFilter} placeholder="hq" hint="NetBox site slug; empty imports every site." />
        <Input label="Description (optional)" help="inventory.description" bind:value={form.description} />

        <div class="md:col-span-2 space-y-3">
          {#if isAdmin}
            <Select label="Authentication" help="inventory.auth" bind:value={form.authMode}>
              <option value="none">No token (NetBox allows anonymous reads)</option>
              <option value="integration">Reuse the token of an integration</option>
              <option value="secret">Existing Vault secret</option>
              <option value="token">Paste an API token (stored in the Vault)</option>
              <option value="ref">Custom secret reference (advanced)</option>
            </Select>

            {#if form.authMode === 'integration'}
              <Select label="Integration" help="inventory.integration" bind:value={form.integrationId} onchange={onIntegrationPicked}
                hint="The sync sends that integration's token (auth_config.token) — bearer or token auth.">
                <option value="" disabled>Choose an integration…</option>
                {#each integrationList as i (i.id)}
                  <option value={i.id}>{i.name} — {i.base_url}</option>
                {/each}
              </Select>
            {:else if form.authMode === 'secret'}
              <Select label="Secret" help="inventory.secret" bind:value={form.secretName} hint="Managed in Operate → Vault. The value never leaves the server.">
                <option value="" disabled>Choose a secret…</option>
                {#each secretList as s (s.secret_id)}
                  <option value={s.name}>{s.name}</option>
                {/each}
              </Select>
            {:else if form.authMode === 'token'}
              <Input
                label="API token"
                help="inventory.token"
                type="password"
                revealable
                autocomplete="off"
                bind:value={form.newToken}
                hint={form.originalRef
                  ? 'A token is already stored. Leave blank to keep it, or paste a new one to replace it.'
                  : `Stored encrypted in the Vault as "${secretNameFor(form.name)}"; the source only keeps the reference. v1 and v2 (nbt_…) tokens both work.`}
              />
            {:else if form.authMode === 'ref'}
              <Input label="Secret reference" help="inventory.secret_ref" bind:value={form.rawRef} placeholder={'${secret:secret:<name>:value}'} />
            {/if}
          {:else}
            <p class="text-sm text-surface-600-400">
              Authentication: <span class="font-medium">{authLabel(form.originalRef)}</span>.
              Only an admin can set the token, or change the base URL while one is set.
            </p>
          {/if}
        </div>

        <div class="md:col-span-2 flex flex-wrap gap-6">
          <Checkbox bind:checked={form.allowPrivate} help="inventory.allow_private" label="Allow private-network base URL (on-prem NetBox on 10/8, 172.16/12, 192.168/16)" />
          <Checkbox bind:checked={form.enabled} help="inventory.enabled" label="Enabled" />
        </div>
      </div>
      <div class="mt-4 flex justify-end gap-2">
        <Button variant="ghost" onclick={() => (formOpen = false)}>Cancel</Button>
        <Button variant="primary" icon={Save} loading={saving} onclick={save}>{editingId ? 'Save' : 'Create'}</Button>
      </div>
    </Card>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading sources…" /></div>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={DatabaseZap}
        title="No inventory sources yet"
        description="Point a source at NetBox and sync it to fill Devices with what NetBox knows: name, primary IP, platform, vendor, site, role and status, plus tags and custom fields."
      >
        {#snippet actions()}
          {#if canManage}
            <Button variant="primary" icon={Plus} onclick={openCreate}>Add NetBox source</Button>
          {/if}
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <div class="space-y-2">
      {#each list as s (s.id)}
        {@const c = cfgOf(s)}
        {@const failed = (s.last_sync_status ?? '').startsWith('failed')}
        {@const r = lastResult[s.id]}
        <Card>
          <div class="flex items-start justify-between gap-3 flex-wrap">
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2 flex-wrap">
                <span class="font-semibold">{s.name}</span>
                <Badge tone="neutral">{s.type}</Badge>
                {#if !s.enabled}<Badge tone="warning">disabled</Badge>{/if}
                {#if isNetBox(s)}<Badge tone="neutral">{authLabel(c.token_secret_ref)}</Badge>{/if}
                {#if c.site_filter}<Badge tone="neutral">site {c.site_filter}</Badge>{/if}
              </div>
              <code class="mt-1 block text-xs text-surface-600-400 truncate">{c.base_url ?? '—'}</code>
              {#if s.description}<p class="mt-1 text-sm text-surface-600-400">{s.description}</p>{/if}
              <p class="mt-2 text-xs {failed ? 'text-error-500' : 'text-surface-500'}">
                {#if s.last_sync_at}
                  Last sync <span title={formatDateTime(s.last_sync_at)}>{formatRelative(s.last_sync_at)}</span>
                {:else}
                  Never synced
                {/if}
                {#if s.last_sync_status} · {s.last_sync_status}{/if}
              </p>
              {#if r}
                <p class="mt-1 text-xs text-surface-600-400">
                  {r.dry_run ? 'Dry run' : 'Last run here'}: {summary(r)}
                </p>
              {/if}
            </div>
            {#if canManage}
              <div class="flex items-center gap-2">
                {#if isNetBox(s)}
                  <Button size="sm" variant="ghost" icon={FlaskConical} disabled={!!busy[s.id] || !s.enabled}
                    loading={busy[s.id] === 'dry'} onclick={() => runSync(s, true)}>Dry run</Button>
                  <Button size="sm" variant="secondary" icon={RefreshCw} disabled={!!busy[s.id] || !s.enabled}
                    loading={busy[s.id] === 'sync'} onclick={() => runSync(s, false)}>Sync</Button>
                {/if}
                <IconButton icon={Pencil} label="Edit" onclick={() => openEdit(s)} />
                <IconButton icon={Trash2} label="Delete" variant="danger" onclick={() => remove(s)} />
              </div>
            {/if}
          </div>
        </Card>
      {/each}
    </div>
  {/if}
</div>
