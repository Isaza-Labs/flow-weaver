<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onDestroy, onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { workflows, importWorkflow, errorMessage, type Workflow, type WorkflowVersion, type DiffResult } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, Badge, Tabs, Dialog, EmptyState,
    Alert, SearchInput, Pagination, Textarea, IconButton, Input, Skeleton,
    SortableTh, Checkbox,
    formatDate, confirm,
    FieldHint,
  } from '$lib/components/ui';
  import { Workflow as WorkflowIcon, Plus, Lock, Trash2, Rocket, Undo2, Copy, GitCommit, Upload, ShieldAlert, Pencil } from 'lucide-svelte';
  import { debounce } from '$lib/utils/debounce';
  import { readUrlInt, readUrlParam, setUrlParams } from '$lib/utils/urlState';
  import { prefsStore } from '$lib/stores/prefs.svelte';
  import { z } from 'zod';
  import { validate } from '$lib/validation/validate';

  let workflowList = $state<Workflow[]>([]);
  let loading = $state(true);
  let error = $state('');
  let showForm = $state(false);
  let newWorkflow = $state({ name: '', description: '' });
  let createErrors = $state<Record<string, string>>({});

  // Edit name/description of an existing (draft) workflow. Production is
  // immutable server-side (409 production_immutable), so this is draft-only.
  let showEditDialog = $state(false);
  let editTarget = $state<Workflow | null>(null);
  let editForm = $state({ name: '', description: '' });
  let editErrors = $state<Record<string, string>>({});
  let editSaving = $state(false);
  let searchQuery = $state(readUrlParam('q') ?? '');
  let activeTab = $state<'draft' | 'qa' | 'production'>(
    readUrlParam('tab') === 'production' ? 'production' : readUrlParam('tab') === 'qa' ? 'qa' : 'draft',
  );
  let total = $state(0);
  let offset = $state(readUrlInt('offset', 0));
  let sortKey = $state<string | null>(readUrlParam('sort'));
  let sortDir = $state<'asc' | 'desc'>(readUrlParam('dir') === 'desc' ? 'desc' : 'asc');
  let selected = $state<Set<string>>(new Set());
  const pageSize = 50;

  const newWorkflowSchema = z.object({
    name: z.string().trim().min(1, 'Name is required').max(120, 'Name must be 120 chars or fewer'),
    description: z.string().max(500, 'Description must be 500 chars or fewer').optional(),
  });

  let showPromoteDialog = $state(false);
  let promoteTarget = $state<Workflow | null>(null);
  let promoteSummary = $state('');
  let promoteDiff = $state<DiffResult | null>(null);
  let promoteRollbackRisk = $state<{
    non_reversible: Array<{ snippet_id: string; snippet_name: string; snippet_type: string; kind: string }>;
    requires_compensation: Array<{ snippet_id: string; snippet_name: string; snippet_type: string; kind: string }>;
    compensated: Array<{ snippet_id: string; snippet_name: string; snippet_type: string; kind: string }>;
  } | null>(null);
  let promoteLoading = $state(false);
  let promoteTargetEnv = $state<'qa' | 'production' | null>(null);
  let promoteApprovedBy = $state('');

  let showRollbackDialog = $state(false);
  let rollbackTarget = $state<Workflow | null>(null);
  let rollbackVersions = $state<WorkflowVersion[]>([]);
  let rollbackLoading = $state(false);

  let showImportDialog = $state(false);
  let importText = $state('');
  let importFormat = $state<'yaml' | 'json'>('yaml');
  let importLoading = $state(false);
  let importError = $state('');
  let importFileInput: HTMLInputElement | null = $state(null);
  let importDragOver = $state(false);

  onMount(() => loadWorkflows(offset));
  onDestroy(() => debouncedSearch.cancel());

  function syncUrl() {
    setUrlParams({
      q: searchQuery.trim() || undefined,
      tab: activeTab === 'draft' ? undefined : activeTab,
      offset: offset || undefined,
      sort: sortKey || undefined,
      dir: sortKey ? sortDir : undefined,
    });
  }

  async function loadWorkflows(requestOffset = offset) {
    loading = true;
    error = '';
    try {
      const trimmedSearch = searchQuery.trim();
      const res = await workflows.list(pageSize, requestOffset, activeTab, trimmedSearch || undefined);
      const lastPageOffset = res.total > 0 ? Math.max(0, Math.floor((res.total - 1) / pageSize) * pageSize) : 0;
      if (requestOffset > 0 && res.data.length === 0 && res.total > 0 && lastPageOffset !== requestOffset) {
        offset = lastPageOffset;
        const retry = await workflows.list(pageSize, lastPageOffset, activeTab, trimmedSearch || undefined);
        workflowList = retry.data;
        total = retry.total;
      } else {
        offset = requestOffset;
        workflowList = res.data;
        total = res.total;
      }
      selected = new Set();
      syncUrl();
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load workflows');
    } finally {
      loading = false;
    }
  }

  // Apply client-side sort on top of the server-paged page. The backend
  // doesn't sort by these fields yet, so this is best-effort — the user
  // sees the current page sorted consistently.
  const sortedList = $derived.by(() => {
    if (!sortKey) return workflowList;
    const key = sortKey;
    const dir = sortDir === 'asc' ? 1 : -1;
    return [...workflowList].sort((a, b) => {
      const av = (a as unknown as Record<string, unknown>)[key];
      const bv = (b as unknown as Record<string, unknown>)[key];
      if (av == null && bv == null) return 0;
      if (av == null) return 1;
      if (bv == null) return -1;
      if (typeof av === 'number' && typeof bv === 'number') return (av - bv) * dir;
      return String(av).localeCompare(String(bv)) * dir;
    });
  });

  function onSort(column: string) {
    if (sortKey === column) {
      sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    } else {
      sortKey = column;
      sortDir = 'asc';
    }
    syncUrl();
  }

  async function switchTab(tab: 'draft' | 'qa' | 'production') {
    activeTab = tab;
    offset = 0;
    await loadWorkflows(0);
  }

  async function createWorkflow(e: Event) {
    e.preventDefault();
    const result = validate(newWorkflowSchema, newWorkflow);
    if (!result.success) {
      createErrors = result.errors;
      return;
    }
    createErrors = {};
    try {
      await workflows.create(result.data);
      newWorkflow = { name: '', description: '' };
      showForm = false;
      await loadWorkflows(0);
    } catch (err) {
      toast.fromError(err, 'Couldn’t create workflow');
    }
  }

  async function deleteWorkflow(wf: Workflow) {
    if (!(await confirm({ title: `Delete "${wf.name}"?`, message: 'This cannot be undone.', tone: 'danger', confirmLabel: 'Delete' }))) return;
    try {
      await workflows.delete(wf.id);
      await loadWorkflows(offset);
      toast.success(`Deleted "${wf.name}"`);
    } catch (err) {
      toast.fromError(err, 'Couldn’t delete workflow');
    }
  }

  function openEditDialog(wf: Workflow) {
    editTarget = wf;
    editForm = { name: wf.name, description: wf.description ?? '' };
    editErrors = {};
    showEditDialog = true;
  }

  async function saveEdit(e?: Event) {
    e?.preventDefault();
    if (!editTarget || editSaving) return;
    const result = validate(newWorkflowSchema, editForm);
    if (!result.success) {
      editErrors = result.errors;
      return;
    }
    editErrors = {};
    editSaving = true;
    try {
      await workflows.update(editTarget.id, {
        name: result.data.name,
        description: result.data.description ?? '',
      });
      showEditDialog = false;
      editTarget = null;
      await loadWorkflows(offset);
      toast.success('Workflow updated');
    } catch (err) {
      toast.fromError(err, 'Couldn’t update workflow');
    } finally {
      editSaving = false;
    }
  }

  async function deleteSelected() {
    if (selected.size === 0) return;
    const ids = Array.from(selected);
    const ok = await confirm({
      title: `Delete ${ids.length} workflow${ids.length === 1 ? '' : 's'}?`,
      message: 'This cannot be undone.',
      tone: 'danger',
      confirmLabel: 'Delete all',
    });
    if (!ok) return;
    let failed = 0;
    await Promise.all(ids.map((id) => workflows.delete(id).catch(() => { failed += 1; })));
    selected = new Set();
    await loadWorkflows(offset);
    if (failed === 0) toast.success(`Deleted ${ids.length} workflow${ids.length === 1 ? '' : 's'}`);
    else toast.warning(`Deleted ${ids.length - failed} workflow${ids.length - failed === 1 ? '' : 's'}, ${failed} failed`);
  }

  async function openPromoteDialog(wf: Workflow) {
    promoteTarget = wf;
    promoteSummary = '';
    promoteApprovedBy = '';
    promoteDiff = null;
    const env = (wf.environment ?? 'draft').toLowerCase();
    promoteTargetEnv = env === 'draft' ? 'qa' : env === 'qa' ? 'production' : null;
    if (promoteTargetEnv === null) {
      toast.error('Nothing to promote', {
        description: 'Production workflows can only be rolled back or cloned.',
      });
      return;
    }
    promoteLoading = true;
    showPromoteDialog = true;
    promoteRollbackRisk = null;
    try {
      const [diff, risk] = await Promise.all([
        workflows.diff(wf.id).catch(() => null),
        workflows.rollbackRisk(wf.id).catch(() => null),
      ]);
      promoteDiff = diff;
      promoteRollbackRisk = risk;
    } finally {
      promoteLoading = false;
    }
  }

  async function confirmPromote() {
    if (!promoteTarget || !promoteSummary.trim() || !promoteTargetEnv) return;
    if (promoteTargetEnv === 'production' && !promoteApprovedBy.trim()) return;
    promoteLoading = true;
    try {
      await workflows.promote(promoteTarget.id, {
        target_environment: promoteTargetEnv,
        change_summary: promoteSummary,
        approved_by: promoteTargetEnv === 'production' ? promoteApprovedBy.trim() : undefined,
      });
      showPromoteDialog = false;
      promoteTarget = null;
      await loadWorkflows(offset);
      toast.success('Promotion applied');
    } catch (err) {
      toast.fromError(err, 'Couldn’t apply promote');
    } finally {
      promoteLoading = false;
    }
  }

  async function openRollbackDialog(wf: Workflow) {
    rollbackTarget = wf;
    rollbackVersions = [];
    rollbackLoading = true;
    showRollbackDialog = true;
    try {
      rollbackVersions = await workflows.versions(wf.id);
    } catch (err) {
      toast.fromError(err, 'Couldn’t load version history');
    } finally {
      rollbackLoading = false;
    }
  }

  async function confirmRollback(version: number) {
    if (!rollbackTarget) return;
    if (!(await confirm({ title: `Rollback to v${version}?`, tone: 'primary', confirmLabel: 'Rollback' }))) return;
    rollbackLoading = true;
    try {
      await workflows.rollback(rollbackTarget.id, version);
      showRollbackDialog = false;
      rollbackTarget = null;
      await loadWorkflows(offset);
      toast.success(`Rolled back to v${version}`);
    } catch (err) {
      const msg = errorMessage(err);
      toast.fromError(err, msg.includes('rollback blocked')
        ? 'Rollback blocked by non-reversible step(s) — build a forward fix in a new draft.'
        : 'Couldn’t apply rollback');
    } finally {
      rollbackLoading = false;
    }
  }

  async function cloneToDraft(wf: Workflow) {
    try {
      await workflows.clone(wf.id);
      activeTab = 'draft';
      await loadWorkflows(0);
      toast.success(`Cloned "${wf.name}" to draft`);
    } catch (err) {
      toast.fromError(err, 'Couldn’t clone to draft');
    }
  }

  // Debounced search: each keystroke schedules a load 250 ms in the future.
  // The shared helper guarantees we cancel the pending timer on the next
  // keystroke and on unmount.
  const debouncedSearch = debounce((v: string) => {
    searchQuery = v;
    void loadWorkflows(0);
  }, 250);
  function queueSearch(v: string) {
    searchQuery = v;
    debouncedSearch(v);
  }

  async function goToPrev() { if (offset > 0) await loadWorkflows(Math.max(0, offset - pageSize)); }
  async function goToNext() { if (offset + workflowList.length < total) await loadWorkflows(offset + pageSize); }

  function openImportDialog() {
    importText = '';
    importError = '';
    importFormat = 'yaml';
    showImportDialog = true;
  }

  async function loadFileIntoImport(file: File) {
    const lower = file.name.toLowerCase();
    importFormat = lower.endsWith('.json') ? 'json' : 'yaml';
    importText = await file.text();
  }

  async function onImportFilePicked(e: Event) {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    await loadFileIntoImport(file);
    input.value = '';
  }

  async function onImportDrop(e: DragEvent) {
    e.preventDefault();
    importDragOver = false;
    const file = e.dataTransfer?.files?.[0];
    if (file) await loadFileIntoImport(file);
  }

  async function confirmImport() {
    if (!importText.trim()) {
      importError = 'Paste a workflow document or pick a file first.';
      return;
    }
    importLoading = true;
    importError = '';
    try {
      const created = await importWorkflow.upload(importText, importFormat);
      showImportDialog = false;
      toast.success('Workflow imported');
      await goto(`/workflows/${created.id}`);
    } catch (err) {
      importError = errorMessage(err);
      toast.fromError(err, 'Import failed');
    } finally {
      importLoading = false;
    }
  }

  function toggleSelectAll() {
    if (selected.size === sortedList.length) {
      selected = new Set();
    } else {
      selected = new Set(sortedList.map((w) => w.id));
    }
  }
  function toggleOne(id: string) {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    selected = next;
  }
</script>

<svelte:head><title>Workflows · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader title="Workflows" description="Author, version and ship automation pipelines.">
    {#snippet actions()}
      {#if activeTab === 'draft'}
        <Button variant="secondary" icon={Upload} href="/workflows/import">Import</Button>
        <Button variant="primary" icon={Plus} onclick={() => (showForm = !showForm)}>
          {showForm ? 'Cancel' : 'New workflow'}
        </Button>
      {/if}
    {/snippet}
  </PageHeader>

  {#if error}<Alert tone="error" dismissible onDismiss={() => (error = '')}>{error}</Alert>{/if}

  {#if showForm && activeTab === 'draft'}
    <Card>
      <form onsubmit={createWorkflow} class="space-y-3">
        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div class="flex flex-col gap-1">
            <label for="wf-name" class="text-xs font-medium text-surface-600-400 inline-flex items-center gap-1">Name <FieldHint id="workflows.new_name" /></label>
            <input id="wf-name" bind:value={newWorkflow.name} placeholder="Workflow name" class="h-8 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm px-3 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30" />
            {#if createErrors.name}<p class="text-xs text-error-400">{createErrors.name}</p>{/if}
          </div>
          <div class="flex flex-col gap-1">
            <label for="wf-desc" class="text-xs font-medium text-surface-600-400 inline-flex items-center gap-1">Description <FieldHint id="workflows.new_description" /></label>
            <input id="wf-desc" bind:value={newWorkflow.description} placeholder="Description" class="h-8 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm px-3 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30" />
            {#if createErrors.description}<p class="text-xs text-error-400">{createErrors.description}</p>{/if}
          </div>
        </div>
        <div class="flex justify-end">
          <Button variant="primary" type="submit">Create workflow</Button>
        </div>
      </form>
    </Card>
  {/if}

  <div class="flex items-center justify-between gap-3 flex-wrap">
    <div class="flex items-center gap-3">
      <FieldHint id="workflows.environment_filter" />
      <Tabs
        bind:value={activeTab}
        tabs={[
          { value: 'draft', label: 'Draft' },
          { value: 'qa', label: 'QA' },
          { value: 'production', label: 'Production' },
        ]}
        onChange={() => switchTab(activeTab)}
      />
      {#if selected.size > 0}
        <span class="text-xs text-surface-500">{selected.size} selected</span>
        <Button size="xs" variant="danger" icon={Trash2} onclick={deleteSelected}>Delete selected</Button>
      {/if}
    </div>
    <div class="flex items-center gap-1">
      <SearchInput value={searchQuery} placeholder="Search workflows…" onInput={queueSearch} />
      <FieldHint id="workflows.search" />
    </div>
  </div>

  {#if loading}
    <Skeleton variant="table" count={6} columns={6} />
  {:else if workflowList.length === 0}
    <Card padding="none">
      <EmptyState
        icon={WorkflowIcon}
        title={searchQuery.trim() ? 'No workflows match your search' : `No ${activeTab} workflows yet`}
        description={searchQuery.trim() ? 'Try a different keyword.' : `Click "New workflow" to start.`}
      >
        {#snippet actions()}
          {#if !searchQuery.trim() && activeTab === 'draft'}
            <Button variant="primary" icon={Plus} onclick={() => (showForm = true)}>New workflow</Button>
            <Button variant="secondary" icon={Upload} href="/workflows/import">Import</Button>
          {/if}
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <DataTable density={prefsStore.density}>
      <thead>
        <tr>
          <th class="w-8">
            <Checkbox
              checked={selected.size > 0 && selected.size === sortedList.length}
              indeterminate={selected.size > 0 && selected.size < sortedList.length}
              onChange={toggleSelectAll}
              label="Select all"
              labelHidden
            />
          </th>
          <SortableTh column="name" {sortKey} {sortDir} onSort={onSort}>Name</SortableTh>
          <th>Description</th>
          {#if activeTab !== 'draft'}
            <SortableTh column="version" {sortKey} {sortDir} onSort={onSort}>Version</SortableTh>
          {/if}
          <th class="!text-right">Nodes</th>
          <th class="!text-right">Edges</th>
          <SortableTh column="created_at" {sortKey} {sortDir} onSort={onSort}>Created</SortableTh>
          <th class="!text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each sortedList as wf (wf.id)}
          <tr data-selected={selected.has(wf.id)}>
            <td>
              <Checkbox checked={selected.has(wf.id)} onChange={() => toggleOne(wf.id)} label="Select {wf.name}" labelHidden />
            </td>
            <td>
              <div class="flex items-center gap-2">
                {#if activeTab !== 'draft'}
                  <Lock size={12} class="text-warning-400 shrink-0" />
                {/if}
                <a href="/workflows/{wf.id}" class="font-medium text-primary-300 hover:text-primary-200 transition-colors">{wf.name}</a>
              </div>
            </td>
            <td class="text-surface-600-400 truncate max-w-xs">{wf.description || '—'}</td>
            {#if activeTab !== 'draft'}
              <td><Badge tone="success" mono>v{wf.version}</Badge></td>
            {/if}
            <td class="text-right text-surface-700-300 tabular-nums">{wf.nodes?.length ?? 0}</td>
            <td class="text-right text-surface-700-300 tabular-nums">{wf.edges?.length ?? 0}</td>
            <td class="text-surface-600-400 text-xs">{formatDate(wf.created_at)}</td>
            <td class="text-right whitespace-nowrap space-x-1">
              {#if activeTab === 'draft'}
                <Button size="xs" variant="success" icon={Rocket} onclick={() => openPromoteDialog(wf)}>Promote</Button>
                <IconButton icon={Pencil} label="Edit name & description" onclick={() => openEditDialog(wf)} />
              {:else if activeTab === 'qa'}
                <Button size="xs" variant="success" icon={Rocket} onclick={() => openPromoteDialog(wf)}>Promote</Button>
                <Button size="xs" variant="secondary" icon={Copy} onclick={() => cloneToDraft(wf)}>Clone</Button>
              {:else}
                <Button size="xs" variant="secondary" icon={Undo2} onclick={() => openRollbackDialog(wf)}>Rollback</Button>
                <Button size="xs" variant="secondary" icon={Copy} onclick={() => cloneToDraft(wf)}>Clone</Button>
              {/if}
              <IconButton icon={Trash2} label="Delete" variant="danger" onclick={() => deleteWorkflow(wf)} />
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
    <Pagination {offset} {pageSize} {total} label="workflow" onPrev={goToPrev} onNext={goToNext} />
  {/if}
</div>

<!-- Promote Dialog -->
<Dialog
  bind:open={showPromoteDialog}
  title={promoteTargetEnv ? `Promote ${promoteTarget?.environment ?? 'draft'} → ${promoteTargetEnv}` : 'Promote workflow'}
  size="md"
>
  {#if promoteTarget && promoteTargetEnv}
    <div class="space-y-4">
      <div class="flex items-center gap-3">
        <div>
          <span class="text-xs text-surface-500">Workflow</span>
          <div class="text-sm font-medium text-surface-900-100">{promoteTarget.name}</div>
        </div>
        <div class="ml-auto flex items-center gap-2">
          <Badge tone="neutral">{promoteTarget.environment ?? 'draft'}</Badge>
          <span class="text-surface-500">→</span>
          <Badge tone={promoteTargetEnv === 'production' ? 'error' : 'warning'}>{promoteTargetEnv}</Badge>
        </div>
      </div>

      {#if promoteLoading}
        <Skeleton variant="row" count={2} />
      {:else if promoteDiff}
        <Card padding="sm">
          <div class="text-xs font-medium text-surface-700-300 mb-2 flex items-center gap-1.5"><GitCommit size={12} /> Changes detected</div>
          <ul class="text-xs space-y-0.5 font-mono">
            {#if promoteDiff.nodes_added.length > 0}<li class="text-success-400">+ {promoteDiff.nodes_added.length} node(s) added</li>{/if}
            {#if promoteDiff.nodes_removed.length > 0}<li class="text-error-400">- {promoteDiff.nodes_removed.length} node(s) removed</li>{/if}
            {#if promoteDiff.nodes_changed.length > 0}<li class="text-warning-400">~ {promoteDiff.nodes_changed.length} node(s) changed</li>{/if}
            {#if promoteDiff.edges_added.length > 0}<li class="text-success-400">+ {promoteDiff.edges_added.length} edge(s) added</li>{/if}
            {#if promoteDiff.edges_removed.length > 0}<li class="text-error-400">- {promoteDiff.edges_removed.length} edge(s) removed</li>{/if}
            {#if !promoteDiff.has_changes}<li class="text-surface-500 not-italic">No changes from current {promoteTargetEnv} version.</li>{/if}
          </ul>
        </Card>
      {:else}
        <Alert tone="info">First promotion to {promoteTargetEnv} — no existing version to compare.</Alert>
      {/if}

      {#if promoteRollbackRisk}
        {@const nrCount = promoteRollbackRisk.non_reversible.length}
        {@const rcCount = promoteRollbackRisk.requires_compensation.length}
        {@const compCount = promoteRollbackRisk.compensated.length}
        {#if nrCount > 0}
          <Alert tone="error">
            <div class="flex items-start gap-2">
              <ShieldAlert size={14} class="mt-0.5 flex-shrink-0" />
              <div class="space-y-1">
                <div class="font-medium">Rollback blocked: {nrCount} non-reversible step{nrCount === 1 ? '' : 's'}</div>
                <div class="text-xs">
                  Once this version reaches production, <code>RollbackAsync</code> will refuse to revert it because the following step{nrCount === 1 ? '' : 's'} cannot be undone:
                </div>
                <ul class="text-xs font-mono list-disc list-inside">
                  {#each promoteRollbackRisk.non_reversible as r}
                    <li>{r.snippet_name} <span class="text-surface-500">({r.snippet_type})</span></li>
                  {/each}
                </ul>
                <div class="text-xs text-surface-500">To recover, you'll need to build a forward fix in a new draft.</div>
              </div>
            </div>
          </Alert>
        {:else if rcCount > 0}
          <Alert tone="warning">
            <div class="flex items-start gap-2">
              <ShieldAlert size={14} class="mt-0.5 flex-shrink-0" />
              <div class="space-y-1">
                <div class="font-medium">{rcCount} step{rcCount === 1 ? '' : 's'} require compensation on rollback</div>
                <div class="text-xs">
                  These steps mutate external state. Add a <code>failure</code> edge with a compensating action so rollback works cleanly:
                </div>
                <ul class="text-xs font-mono list-disc list-inside">
                  {#each promoteRollbackRisk.requires_compensation as r}
                    <li>{r.snippet_name} <span class="text-surface-500">({r.snippet_type})</span></li>
                  {/each}
                </ul>
                {#if compCount > 0}
                  <div class="text-xs text-surface-500">{compCount} other mutation step{compCount === 1 ? '' : 's'} already have compensation edges wired.</div>
                {/if}
              </div>
            </div>
          </Alert>
        {/if}
      {/if}

      <Textarea label="Change summary (required)" help="workflows.promote_summary" bind:value={promoteSummary} rows={3} placeholder="Describe what changed and why…" />

      {#if promoteTargetEnv === 'production'}
        <Input
          label="Approved by (required)"
          help="workflows.promote_approved_by"
          bind:value={promoteApprovedBy}
          placeholder="Name / email of the second reviewer"
        />
        <p class="text-xs text-surface-500">
          Production promotion needs an approver different from the operator running this
          action. It also requires a completed run of the qa workflow in the last 48 hours
          — see the <a class="text-primary-400 hover:underline" href="/qa">QA lab</a> for readiness.
        </p>
      {/if}
    </div>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showPromoteDialog = false)}>Cancel</Button>
    <Button
      variant="success"
      icon={Rocket}
      onclick={confirmPromote}
      disabled={!promoteSummary.trim() || promoteLoading || (promoteTargetEnv === 'production' && !promoteApprovedBy.trim())}
      loading={promoteLoading}
    >
      Promote to {promoteTargetEnv ?? 'next'}
    </Button>
  {/snippet}
</Dialog>

<!-- Edit name/description Dialog -->
<Dialog bind:open={showEditDialog} title="Edit workflow" size="md">
  {#if editTarget}
    <form onsubmit={saveEdit} class="space-y-3">
      <div>
        <Input label="Name" help="workflows.name" bind:value={editForm.name} placeholder="Workflow name" />
        {#if editErrors.name}<p class="text-xs text-error-400 mt-1">{editErrors.name}</p>{/if}
      </div>
      <div>
        <Textarea label="Description" help="workflows.description" bind:value={editForm.description} rows={3} placeholder="Optional description" />
        {#if editErrors.description}<p class="text-xs text-error-400 mt-1">{editErrors.description}</p>{/if}
      </div>
    </form>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showEditDialog = false)} disabled={editSaving}>Cancel</Button>
    <Button variant="primary" onclick={() => saveEdit()} loading={editSaving}>Save changes</Button>
  {/snippet}
</Dialog>

<!-- Import Dialog -->
<Dialog bind:open={showImportDialog} title="Import workflow" size="md">
  <div class="space-y-3">
    <p class="text-xs text-surface-500">
      Paste a workflow YAML or JSON exported from FlowWeaver, drop a file below, or pick one.
      The imported workflow lands as a fresh draft.
    </p>
    <!-- A .bundle.json is detected from its own marker, so no extra control is
         needed here — but users need to know which export to ask a colleague
         for, because the other formats simply cannot resolve elsewhere. -->
    <p class="text-xs text-surface-500">
      A <strong>.bundle.json</strong> from another FlowWeaver instance is recognised
      automatically: its snippets are recreated from the definitions it carries and its
      integrations are bound to the ones you already have, matched by identity. If this
      instance is missing an integration the workflow needs, the import stops and names it
      instead of substituting anything.
    </p>
    <div class="flex items-center gap-2">
      <Button size="xs" variant="ghost" icon={Upload} onclick={() => importFileInput?.click()}>
        Choose file…
      </Button>
      <input
        bind:this={importFileInput}
        type="file"
        accept=".yaml,.yml,.json"
        class="hidden"
        onchange={onImportFilePicked}
      />
      <div class="ml-auto flex items-center gap-1 text-xs">
        <label for="import-fmt" class="text-surface-500">Format</label><FieldHint id="import.format_hint" />
        <select
          id="import-fmt"
          bind:value={importFormat}
          class="h-7 bg-surface-50-950 border border-surface-300-700 rounded-md px-2 text-xs"
        >
          <option value="yaml">YAML</option>
          <option value="json">JSON</option>
        </select>
      </div>
    </div>
    <div
      class="relative rounded-md transition-colors {importDragOver ? 'ring-2 ring-primary-500/60 bg-primary-500/5' : ''}"
      ondragover={(e) => { e.preventDefault(); importDragOver = true; }}
      ondragleave={() => (importDragOver = false)}
      ondrop={onImportDrop}
      role="region"
      aria-label="Drop workflow file here"
    >
      <Textarea
        bind:value={importText}
        rows={14}
        placeholder={importFormat === 'yaml' ? 'version: 1\nworkflow:\n  name: …' : '{ "workflow": { "name": "…" }, "nodes": [], "edges": [] }'}
      />
      {#if importDragOver}
        <div class="pointer-events-none absolute inset-0 flex items-center justify-center text-sm font-medium text-primary-200 rounded-md">
          Drop file to load
        </div>
      {/if}
    </div>
    {#if importError}
      <Alert tone="error">{importError}</Alert>
    {/if}
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showImportDialog = false)} disabled={importLoading}>Cancel</Button>
    <Button variant="primary" icon={Upload} onclick={confirmImport} loading={importLoading} disabled={importLoading || !importText.trim()}>
      Import
    </Button>
  {/snippet}
</Dialog>

<!-- Rollback Dialog -->
<Dialog bind:open={showRollbackDialog} title="Rollback version" size="md">
  {#if rollbackTarget}
    <div class="space-y-4">
      <div>
        <span class="text-xs text-surface-500">Workflow</span>
        <div class="text-sm font-medium text-surface-900-100">{rollbackTarget.name} <Badge tone="success" mono>v{rollbackTarget.version}</Badge></div>
      </div>

      {#if rollbackLoading}
        <Skeleton variant="row" count={3} />
      {:else if rollbackVersions.length === 0}
        <p class="text-sm text-surface-500">No version history available.</p>
      {:else}
        <div class="max-h-72 overflow-y-auto space-y-2">
          {#each rollbackVersions as ver}
            <Card padding="sm">
              <div class="flex items-center justify-between gap-3">
                <div class="min-w-0">
                  <div class="flex items-center gap-2">
                    <Badge tone="success" mono>v{ver.version}</Badge>
                    <span class="text-xs text-surface-500">{formatDate(ver.created_at)}</span>
                  </div>
                  {#if ver.change_summary}
                    <p class="text-xs text-surface-600-400 mt-1">{ver.change_summary}</p>
                  {/if}
                  {#if ver.promoted_by}
                    <p class="text-xs text-surface-500 mt-0.5">by {ver.promoted_by}</p>
                  {/if}
                </div>
                <Button
                  size="xs"
                  variant={ver.version === rollbackTarget.version ? 'ghost' : 'secondary'}
                  disabled={ver.version === rollbackTarget.version}
                  onclick={() => confirmRollback(ver.version)}
                >
                  {ver.version === rollbackTarget.version ? 'Current' : 'Restore'}
                </Button>
              </div>
            </Card>
          {/each}
        </div>
      {/if}
    </div>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showRollbackDialog = false)}>Close</Button>
  {/snippet}
</Dialog>
