<script lang="ts">
  // Prompt skill catalog — the markdown fragments concatenated into every
  // chat's system prompt (sorted by SortOrder then Name). Edits invalidate
  // SkillPromptLoader's cache on save so the next message picks them up.
  // Supports {{CurrentDate}} and {{ToolList}} placeholders.

  import { onMount } from 'svelte';
  import {
    promptSkills,
    integrations,
    type PromptSkillSummary,
    type Integration,
    errorMessage,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Input, Select, Textarea, Dialog, Alert, Spinner,
    EmptyState, StatusBadge, formatDateTime, toast, confirm, FieldHint,
  } from '$lib/components/ui';
  import {
    BookOpen, Plus, Pencil, Trash2, RefreshCw, FileCode, HardDriveDownload, Upload,
  } from 'lucide-svelte';

  let rows = $state<PromptSkillSummary[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  // Integration name cache used by the "Integration" column + filter. Built
  // once on mount from the integrations list. Missing entries fall
  // back to showing a raw UUID so the user notices stale references.
  let integrationsById = $state<Map<string, string>>(new Map());
  let integrationsList = $state<Integration[]>([]);
  // 'all' | 'global' | <integration_id>
  let integrationFilter = $state<string>('all');

  let showDialog = $state(false);
  let editing = $state<PromptSkillSummary | null>(null);
  let name = $state('');
  let content = $state('');
  let sortOrder = $state(100);
  let isActive = $state(true);
  // '' = global; otherwise an integration id. Pairs the skill with that
  // integration's base URL + credentials.
  let integrationId = $state<string>('');

  // Hidden native input + custom trigger — browsers localize
  // "Examinar" / "Ningún archivo seleccionado" from the OS and that
  // bleeds through in non-English environments. Driving the input
  // via .click() from a Button keeps the UI fully in our language.
  let fileInput = $state<HTMLInputElement | null>(null);
  let saving = $state(false);
  let formError = $state<string | null>(null);

  onMount(async () => {
    // Integrations load is best-effort — if it fails the page still works,
    // the Integration column just shows raw ids.
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
      const res = await promptSkills.list(100, 0);
      rows = res.data;
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load skills");
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
    name = '';
    content = '';
    sortOrder = 100;
    isActive = true;
    integrationId = '';
    formError = null;
    showDialog = true;
  }

  async function openEdit(row: PromptSkillSummary) {
    editing = row;
    name = row.name;
    content = row.content ?? '';
    sortOrder = row.sort_order;
    isActive = row.is_active;
    integrationId = row.integration_id ?? '';
    formError = null;
    showDialog = true;

    // The list endpoint already returns content, but double-check — some
    // variants truncate. A fresh GET guarantees we edit the full body.
    try {
      const full = await promptSkills.get(row.ai_prompt_skill_id);
      content = full.content;
    } catch {
      // keep whatever we had from the list call
    }
  }

  function closeDialog() {
    if (saving) return;
    showDialog = false;
    editing = null;
  }

  // Load the contents of a picked .md file into the Content textarea.
  // Mirrors the pattern in /ai/specs/+page.svelte so both catalog pages
  // share the same upload affordance.
  async function onFile(ev: Event) {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    content = await file.text();
    if (!name) {
      // The filename becomes the skill name verbatim — keeps the .md
      // extension so the validation regex passes.
      name = file.name;
    }
    input.value = '';
  }

  function validate(): string | null {
    if (!name.trim()) return 'Name is required.';
    if (!/^[a-z0-9_.-]+$/i.test(name.trim())) return 'Name must be filename-safe (letters, digits, _, -, .).';
    if (!content.trim()) return 'Content cannot be empty.';
    return null;
  }

  async function save() {
    if (saving) return;
    formError = validate();
    if (formError) return;

    saving = true;
    try {
      if (editing) {
        await promptSkills.update(editing.ai_prompt_skill_id, {
          name: name.trim(),
          content,
          sort_order: sortOrder,
          is_active: isActive,
          integration_id: integrationId || null,
        });
        toast.success('Skill updated');
      } else {
        await promptSkills.create({
          name: name.trim(),
          content,
          sort_order: sortOrder,
          integration_id: integrationId || null,
        });
        toast.success('Skill created');
      }
      showDialog = false;
      await load();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function del(row: PromptSkillSummary) {
    if (!(await confirm({
      title: `Delete skill "${row.name}"?`,
      message: "The agent's system prompt will lose this section on the next message.",
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await promptSkills.delete(row.ai_prompt_skill_id);
      toast.success('Skill deleted');
      await load();
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  function kb(n: number): string {
    return n < 1024 ? `${n} B` : `${(n / 1024).toFixed(1)} KB`;
  }

  // Forces the backend to re-import /Skills/*.md from disk, overwriting
  // existing rows whose filename matches. Custom skills stay untouched.
  // Useful after a backend deploy ships updated bundled skills.
  let reseeding = $state(false);
  async function reseedFromDisk() {
    if (reseeding) return;
    reseeding = true;
    try {
      const res = await promptSkills.reseed();
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

<svelte:head><title>Prompt Skills · AI · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Prompt skills"
    description={"Markdown fragments concatenated into every chat's system prompt. Sorted by sort-order (base.md = 0) then name. Supports {{CurrentDate}} and {{ToolList}}."}
    breadcrumbs={[{ label: 'AI', href: '/ai' }, { label: 'Skills' }]}
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button
        variant="ghost"
        icon={HardDriveDownload}
        loading={reseeding}
        onclick={reseedFromDisk}
        title="Re-import /Skills/*.md from the backend's disk, overwriting existing rows by name."
      >Reseed from disk</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>New skill</Button>
    {/snippet}
  </PageHeader>

  {#if !loading && !loadError && rows.length > 0}
    <div class="flex items-center gap-3">
      <span class="text-xs text-surface-500 whitespace-nowrap">Filter by integration</span>
      <div class="max-w-xs">
        <Select bind:value={integrationFilter}>
          <option value="all">All skills</option>
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
        icon={BookOpen}
        title="No skills yet"
        description="On first boot the catalog imports /Skills/*.md. If this is empty, restart the backend or add skills manually."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>New skill</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <table class="w-full text-sm">
        <thead class="border-b border-surface-200-800">
          <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
            <th class="px-4 py-2 w-16">Order</th>
            <th class="px-4 py-2">Name</th>
            <th class="px-4 py-2">Integration</th>
            <th class="px-4 py-2">Size</th>
            <th class="px-4 py-2">State</th>
            <th class="px-4 py-2">Updated</th>
            <th class="px-4 py-2 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each filteredRows as r (r.ai_prompt_skill_id)}
            <tr class="border-b border-surface-200-800/50 hover:bg-surface-100-900/40">
              <td class="px-4 py-2 text-surface-500 tabular-nums">{r.sort_order}</td>
              <td class="px-4 py-2">
                <div class="flex items-center gap-2">
                  <FileCode size={12} class="text-surface-500" />
                  <span class="font-mono text-surface-900-100">{r.name}</span>
                </div>
              </td>
              <td class="px-4 py-2 text-xs text-surface-500">
                {#if r.integration_id}
                  <span class="text-surface-700-300">{integrationsById.get(r.integration_id) ?? r.integration_id.slice(0, 8) + '…'}</span>
                {:else}
                  <span class="opacity-50">—</span>
                {/if}
              </td>
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

<Dialog bind:open={showDialog} title={editing ? 'Edit skill' : 'New skill'} size="xl">
  <div class="space-y-4 p-1">
    {#if formError}
      <Alert tone="error">{formError}</Alert>
    {/if}
    <div class="grid grid-cols-1 md:grid-cols-3 gap-3">
      <div class="md:col-span-2">
        <Input label="Name" help="skills.name" bind:value={name} disabled={saving} placeholder="base.md" />
      </div>
      <Input label="Sort order" help="skills.sort_order" type="number" min={0} bind:value={sortOrder} disabled={saving} />
    </div>
    <Select label="Integration (optional)" help="skills.integration" bind:value={integrationId}>
      <option value="">Global — applies to all integrations</option>
      {#each integrationsList as i (i.id)}
        <option value={i.id}>{i.name}</option>
      {/each}
    </Select>
    <div class="flex items-center gap-3">
      <FieldHint id="skills.upload_file" />
      <input
        bind:this={fileInput}
        type="file"
        accept=".md,text/markdown"
        onchange={onFile}
        disabled={saving}
        class="sr-only"
        tabindex="-1"
        aria-hidden="true"
      />
      <Button
        variant="secondary"
        icon={Upload}
        disabled={saving}
        onclick={() => fileInput?.click()}
      >
        Load from .md file
      </Button>
      <span class="text-xs text-surface-500">Optional — overwrites the content below.</span>
    </div>
    <Textarea
      label="Content (markdown)"
      help="skills.content"
      bind:value={content}
      disabled={saving}
      rows={16}
      placeholder={'You are the FlowWeaver assistant...\n\nCurrent date: {{CurrentDate}}\n\nTools available:\n{{ToolList}}'}
    />
    {#if editing}
      <label class="inline-flex items-center gap-2 text-sm">
        <input type="checkbox" bind:checked={isActive} disabled={saving} class="accent-primary-500" />
        Active <FieldHint id="skills.active" />
      </label>
    {/if}
  </div>

  {#snippet footer()}
    <Button variant="ghost" onclick={closeDialog} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={save} loading={saving}>{editing ? 'Save changes' : 'Create'}</Button>
  {/snippet}
</Dialog>
