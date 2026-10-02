<script lang="ts">
  // Secret manager. Secrets here are referenced via
  // ${secret:secret:<name>:value} inside OpenAPI specs (x-credential-ref
  // hooks in securitySchemes). The AI agent pulls them at execute time
  // through SecretResolver — plaintext never leaves the backend, so the
  // UI only ever shows metadata + a "value set" indicator.

  import { onMount } from 'svelte';
  import { copyText } from '$lib/utils/clipboard';
  import {
    secrets, type Secret,
    errorMessage,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Input, Textarea, Dialog, Alert, Spinner,
    EmptyState, StatusBadge, formatDateTime, truncate, toast, confirm,
  } from '$lib/components/ui';
  import {
    KeyRound, Plus, Pencil, Trash2, RefreshCw, Copy, Info,
  } from 'lucide-svelte';

  let rows = $state<Secret[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  // Dialog state — `editing === null` means we're creating a new secret;
  // otherwise it's an update where `value` stays empty unless the user
  // explicitly rotates.
  let showDialog = $state(false);
  let editing = $state<Secret | null>(null);
  let name = $state('');
  let description = $state('');
  let value = $state('');
  let saving = $state(false);
  let formError = $state<string | null>(null);

  // Mirrors the backend regex so we flag bad input before a round-trip.
  const NAME_RE = /^[a-z0-9][a-z0-9_-]{0,62}[a-z0-9]$/;

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      rows = await secrets.list();
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load secrets");
    } finally {
      loading = false;
    }
  }

  function openCreate() {
    editing = null;
    name = '';
    description = '';
    value = '';
    formError = null;
    showDialog = true;
  }

  function openEdit(s: Secret) {
    editing = s;
    name = s.name;
    description = s.description ?? '';
    value = '';
    formError = null;
    showDialog = true;
  }

  function closeDialog() {
    if (saving) return;
    showDialog = false;
    editing = null;
  }

  function validate(): string | null {
    const n = name.trim();
    if (!NAME_RE.test(n)) return 'Name must be 2–64 chars, lowercase letters/digits/hyphens/underscores.';
    if (!editing && !value) return 'Value is required when creating a secret.';
    return null;
  }

  async function save() {
    if (saving) return;
    formError = validate();
    if (formError) return;

    // Rotating an existing secret (editing + a new non-empty value) swaps the
    // ciphertext the moment we save, so confirm before replacing it. The
    // value-send check below uses the same truthiness test.
    if (editing && value) {
      const ok = await confirm({
        tone: 'danger',
        title: `Rotate secret "${editing.name}"?`,
        message:
          'Rotating replaces the credential immediately; in-flight specs will use the new value.',
        confirmLabel: 'Rotate',
      });
      if (!ok) return;
    }

    saving = true;
    try {
      if (editing) {
        // Only send value if the user filled it in — editing description
        // alone should not touch the ciphertext.
        const body: { description?: string; value?: string } = {
          description: description.trim() || undefined,
        };
        if (value) body.value = value;
        await secrets.update(editing.secret_id, body);
        toast.success('Secret updated');
      } else {
        await secrets.create({
          name: name.trim(),
          description: description.trim() || undefined,
          value,
        });
        toast.success('Secret created');
      }
      showDialog = false;
      await load();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function del(s: Secret) {
    if (!(await confirm({
      title: `Delete secret "${s.name}"?`,
      message: 'Any spec referencing it will break until a replacement is created.',
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await secrets.delete(s.secret_id);
      toast.success('Secret deleted');
      await load();
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  // Copies the ${secret:...} template for a row so the admin can paste it
  // into an OpenAPI spec's x-credential-ref without remembering the syntax.
  async function copyRef(s: Secret) {
    const ref = `\${secret:secret:${s.name}:value}`;
    if (await copyText(ref)) toast.success('Reference copied', { description: ref });
    else toast.info('Copy failed — reference: ' + ref);
  }
</script>

<svelte:head><title>Secrets · Admin · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Secrets"
    description={'Named credentials the AI agent consumes when calling REST operations. Reference them from OpenAPI specs as ${secret:secret:<name>:value}.'}
    breadcrumbs={[{ label: 'Admin', href: '/admin' }, { label: 'Secrets' }]}
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>New secret</Button>
    {/snippet}
  </PageHeader>

  <Alert tone="info">
    <div class="flex items-start gap-2 text-xs">
      <Info size={12} class="mt-0.5 shrink-0" />
      <div>
        Plaintext values are only readable by the backend; the UI shows
        metadata only. Rotate by editing the secret and pasting a new value —
        references stay stable under the same name.
      </div>
    </div>
  </Alert>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if rows.length === 0}
    <Card padding="none">
      <EmptyState
        icon={KeyRound}
        title="No secrets yet"
        description="Add credentials the agent needs (API keys, bearer tokens, basic-auth passwords). Specs reference them by name."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>Create first secret</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <table class="w-full text-sm">
        <thead class="border-b border-surface-200-800">
          <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
            <th class="px-4 py-2">Name</th>
            <th class="px-4 py-2">Description</th>
            <th class="px-4 py-2">Value</th>
            <th class="px-4 py-2">Updated</th>
            <th class="px-4 py-2 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each rows as s (s.secret_id)}
            <tr class="border-b border-surface-200-800/50 hover:bg-surface-100-900/40">
              <td class="px-4 py-2">
                <div class="flex items-center gap-2">
                  <KeyRound size={12} class="text-surface-500" />
                  <span class="font-mono text-surface-900-100">{s.name}</span>
                </div>
              </td>
              <td class="px-4 py-2 text-surface-600-400">{s.description ?? '—'}</td>
              <td class="px-4 py-2">
                {#if s.has_value}
                  <StatusBadge status="success" label="set" showDot={false} />
                {:else}
                  <StatusBadge status="failed" label="empty" showDot={false} />
                {/if}
              </td>
              <td class="px-4 py-2 text-surface-500 text-[11px] tabular-nums whitespace-nowrap">{formatDateTime(s.updated_at)}</td>
              <td class="px-4 py-2">
                <div class="flex items-center justify-end gap-1">
                  <Button size="xs" variant="ghost" icon={Copy} onclick={() => copyRef(s)} title="Copy reference">Ref</Button>
                  <Button size="xs" variant="ghost" icon={Pencil} onclick={() => openEdit(s)}>Edit</Button>
                  <Button size="xs" variant="ghost" icon={Trash2} onclick={() => del(s)} title="Delete">Delete</Button>
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </Card>
  {/if}
</div>

<Dialog bind:open={showDialog} title={editing ? 'Edit secret' : 'New secret'} size="md">
  <div class="space-y-4 p-1">
    {#if formError}
      <Alert tone="error">{formError}</Alert>
    {/if}
    <Input
      label="Name"
      help="secrets.name"
      placeholder="netbox-token"
      bind:value={name}
      disabled={!!editing || saving}
    />
    {#if editing}
      <p class="text-[11px] text-surface-500 -mt-2">
        Names are immutable — references in specs stay stable.
      </p>
    {/if}
    <Textarea
      label="Description"
      help="secrets.description"
      placeholder="What this credential authorizes — shown in /admin/secrets only."
      rows={2}
      bind:value={description}
      disabled={saving}
    />
    <div>
      <Textarea
        label={editing ? 'New value (leave empty to keep current)' : 'Value'}
        help="secrets.value"
        placeholder="Paste the secret value here."
        rows={3}
        bind:value={value}
        disabled={saving}
      />
      <p class="text-[11px] text-surface-500 mt-1">
        {#if editing}
          Leave blank to update only the description. Submitting a value rotates the ciphertext.
        {:else}
          Stored encrypted at rest. The value cannot be retrieved after submission — rotate to replace it.
        {/if}
      </p>
    </div>
  </div>

  {#snippet footer()}
    <Button variant="ghost" onclick={closeDialog} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={save} loading={saving}>{editing ? 'Save changes' : 'Create'}</Button>
  {/snippet}
</Dialog>
