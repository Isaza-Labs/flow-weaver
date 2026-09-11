<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { snippets, errorMessage, type Snippet } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, Badge, EmptyState,
    Alert, Spinner, SearchInput, confirm,
    FieldHint,
  } from '$lib/components/ui';
  import { goto } from '$app/navigation';
  import { Settings2, Plus, Edit, Play, Copy, Trash2, ChevronDown, ChevronRight } from 'lucide-svelte';
  import {
    HANDLER_FLOOR, effectiveIdempotency as computeEffective,
    idempotencyTone as toneFor, idempotencyShort as shortFor,
    type IdempotencyKind,
  } from '$lib/workflow/idempotency';

  let snippetList = $state<Snippet[]>([]);
  let loading = $state(true);
  let error = $state('');
  let expandedSchemas = $state(new Set<string>());
  let searchText = $state('');
  // S13.6 follow-up: filter pill that narrows the list to snippets
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
          <tr>
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
              <td colspan="8" class="!p-0">
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
