<script lang="ts">
  import { onMount } from 'svelte';
  import {
    promptSkills,
    apiSpecs,
    errorMessage,
    type PromptSkillSummary,
    type ApiSpecSummary,
  } from '$lib/api/client';
  import {
    PageHeader,
    Card,
    DataTable,
    Badge,
    Button,
    IconButton,
    Dialog,
    Input,
    Textarea,
    Alert,
    Spinner,
    ErrorState,
    EmptyState,
    toast,
    confirm,
    formatDate,
  } from '$lib/components/ui';
  import { Upload, Save, Trash2, RefreshCcw, FileCode, Sparkles } from 'lucide-svelte';

  // Polymorphic between prompt skills (*.md) and api specs (*.yaml). The
  // two share ~90% of the flow so the component is parameterised by
  // `kind` and picks the right API client + constants at runtime.
  type Kind = 'skill' | 'spec';

  let { kind }: { kind: Kind } = $props();

  // Either-or arrays; the UI only ever uses one. Typed separately to
  // keep the payload shape literal in the DataTable rows.
  let skillList = $state<PromptSkillSummary[]>([]);
  let specList = $state<ApiSpecSummary[]>([]);

  let loading = $state(true);
  let loadError = $state<unknown>(null);

  // Selected row for the editor pane. Kept as the full object so we can
  // diff `is_active` on toggle and avoid an extra GET.
  let selected = $state<PromptSkillSummary | ApiSpecSummary | null>(null);
  let editorContent = $state('');
  let editorBusy = $state(false);
  let editorError = $state<string | null>(null);

  // Upload dialog state. For skills the user sets `name` (must end in .md);
  // for specs `api` (no extension, matches the tool-facing identifier).
  let showUpload = $state(false);
  let uploadName = $state('');
  let uploadContent = $state('');
  let uploadSortOrder = $state<number | null>(null);
  let uploadBusy = $state(false);
  let uploadError = $state<string | null>(null);
  // Hidden native input + custom trigger — see the upload dialog markup.
  let fileInput = $state<HTMLInputElement | null>(null);
  let pickedFileName = $state('');

  const config = $derived.by(() => {
    if (kind === 'skill') {
      return {
        title: 'Skills',
        description: 'Prompt catalog concatenated into the agent system prompt. Admin-only.',
        icon: Sparkles,
        accept: '.md,text/markdown',
        nameLabel: 'Name (must end in .md)',
        namePlaceholder: 'base.md',
        namePattern: /^[a-zA-Z0-9_\-]+\.md$/,
        noun: 'skill',
        showSortOrder: true,
        showOperationCount: false,
      };
    }
    return {
      title: 'Specs',
      description: 'OpenAPI 3.x YAMLs indexed for the agent’s discover/execute tools. Admin-only.',
      icon: FileCode,
      accept: '.yaml,.yml,text/yaml,application/yaml,application/x-yaml',
      nameLabel: 'API identifier (no extension)',
      namePlaceholder: 'netbox',
      namePattern: /^[a-zA-Z0-9_\-]+$/,
      noun: 'spec',
      showSortOrder: false,
      showOperationCount: true,
    };
  });

  // Active list, regardless of kind, so the template can render one loop.
  const rows = $derived<(PromptSkillSummary | ApiSpecSummary)[]>(
    kind === 'skill' ? skillList : specList,
  );

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      if (kind === 'skill') {
        const res = await promptSkills.list(200, 0);
        skillList = res.data;
      } else {
        const res = await apiSpecs.list(200, 0);
        specList = res.data;
      }
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }

  function displayName(row: PromptSkillSummary | ApiSpecSummary): string {
    return 'name' in row ? row.name : row.api;
  }

  function rowId(row: PromptSkillSummary | ApiSpecSummary): string {
    return 'ai_prompt_skill_id' in row ? row.ai_prompt_skill_id : row.ai_api_spec_id;
  }

  async function openRow(row: PromptSkillSummary | ApiSpecSummary) {
    editorError = null;
    // The list endpoint leaves content empty; fetch the body on demand.
    try {
      const id = rowId(row);
      const full = kind === 'skill'
        ? await promptSkills.get(id)
        : await apiSpecs.get(id);
      selected = full;
      editorContent = full.content;
    } catch (e) {
      toast.fromError(e, 'Failed to load contents');
    }
  }

  async function saveEditor() {
    if (!selected || editorBusy) return;
    editorBusy = true;
    editorError = null;
    try {
      const id = rowId(selected);
      const updated = kind === 'skill'
        ? await promptSkills.update(id, { content: editorContent })
        : await apiSpecs.update(id, { content: editorContent });
      selected = updated;
      editorContent = updated.content;
      // Refresh the list so size_bytes / operation_count stay accurate.
      await load();
      toast.success(`Saved ${displayName(updated)}`);
    } catch (e) {
      editorError = errorMessage(e);
    } finally {
      editorBusy = false;
    }
  }

  async function removeRow(row: PromptSkillSummary | ApiSpecSummary) {
    const ok = await confirm({
      title: `Delete ${displayName(row)}?`,
      message: 'This is a soft delete — the row stays in DB but stops being served to the agent.',
      confirmLabel: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      const id = rowId(row);
      if (kind === 'skill') await promptSkills.delete(id);
      else await apiSpecs.delete(id);
      if (selected && rowId(selected) === id) {
        selected = null;
        editorContent = '';
      }
      await load();
      toast.success(`Deleted ${displayName(row)}`);
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  function openUpload() {
    uploadName = '';
    uploadContent = '';
    uploadSortOrder = null;
    uploadError = null;
    pickedFileName = '';
    showUpload = true;
  }

  async function handleFilePicked(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    pickedFileName = file.name;
    uploadContent = await file.text();
    if (!uploadName) {
      // Skills keep the full filename (base.md); specs strip the extension.
      uploadName = kind === 'skill'
        ? file.name
        : file.name.replace(/\.(ya?ml)$/i, '').toLowerCase();
    }
  }

  async function submitUpload(event: SubmitEvent) {
    event.preventDefault();
    if (uploadBusy) return;
    uploadError = null;

    const trimmed = uploadName.trim();
    if (!trimmed || !uploadContent) {
      uploadError = 'Name and file content are required';
      return;
    }
    if (!config.namePattern.test(trimmed)) {
      uploadError = `Name must match ${config.namePattern}`;
      return;
    }

    uploadBusy = true;
    try {
      if (kind === 'skill') {
        const created = await promptSkills.create({
          name: trimmed,
          content: uploadContent,
          sort_order: uploadSortOrder ?? undefined,
        });
        selected = created;
        editorContent = created.content;
      } else {
        const created = await apiSpecs.create({
          api: trimmed,
          content: uploadContent,
        });
        selected = created;
        editorContent = created.content;
      }
      showUpload = false;
      await load();
      toast.success(`Uploaded ${trimmed}`);
    } catch (e) {
      uploadError = errorMessage(e);
    } finally {
      uploadBusy = false;
    }
  }
</script>

<svelte:head><title>{config.title} · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader title={config.title} description={config.description}>
    {#snippet actions()}
      <div class="flex items-center gap-2">
        <Button variant="ghost" icon={RefreshCcw} onclick={load} disabled={loading}>Refresh</Button>
        <Button variant="primary" icon={Upload} onclick={openUpload}>Upload</Button>
      </div>
    {/snippet}
  </PageHeader>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading…" /></div>
  {:else if loadError}
    <Card padding="none"><ErrorState error={loadError} onRetry={load} /></Card>
  {:else if rows.length === 0}
    <Card padding="none">
      <EmptyState
        icon={config.icon}
        title={`No ${config.noun}s yet`}
        description={`Upload a ${config.noun} to seed the catalog.`}
      />
    </Card>
  {:else}
    <div class="grid grid-cols-1 lg:grid-cols-5 gap-5">
      <!-- List pane ─────────────────────────────────────────────── -->
      <div class="lg:col-span-2">
        <DataTable>
          <thead>
            <tr>
              <th>Name</th>
              <th class="!text-right">Size</th>
              {#if config.showOperationCount}
                <th class="!text-right">Ops</th>
              {/if}
              <th class="!text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            {#each rows as row (rowId(row))}
              {@const isSelected = selected && rowId(selected) === rowId(row)}
              <tr
                class={isSelected ? 'bg-primary-500/10' : 'cursor-pointer'}
                onclick={() => openRow(row)}
              >
                <td class="font-medium text-surface-900-100">
                  <div class="flex items-center gap-2">
                    <span>{displayName(row)}</span>
                    {#if kind === 'skill' && 'sort_order' in row && row.sort_order === 0}
                      <Badge tone="primary" size="xs">base</Badge>
                    {/if}
                  </div>
                  <div class="text-xs text-surface-500 mt-0.5 tabular-nums">
                    Updated {formatDate(row.updated_at)}
                  </div>
                </td>
                <td class="text-right font-mono tabular-nums text-surface-600-400 text-xs">
                  {row.size_bytes}
                </td>
                {#if config.showOperationCount && 'operation_count' in row}
                  <td class="text-right font-mono tabular-nums text-surface-600-400 text-xs">
                    {row.operation_count}
                  </td>
                {/if}
                <td class="text-right">
                  <IconButton
                    icon={Trash2}
                    label={`Delete ${displayName(row)}`}
                    size="sm"
                    variant="danger"
                    onclick={(e: MouseEvent) => { e.stopPropagation(); removeRow(row); }}
                  />
                </td>
              </tr>
            {/each}
          </tbody>
        </DataTable>
        <p class="text-xs text-surface-500 mt-2">
          {rows.length} {config.noun}{rows.length === 1 ? '' : 's'}
        </p>
      </div>

      <!-- Editor pane ─────────────────────────────────────────────── -->
      <div class="lg:col-span-3">
        {#if !selected}
          <Card padding="none">
            <EmptyState
              icon={config.icon}
              title="Select an item"
              description="Pick one from the list to view or edit its contents."
            />
          </Card>
        {:else}
          <Card>
            <div class="p-4 space-y-3">
              <div class="flex items-center justify-between gap-3">
                <div class="min-w-0">
                  <div class="text-sm font-semibold text-surface-900-100 truncate">
                    {displayName(selected)}
                  </div>
                  <div class="text-[11px] text-surface-500 mt-0.5">
                    {selected.size_bytes} bytes · updated {formatDate(selected.updated_at)}
                  </div>
                </div>
                <Button variant="primary" icon={Save} loading={editorBusy} onclick={saveEditor}>
                  Save
                </Button>
              </div>

              {#if editorError}
                <Alert tone="error">{editorError}</Alert>
              {/if}

              <Textarea
                bind:value={editorContent}
                mono
                rows={24}
                disabled={editorBusy}
              />
            </div>
          </Card>
        {/if}
      </div>
    </div>
  {/if}
</div>

<!-- Upload dialog ────────────────────────────────────────────────────── -->
<Dialog bind:open={showUpload} title={`Upload ${config.noun}`} size="lg">
  <form onsubmit={submitUpload} class="flex flex-col gap-3">
    {#if uploadError}
      <Alert tone="error">{uploadError}</Alert>
    {/if}

    <div class="flex flex-col gap-1.5 text-xs font-medium text-surface-600-400">
      Pick a file
      <!-- The browser labels a native file input in the OS locale
           ("Examinar…" / "Ningún archivo seleccionado"), which reads as stray
           foreign text in an English UI. Hide it, trigger it from a Button and
           echo the filename ourselves. -->
      <input
        bind:this={fileInput}
        type="file"
        accept={config.accept}
        onchange={handleFilePicked}
        disabled={uploadBusy}
        class="sr-only"
        tabindex="-1"
        aria-hidden="true"
      />
      <div class="flex items-center gap-2">
        <Button
          variant="secondary"
          icon={FileCode}
          disabled={uploadBusy}
          onclick={() => fileInput?.click()}
        >
          {pickedFileName ? 'Choose another file' : 'Choose file'}
        </Button>
        {#if pickedFileName}
          <span class="font-normal text-surface-500">{pickedFileName}</span>
        {/if}
      </div>
    </div>

    <Input
      label={config.nameLabel} help="upload.name"
      type="text"
      autocomplete="off"
      bind:value={uploadName}
      disabled={uploadBusy}
      placeholder={config.namePlaceholder}
    />

    {#if config.showSortOrder}
      <Input
        label="Sort order (optional — base.md uses 0)" help="upload.sort_order"
        type="number"
        autocomplete="off"
        bind:value={uploadSortOrder as unknown as string}
        disabled={uploadBusy}
        placeholder="100"
      />
    {/if}

    <Textarea
      label="Preview" help="upload.preview"
      bind:value={uploadContent}
      mono
      rows={10}
      disabled={uploadBusy}
    />
  </form>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showUpload = false)} disabled={uploadBusy}>Cancel</Button>
    <Button
      variant="primary"
      loading={uploadBusy}
      onclick={() => (document.querySelector('form') as HTMLFormElement | null)?.requestSubmit()}
    >
      Upload
    </Button>
  {/snippet}
</Dialog>
