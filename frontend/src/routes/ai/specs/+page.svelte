<script lang="ts">
  // API spec catalog — OpenAPI 3.x YAML documents consumed by the agent's
  // dynamic tools (list_apis / discover_operations / operation_detail /
  // execute_operation). `api` is the filename-stem identifier the agent
  // sees; operation_count comes from the parser.

  import { onMount } from 'svelte';
  import {
    apiSpecs,
    integrations,
    type ApiSpecSummary,
    type Integration,
    errorMessage,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Input, Select, Textarea, Dialog, Alert, Spinner,
    EmptyState, StatusBadge, formatDateTime, toast, confirm, FieldHint,
  } from '$lib/components/ui';
  import {
    FileCode2, Plus, Pencil, Trash2, RefreshCw, HardDriveDownload,
  } from 'lucide-svelte';

  let rows = $state<ApiSpecSummary[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  // Integration lookup + filter — same pattern as /ai/skills. Populated
  // once on mount; missing entries show a truncated UUID.
  let integrationsById = $state<Map<string, string>>(new Map());
  let integrationsList = $state<Integration[]>([]);
  let integrationFilter = $state<string>('all');

  let showDialog = $state(false);
  let editing = $state<ApiSpecSummary | null>(null);
  let api = $state('');
  let content = $state('');
  let isActive = $state(true);
  // '' = global; otherwise an integration id. Tells the agent which base URL
  // + credentials back the operations this spec describes.
  let integrationId = $state<string>('');
  let saving = $state(false);
  let formError = $state<string | null>(null);

  onMount(async () => {
    try {
      const res = await integrations.list(200, 0);
      integrationsList = res.data;
      integrationsById = new Map(res.data.map((i) => [i.id, i.name]));
    } catch {
      integrationsList = [];
      integrationsById = new Map();
    }
    await load();
  });

  async function load() {
    loading = true;
    loadError = null;
    try {
      const res = await apiSpecs.list(100, 0);
      rows = res.data;
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load specs");
    } finally {
      loading = false;
    }
  }

  const filteredRows = $derived.by(() => {
    if (integrationFilter === 'all') return rows;
    if (integrationFilter === 'global') return rows.filter((r) => !r.integration_id);
    return rows.filter((r) => r.integration_id === integrationFilter);
  });

  function openCreate() {
    editing = null;
    api = '';
    content = '';
    isActive = true;
    integrationId = '';
    formError = null;
    showDialog = true;
  }

  async function openEdit(row: ApiSpecSummary) {
    editing = row;
    api = row.api;
    content = row.content ?? '';
    isActive = row.is_active;
    integrationId = row.integration_id ?? '';
    formError = null;
    showDialog = true;

    try {
      const full = await apiSpecs.get(row.ai_api_spec_id);
      content = full.content;
    } catch {
      // keep list content
    }
  }

  function closeDialog() {
    if (saving) return;
    showDialog = false;
    editing = null;
  }

  function validate(): string | null {
    const trimmed = api.trim().toLowerCase();
    if (!/^[a-z0-9_-]+$/.test(trimmed)) return 'API identifier must be lowercase letters/digits/hyphens/underscores.';
    if (!content.trim()) return 'Content cannot be empty.';
    // Dumb sanity check on YAML — the backend parser is lenient, but we
    // want to catch "this is JSON" level mistakes early.
    if (!/\bpaths\s*:/.test(content)) return 'YAML must declare a top-level `paths:` map (OpenAPI 3.x).';
    return null;
  }

  async function save() {
    if (saving) return;
    formError = validate();
    if (formError) return;

    saving = true;
    try {
      if (editing) {
        await apiSpecs.update(editing.ai_api_spec_id, {
          api: api.trim().toLowerCase(),
          content,
          is_active: isActive,
          integration_id: integrationId || null,
        });
        toast.success('Spec updated');
      } else {
        await apiSpecs.create({
          api: api.trim().toLowerCase(),
          content,
          integration_id: integrationId || null,
        });
        toast.success('Spec created');
      }
      showDialog = false;
      await load();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function del(row: ApiSpecSummary) {
    if (!(await confirm({
      title: `Delete spec "${row.api}"?`,
      message: 'Operations referenced by workflows will stop resolving until replaced.',
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await apiSpecs.delete(row.ai_api_spec_id);
      toast.success('Spec deleted');
      await load();
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  async function onFile(ev: Event) {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    const text = await file.text();
    content = text;
    // Pre-fill api from filename if not set yet.
    if (!api) {
      const stem = file.name.replace(/\.(ya?ml)$/i, '').toLowerCase();
      api = stem;
    }
    input.value = '';
  }

  function kb(n: number): string {
    return n < 1024 ? `${n} B` : `${(n / 1024).toFixed(1)} KB`;
  }

  // Re-import /Specs/*.yaml from disk. Overwrites existing rows whose
  // `api` matches the filename stem; custom specs stay intact. Reload
  // of the in-memory IApiSpecIndex happens backend-side so the agent
  // sees the new operations on the very next message.
  let reseeding = $state(false);
  async function reseedFromDisk() {
    if (reseeding) return;
    reseeding = true;
    try {
      const res = await apiSpecs.reseed();
      const parts = [`${res.imported} added`, `${res.updated} updated`];
      toast.success('Reseed complete', { description: parts.join(' · ') });
      await load();
    } catch (e) {
      toast.fromError(e, 'Reseed failed');
    } finally {
      reseeding = false;
    }
  }
</script>

<svelte:head><title>API Specs · AI · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="API specs"
    description="OpenAPI YAML documents the agent uses to discover and execute REST operations. `api` is the identifier the agent sees."
    breadcrumbs={[{ label: 'AI', href: '/ai' }, { label: 'Specs' }]}
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button
        variant="ghost"
        icon={HardDriveDownload}
        loading={reseeding}
        onclick={reseedFromDisk}
        title="Re-import /Specs/*.yaml from the backend's disk, overwriting existing rows by api."
      >Reseed from disk</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>New spec</Button>
    {/snippet}
  </PageHeader>

  {#if !loading && !loadError && rows.length > 0}
    <div class="flex items-center gap-3">
      <span class="text-xs text-surface-500 whitespace-nowrap">Filter by integration</span>
      <div class="max-w-xs">
        <Select bind:value={integrationFilter}>
          <option value="all">All specs</option>
          <option value="global">Global only</option>
          {#each integrationsList as i (i.id)}
            <option value={i.id}>{i.name}</option>
          {/each}
        </Select>
      </div>
      <span class="text-xs text-surface-500 tabular-nums">
        {filteredRows.length} of {rows.length}
      </span>
    </div>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if rows.length === 0}
    <Card padding="none">
      <EmptyState
        icon={FileCode2}
        title="No specs yet"
        description={'Paste or upload an OpenAPI 3.x YAML. Reference secrets from securitySchemes with x-credential-ref: ${secret:secret:<name>:value}.'}
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>New spec</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <table class="w-full text-sm">
        <thead class="border-b border-surface-200-800">
          <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
            <th class="px-4 py-2">API</th>
            <th class="px-4 py-2">Integration</th>
            <th class="px-4 py-2">Operations</th>
            <th class="px-4 py-2">Size</th>
            <th class="px-4 py-2">State</th>
            <th class="px-4 py-2">Updated</th>
            <th class="px-4 py-2 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each filteredRows as r (r.ai_api_spec_id)}
            <tr class="border-b border-surface-200-800/50 hover:bg-surface-100-900/40">
              <td class="px-4 py-2">
                <div class="flex items-center gap-2">
                  <FileCode2 size={12} class="text-surface-500" />
                  <span class="font-mono text-surface-900-100">{r.api}</span>
                </div>
              </td>
              <td class="px-4 py-2 text-xs text-surface-500">
                {#if r.integration_id}
                  <span class="text-surface-700-300">{integrationsById.get(r.integration_id) ?? r.integration_id.slice(0, 8) + '…'}</span>
                {:else}
                  <span class="opacity-50">—</span>
                {/if}
              </td>
              <td class="px-4 py-2 text-surface-700-300 tabular-nums">{r.operation_count}</td>
              <td class="px-4 py-2 text-surface-500 tabular-nums">{kb(r.size_bytes)}</td>
              <td class="px-4 py-2">
                {#if r.is_active}
                  <StatusBadge status="success" label="active" showDot={false} />
                {:else}
                  <StatusBadge status="pending" label="inactive" showDot={false} />
                {/if}
              </td>
              <td class="px-4 py-2 text-surface-500 text-[11px] tabular-nums whitespace-nowrap">{formatDateTime(r.updated_at)}</td>
              <td class="px-4 py-2">
                <div class="flex items-center justify-end gap-1">
                  <Button size="xs" variant="ghost" icon={Pencil} onclick={() => openEdit(r)}>Edit</Button>
                  <Button size="xs" variant="ghost" icon={Trash2} onclick={() => del(r)}>Delete</Button>
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </Card>
  {/if}
</div>

<Dialog bind:open={showDialog} title={editing ? 'Edit spec' : 'New spec'} size="xl">
  <div class="space-y-4 p-1">
    {#if formError}
      <Alert tone="error">{formError}</Alert>
    {/if}

    <div class="grid grid-cols-1 md:grid-cols-3 gap-3 items-end">
      <div class="md:col-span-2">
        <Input label="API identifier"
      help="specs.api" bind:value={api} disabled={saving || !!editing} placeholder="netbox" />
      </div>
      <!-- `for` is load-bearing: <button> is a labelable element, so without
           it FieldHint's info button (which precedes the input) becomes the
           labeled control and the picker never opens. -->
      <label for="spec-upload-file" class="inline-flex items-center justify-center gap-2 rounded-md border border-dashed border-surface-300-700 px-3 py-2 text-xs cursor-pointer hover:bg-surface-200-800/40">
        Upload .yaml <FieldHint id="specs.upload_file" />
        <input id="spec-upload-file" type="file" accept=".yaml,.yml" onchange={onFile} disabled={saving} class="sr-only" />
      </label>
    </div>

    <Select label="Integration (optional)"
      help="specs.integration" bind:value={integrationId}>
      <option value="">Global — applies to all integrations</option>
      {#each integrationsList as i (i.id)}
        <option value={i.id}>{i.name}</option>
      {/each}
    </Select>

    <Textarea
      label="Content (OpenAPI 3.x YAML)"
      help="specs.content"
      bind:value={content}
      disabled={saving}
      rows={22}
      placeholder={`openapi: 3.0.0\ninfo:\n  title: netbox\n  version: "1.0"\nservers:\n  - url: https://netbox.example.com/api\npaths:\n  /dcim/devices/:\n    get:\n      operationId: netbox:list_devices\n      summary: List devices\n      tags: [dcim]\ncomponents:\n  securitySchemes:\n    token:\n      type: http\n      scheme: bearer\n      x-credential-ref: \${secret:secret:netbox-token:value}\nsecurity:\n  - token: []\n`}
    />

    {#if editing}
      <label class="inline-flex items-center gap-2 text-sm">
        <input type="checkbox" bind:checked={isActive} disabled={saving} class="accent-primary-500" />
        Active <FieldHint id="specs.active" />
      </label>
    {/if}
  </div>

  {#snippet footer()}
    <Button variant="ghost" onclick={closeDialog} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={save} loading={saving}>{editing ? 'Save changes' : 'Create'}</Button>
  {/snippet}
</Dialog>
