<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { snippets, errorMessage, type Snippet } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, Badge, EmptyState,
    Alert, Spinner, SearchInput, confirm,
    FieldHint, Checkbox, Dialog,
  } from '$lib/components/ui';
  import { goto } from '$app/navigation';
  import { Settings2, Plus, Edit, Play, Copy, Trash2, ChevronDown, ChevronRight, Upload, Download } from 'lucide-svelte';
  import {
    HANDLER_FLOOR, effectiveIdempotency as computeEffective,
    idempotencyTone as toneFor, idempotencyShort as shortFor,
    type IdempotencyKind,
  } from '$lib/workflow/idempotency';
  import {
    downloadBundle, parseBundle, importedName, type PortableSnippet,
  } from '$lib/snippets/portable';

  let snippetList = $state<Snippet[]>([]);
  let loading = $state(true);
  let error = $state('');
  let expandedSchemas = $state(new Set<string>());
  let searchText = $state('');
  // Filter pill that narrows the list to snippets
  // whose authors set an explicit rollback policy override. Useful for
  // periodic review ("which snippets did someone tag as idempotent?
  // is that still true?").
  let onlyOverridden = $state(false);

  const filtered = $derived.by(() => {
    const q = searchText.trim().toLowerCase();
    let list = q
      ? snippetList.filter(
          (s) =>
            s.name.toLowerCase().includes(q) ||
            (s.type ?? '').toLowerCase().includes(q) ||
            (s.description ?? '').toLowerCase().includes(q),
        )
      : snippetList;
    if (onlyOverridden) list = list.filter((s) => s.idempotency != null);
    return list;
  });

  onMount(loadAll);

  async function loadAll() {
    loading = true;
    error = '';
    try {
      const res = await snippets.list(500, 0);
      snippetList = res.data ?? [];
    } catch (e) {
      error = errorMessage(e);
          toast.fromError(e, 'Couldn’t load all');
    } finally {
      loading = false;
    }
  }

  function toggleSchema(name: string) {
    const next = new Set(expandedSchemas);
    if (next.has(name)) next.delete(name);
    else next.add(name);
    expandedSchemas = next;
  }

  // Copying an existing snippet is the only way to start from a body that already
  // works. /snippets/new offers ONE generic starter per handler type, so the
  // seeded baselines — the paramiko SSH primitive above all — are unreachable from
  // there, and retyping one from memory is how the details that matter get lost.
  let duplicating = $state<string | null>(null);

  // For the reader, not the database: the backend does not require unique names,
  // and three copies of one baseline all reading "X (copy)" are three rows nobody
  // can tell apart in the node picker.
  function copyName(base: string) {
    const taken = new Set(snippetList.map((s) => s.name));
    let candidate = `${base} (copy)`;
    for (let n = 2; taken.has(candidate); n++) candidate = `${base} (copy ${n})`;
    return candidate;
  }

  async function duplicateSnippet(snip: Snippet) {
    duplicating = snip.id;
    const name = copyName(snip.name);
    try {
      let created: Snippet;
      try {
        created = await snippets.duplicate(snip.id, { name });
      } catch (e) {
        // network_enabled is admin-gated on create. A non-admin duplicating the
        // paramiko baseline should still get their copy — an inert one, said out
        // loud — rather than an error that reads as "duplicate is broken".
        if (snip.network_enabled && /admin role/i.test(errorMessage(e))) {
          created = await snippets.duplicate(snip.id, { name, network_enabled: false });
          toast.warning('Copied without network access', {
            description: 'Enabling it for interactive SSH needs an admin.',
          });
        } else {
          throw e;
        }
      }
      await goto(`/snippets/${created.id}`);
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t duplicate snippet');
    } finally {
      duplicating = null;
    }
  }

  // ── Selection + export ────────────────────────────────────────────────
  // Export takes the selected rows; with nothing selected it takes what the
  // filter currently shows, so "search, then Export" works without ticking boxes.
  let selected = $state(new Set<string>());
  const selectedVisible = $derived(filtered.filter((s) => selected.has(s.id)));
  const exportTargets = $derived(selectedVisible.length > 0 ? selectedVisible : filtered);

  function toggleOne(id: string) {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    selected = next;
  }

  function toggleSelectAll() {
    selected = selectedVisible.length === filtered.length ? new Set() : new Set(filtered.map((s) => s.id));
  }

  function exportSnippets() {
    if (exportTargets.length === 0) return;
    downloadBundle(exportTargets);
    toast.success(`Exported ${exportTargets.length} snippet${exportTargets.length === 1 ? '' : 's'}`);
  }

  // ── Import ────────────────────────────────────────────────────────────
  // Client-side: the file is parsed here and each entry goes through the normal
  // create/update endpoints, so the backend applies exactly the validation (and
  // the admin gate on network_enabled) it applies to a snippet typed by hand.
  type ConflictPolicy = 'rename' | 'skip' | 'overwrite';

  let showImport = $state(false);
  let importText = $state('');
  let importFileName = $state('');
  let importError = $state('');
  let importing = $state(false);
  let importDragOver = $state(false);
  let conflictPolicy = $state<ConflictPolicy>('rename');
  let importFileInput: HTMLInputElement | null = $state(null);

  const importPreview = $derived.by(() => {
    if (!importText.trim()) return null;
    try {
      const parsed = parseBundle(importText);
      const existing = new Set(snippetList.map((s) => s.name));
      const conflicts = parsed.snippets.filter((s) => existing.has(s.name)).length;
      return { ...parsed, existing, conflicts, error: '' };
    } catch (e) {
      return {
        snippets: [] as PortableSnippet[], problems: [] as string[],
        existing: new Set<string>(), conflicts: 0, error: (e as Error).message,
      };
    }
  });

  function openImport() {
    importText = '';
    importFileName = '';
    importError = '';
    conflictPolicy = 'rename';
    showImport = true;
  }

  async function loadImportFile(file: File) {
    importFileName = file.name;
    importError = '';
    importText = await file.text();
  }

  async function onImportFilePicked(e: Event) {
    const input = e.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    if (file) await loadImportFile(file);
    input.value = '';
  }

  async function onImportDrop(e: DragEvent) {
    e.preventDefault();
    importDragOver = false;
    const file = e.dataTransfer?.files?.[0];
    if (file) await loadImportFile(file);
  }

  // Same fallback as duplicate: a non-admin importing a network-enabled snippet
  // gets an inert copy, said out loud, instead of a failed row.
  async function writeWithNetworkFallback(
    write: (data: Partial<Snippet>) => Promise<unknown>,
    data: PortableSnippet,
    downgraded: string[],
  ) {
    try {
      await write(data);
    } catch (e) {
      if (data.network_enabled && /admin role/i.test(errorMessage(e))) {
        await write({ ...data, network_enabled: false });
        downgraded.push(data.name);
      } else {
        throw e;
      }
    }
  }

  async function confirmImport() {
    const preview = importPreview;
    if (!preview || preview.error || preview.snippets.length === 0) return;
    importing = true;
    importError = '';

    const byName = new Map(snippetList.map((s) => [s.name, s]));
    const taken = new Set(snippetList.map((s) => s.name));
    let created = 0, updated = 0, skipped = 0;
    const failed: string[] = [];
    const downgraded: string[] = [];

    for (const entry of preview.snippets) {
      const existing = byName.get(entry.name);
      try {
        if (existing && conflictPolicy === 'skip') {
          skipped++;
        } else if (existing && conflictPolicy === 'overwrite') {
          await writeWithNetworkFallback((d) => snippets.update(existing.id, d), entry, downgraded);
          updated++;
        } else {
          // Also renames a clash WITHIN the file, so two entries sharing a name
          // both land instead of the second silently shadowing the first.
          const name = taken.has(entry.name) ? importedName(entry.name, taken) : entry.name;
          await writeWithNetworkFallback((d) => snippets.create(d), { ...entry, name }, downgraded);
          taken.add(name);
          created++;
        }
      } catch (e) {
        failed.push(`${entry.name}: ${errorMessage(e)}`);
      }
    }

    importing = false;
    await loadAll();

    const parts = [
      created && `${created} created`,
      updated && `${updated} updated`,
      skipped && `${skipped} skipped`,
    ].filter(Boolean).join(', ');
    if (downgraded.length) {
      toast.warning('Imported without network access', {
        description: `${downgraded.join(', ')} — enabling it for interactive SSH needs an admin.`,
      });
    }
    if (failed.length) {
      // Keep the dialog open with the reasons; what did succeed is already saved.
      importError = `${failed.length} failed${parts ? ` (${parts})` : ''}:\n${failed.join('\n')}`;
      toast.error(`Import finished with ${failed.length} error${failed.length === 1 ? '' : 's'}`);
    } else {
      showImport = false;
      toast.success(`Import complete${parts ? `: ${parts}` : ''}`);
    }
  }

  async function deleteSnippet(snip: Snippet) {
    if (!(await confirm({ title: `Delete "${snip.name}"?`, message: 'This cannot be undone.', tone: 'danger', confirmLabel: 'Delete' }))) return;
    try {
      await snippets.delete(snip.id);
      await loadAll();
    } catch (e) {
      error = errorMessage(e);toast.fromError(e, 'Couldn’t delete snippet');
    }
  }
</script>

<svelte:head><title>Snippets · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader title="Snippets" description="Reusable building blocks workflows can call.">
    {#snippet actions()}
      <Button variant="secondary" icon={Upload} onclick={openImport}>Import</Button>
      <Button
        variant="secondary"
        icon={Download}
        onclick={exportSnippets}
        disabled={exportTargets.length === 0}
        title={selectedVisible.length > 0 ? 'Export the selected snippets as JSON' : 'Export the snippets shown by the current filter as JSON'}
      >
        Export{selectedVisible.length > 0 ? ` selected (${selectedVisible.length})` : ''}
      </Button>
      <Button variant="primary" icon={Plus} href="/snippets/new">New snippet</Button>
    {/snippet}
  </PageHeader>

  {#if error}<Alert tone="error" dismissible onDismiss={() => (error = '')}>{error}</Alert>{/if}

  <div class="flex items-center gap-3 flex-wrap">
    <SearchInput bind:value={searchText} placeholder="Filter by name, type, description…" width="w-80" />
    <label class="inline-flex items-center gap-1.5 text-xs text-surface-700-300 cursor-pointer select-none">
      <input
        type="checkbox"
        bind:checked={onlyOverridden}
        class="rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-primary-500"
      />
      Only with rollback override <FieldHint id="snippets.search" />
    </label>
    <span class="text-xs text-surface-500 tabular-nums">{filtered.length} of {snippetList.length}</span>
  </div>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if snippetList.length === 0}
    <Card padding="none">
      <EmptyState icon={Settings2} title="No snippets yet" description="Create your first reusable snippet.">
        {#snippet actions()}
          <Button variant="primary" icon={Plus} href="/snippets/new">New snippet</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else if filtered.length === 0}
    <Card><p class="text-sm text-surface-500 text-center py-6">No snippets match the filter.</p></Card>
  {:else}
    <DataTable>
      <thead>
        <tr>
          <th class="w-8">
            <Checkbox
              checked={selectedVisible.length > 0 && selectedVisible.length === filtered.length}
              indeterminate={selectedVisible.length > 0 && selectedVisible.length < filtered.length}
              onChange={toggleSelectAll}
              label="Select all"
              labelHidden
            />
          </th>
          <th></th>
          <th>Name</th>
          <th>Type</th>
          <th>Target</th>
          <th>Parallel</th>
          <th>Timeout</th>
          <th>Rollback</th>
          <th class="!text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each filtered as snip (snip.id)}
          {@const expanded = expandedSchemas.has(snip.name)}
          {@const hasSchema = snip.input_schema && Object.keys(snip.input_schema).length > 0}
          {@const effIdempotency = computeEffective(snip.type, snip.idempotency)}
          {@const hasIdempotencyOverride = snip.idempotency != null}
          <tr data-selected={selected.has(snip.id)}>
            <td>
              <Checkbox checked={selected.has(snip.id)} onChange={() => toggleOne(snip.id)} label="Select {snip.name}" labelHidden />
            </td>
            <td class="!pl-2 !pr-0 w-8">
              {#if hasSchema}
                <button
                  type="button"
                  onclick={() => toggleSchema(snip.name)}
                  class="inline-flex items-center justify-center w-5 h-5 rounded text-surface-500 hover:text-surface-900-100 hover:bg-surface-200-800/60 transition-colors"
                  aria-label="Toggle schema"
                >
                  {#if expanded}<ChevronDown size={12} />{:else}<ChevronRight size={12} />{/if}
                </button>
              {/if}
            </td>
            <td>
              <a href="/snippets/{snip.id}" class="font-medium text-surface-900-100 hover:text-primary-300 transition-colors">{snip.name}</a>
              <!-- Never completed a run. The workflow editor palette files these
                   under "Unproven"; surfacing it here too is how you spot the
                   drafts worth finishing or deleting. -->
              {#if (snip.completed_run_count ?? 0) === 0}
                <span
                  class="ml-1.5 align-middle text-[10px] px-1 py-0.5 rounded border border-dashed border-surface-300-700 text-surface-500"
                  title="No step using this snippet has ever completed. It sits under “Unproven” in the workflow editor palette."
                >unproven</span>
              {/if}
              {#if snip.description}
                <div class="text-xs text-surface-500 mt-0.5 line-clamp-1">{snip.description}</div>
              {/if}
            </td>
            <td><Badge tone="neutral">{snip.type ?? '—'}</Badge></td>
            <td class="text-xs text-surface-700-300">{snip.target_mode ?? '—'}</td>
            <td class="text-xs text-surface-700-300 tabular-nums">{snip.max_parallel ?? '—'}</td>
            <td class="text-xs text-surface-700-300 tabular-nums">{snip.timeout_seconds ? snip.timeout_seconds + 's' : '—'}</td>
            <td>
              <span title={hasIdempotencyOverride
                ? `Override: ${snip.idempotency}. Handler floor: ${HANDLER_FLOOR[snip.type] ?? 'requires_compensation'}.`
                : `Inherited from handler (${snip.type}).`}>
                <Badge tone={toneFor(effIdempotency)}>{shortFor(effIdempotency)}</Badge>
              </span>
              {#if hasIdempotencyOverride}
                <span class="ml-1 text-[10px] text-surface-500" title="Per-snippet override">★</span>
              {/if}
            </td>
            <td class="text-right whitespace-nowrap space-x-1">
              <Button size="xs" variant="ghost" icon={Edit} href="/snippets/{snip.id}">Edit</Button>
              <Button size="xs" variant="ghost" icon={Play} href="/snippets/{snip.id}#test">Test</Button>
              <Button
                size="xs"
                variant="ghost"
                icon={Copy}
                disabled={duplicating !== null}
                onclick={() => duplicateSnippet(snip)}
              >
                {duplicating === snip.id ? 'Duplicating…' : 'Duplicate'}
              </Button>
              <Button size="xs" variant="danger" icon={Trash2} onclick={() => deleteSnippet(snip)}>Delete</Button>
            </td>
          </tr>
          {#if expanded && hasSchema}
            <tr>
              <td colspan="9" class="!p-0">
                <div class="bg-surface-50-950 px-4 py-3 border-t border-surface-200-800/60">
                  <div class="text-[10px] uppercase tracking-wider text-surface-500 mb-1">input_schema</div>
                  <pre class="text-xs font-mono text-surface-700-300 whitespace-pre-wrap overflow-auto max-h-64">{JSON.stringify(snip.input_schema, null, 2)}</pre>
                </div>
              </td>
            </tr>
          {/if}
        {/each}
      </tbody>
    </DataTable>
  {/if}
</div>

<Dialog bind:open={showImport} title="Import snippets" size="lg">
  <div class="space-y-4">
    <p class="text-sm text-surface-700-300">
      Pick or drop a JSON file exported from this page, or paste its contents. A bare array of
      snippets, or a single snippet object, is accepted too. Each snippet goes through the same
      checks as one created by hand.
    </p>

    <div class="flex items-center gap-2">
      <Button size="xs" variant="ghost" icon={Upload} onclick={() => importFileInput?.click()} disabled={importing}>
        Choose file…
      </Button>
      <input
        bind:this={importFileInput}
        type="file"
        accept=".json,application/json"
        class="sr-only"
        onchange={onImportFilePicked}
      />
      {#if importFileName}<span class="text-xs text-surface-500 truncate">{importFileName}</span>{/if}
    </div>

    <div
      class="relative rounded-md transition-colors {importDragOver ? 'ring-2 ring-primary-500/60 bg-primary-500/5' : ''}"
      ondragover={(e) => { e.preventDefault(); importDragOver = true; }}
      ondragleave={() => (importDragOver = false)}
      ondrop={onImportDrop}
      role="region"
      aria-label="Drop snippet file here"
    >
      <textarea
        bind:value={importText}
        oninput={() => (importFileName = '')}
        rows="10"
        spellcheck="false"
        aria-label="Snippet bundle JSON"
        placeholder={'{ "format": "flowweaver.snippets", "version": 1, "snippets": [ … ] }'}
        class="w-full font-mono text-xs rounded-md border border-surface-300-700 bg-surface-50-950 p-2"
        disabled={importing}
      ></textarea>
      {#if importDragOver}
        <div class="absolute inset-0 flex items-center justify-center text-sm text-primary-400 pointer-events-none">
          Drop file to load
        </div>
      {/if}
    </div>

    {#if importPreview}
      {#if importPreview.error}
        <Alert tone="error">{importPreview.error}</Alert>
      {:else}
        <div class="text-sm text-surface-700-300 space-y-1">
          <div>
            <span class="font-medium tabular-nums">{importPreview.snippets.length}</span>
            snippet{importPreview.snippets.length === 1 ? '' : 's'} ready to import{#if importPreview.conflicts > 0}, <span class="tabular-nums">{importPreview.conflicts}</span> with a name that already exists{/if}.
          </div>
          <ul class="text-xs text-surface-500 max-h-32 overflow-auto">
            {#each importPreview.snippets as s, i (i)}
              <li>
                {s.name} <span class="text-surface-400">· {s.type}</span>
                {#if importPreview.existing.has(s.name)}<span class="ml-1 text-warning-500">(exists)</span>{/if}
                {#if s.network_enabled}<span class="ml-1 text-warning-500">(network-enabled)</span>{/if}
              </li>
            {/each}
          </ul>
        </div>
        {#if importPreview.problems.length}
          <Alert tone="warning">
            These entries will be ignored:
            <ul class="list-disc ml-5 text-xs">
              {#each importPreview.problems as p (p)}<li>{p}</li>{/each}
            </ul>
          </Alert>
        {/if}
        {#if importPreview.conflicts > 0}
          <fieldset class="space-y-1 text-sm">
            <legend class="text-xs uppercase tracking-wider text-surface-500 mb-1">When the name already exists</legend>
            <label class="flex items-center gap-2 cursor-pointer">
              <input type="radio" bind:group={conflictPolicy} value="rename" disabled={importing} /> Import as a new snippet named “… (imported)”
            </label>
            <label class="flex items-center gap-2 cursor-pointer">
              <input type="radio" bind:group={conflictPolicy} value="skip" disabled={importing} /> Skip it and keep the existing one
            </label>
            <label class="flex items-center gap-2 cursor-pointer">
              <input type="radio" bind:group={conflictPolicy} value="overwrite" disabled={importing} /> Overwrite the existing snippet
            </label>
          </fieldset>
        {/if}
      {/if}
    {/if}

    {#if importError}
      <Alert tone="error"><pre class="whitespace-pre-wrap text-xs font-sans">{importError}</pre></Alert>
    {/if}
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showImport = false)} disabled={importing}>Cancel</Button>
    <Button
      variant="primary"
      icon={Upload}
      onclick={confirmImport}
      loading={importing}
      disabled={importing || !importPreview || !!importPreview.error || importPreview.snippets.length === 0}
    >
      Import
    </Button>
  {/snippet}
</Dialog>
