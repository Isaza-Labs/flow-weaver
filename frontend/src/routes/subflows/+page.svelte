<script lang="ts">
  import { onMount } from 'svelte';
  import { workflows, type Workflow } from '$lib/api/client';
  import { PageHeader, Card, Spinner, EmptyState, Button, Select, formatDateTime, toast, confirm } from '$lib/components/ui';
  import { GitBranch, Plus, RefreshCw, Trash2 } from 'lucide-svelte';

  let rows = $state<Workflow[]>([]);
  let loading = $state(true);
  let environment = $state<'' | 'draft' | 'qa' | 'production'>('');

  onMount(load);

  async function load() {
    loading = true;
    try {
      rows = await workflows.subflows(environment || undefined);
    } catch (e) {
      toast.fromError(e, 'Failed to load subflows');
    } finally {
      loading = false;
    }
  }

  async function deleteSubflow(wf: Workflow) {
    // Deletes the workflow itself, not just its subflow tag — callers that
    // invoke it via a subflow node reference it by id, so they will break.
    // To merely drop it from this list, untick "Reusable as subflow" in the
    // editor instead.
    if (!(await confirm({
      title: `Delete subflow "${wf.name}"?`,
      message: 'This deletes the workflow itself, not just its subflow tag. Any workflow that calls it via a subflow node will break. This cannot be undone.',
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await workflows.delete(wf.id);
      await load();
      toast.success(`Deleted "${wf.name}"`);
    } catch (e) {
      toast.fromError(e, 'Couldn’t delete subflow');
    }
  }
</script>

<svelte:head><title>Subflows · Flow Weaver</title></svelte:head>

<div class="p-6 max-w-6xl mx-auto space-y-5">
  <PageHeader
    title="Reusable subflows"
    description="Workflows tagged as subflows can be invoked by other workflows via a subflow node. Toggle the tag from the workflow's metadata pane."
  >
    {#snippet actions()}
      <Button href="/workflows/new" variant="primary" icon={Plus}>New workflow</Button>
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
    {/snippet}
  </PageHeader>

  <div class="flex items-end gap-3">
    <Select label="Environment" help="subflows.environment_filter" bind:value={environment}>
      <option value="">All</option>
      <option value="draft">Draft</option>
      <option value="qa">QA</option>
      <option value="production">Production</option>
    </Select>
    <Button variant="ghost" onclick={load}>Apply</Button>
  </div>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading subflows…" /></div>
  {:else if rows.length === 0}
    <Card>
      <EmptyState
        icon={GitBranch}
        title="No subflows yet"
        description="Open any workflow → metadata → tick 'Reusable as subflow'. It will appear here and become selectable from a subflow node in any other workflow."
      />
    </Card>
  {:else}
    <Card padding="none">
      <table class="w-full text-sm">
        <thead class="border-b border-surface-200-800">
          <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
            <th class="px-4 py-2">Name</th>
            <th class="px-4 py-2">Environment</th>
            <th class="px-4 py-2">Version</th>
            <th class="px-4 py-2">Updated</th>
            <th class="px-4 py-2"></th>
          </tr>
        </thead>
        <tbody>
          {#each rows as wf (wf.id)}
            <tr class="border-b border-surface-200-800/50 hover:bg-surface-100-900/50">
              <td class="px-4 py-2 font-medium text-surface-900-100">{wf.name}</td>
              <td class="px-4 py-2 text-surface-500">{wf.environment}</td>
              <td class="px-4 py-2 font-mono tabular-nums">v{wf.version}</td>
              <td class="px-4 py-2 text-surface-500 tabular-nums">{formatDateTime(wf.updated_at)}</td>
              <td class="px-4 py-2 text-right whitespace-nowrap">
                <Button size="sm" variant="ghost" href={`/workflows/${wf.id}`}>Open</Button>
                <Button size="sm" variant="ghost" icon={Trash2} onclick={() => deleteSubflow(wf)}>Delete</Button>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </Card>
  {/if}
</div>
