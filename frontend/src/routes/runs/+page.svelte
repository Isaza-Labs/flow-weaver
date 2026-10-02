<script lang="ts">
  import { confirm, toast } from '$lib/components/ui';
  import { onDestroy, onMount } from 'svelte';
  import { runs, workflows, errorMessage, type WorkflowRun } from '$lib/api/client';
  import { authStore } from '$lib/stores/auth.svelte';
  import {
    PageHeader, Card, DataTable, Button, IconButton, EmptyState, StatusBadge,
    Alert, Skeleton, SortableTh, SearchInput, Select, Pagination,
    formatDateTime, formatDuration, truncate,
  } from '$lib/components/ui';
  import { Activity, RefreshCw, ExternalLink, Trash2, Wifi, WifiOff } from 'lucide-svelte';
  import { readUrlInt, readUrlParam, setUrlParams } from '$lib/utils/urlState';
  import { prefsStore } from '$lib/stores/prefs.svelte';
  import { browser } from '$app/environment';

  let runList = $state<WorkflowRun[]>([]);
  let workflowNames = $state<Record<string, string>>({});
  let total = $state(0);
  let offset = $state(readUrlInt('offset', 0));
  let loading = $state(true);
  let error = $state('');
  let deleting = $state<Record<string, boolean>>({});
  let deletingAll = $state(false);
  let sortKey = $state<string | null>(readUrlParam('sort'));
  let sortDir = $state<'asc' | 'desc'>(readUrlParam('dir') === 'asc' ? 'asc' : 'desc');
  let statusFilter = $state(readUrlParam('status') ?? '');
  let searchQuery = $state(readUrlParam('q') ?? '');
  let live = $state(readUrlParam('live') !== '0');
  let pollTimer: ReturnType<typeof setInterval> | null = null;
  const pageSize = 50;

  const isAdmin = $derived(authStore.session?.role === 'admin');

  onMount(() => {
    loadRuns();
    startPolling();
  });
  onDestroy(() => stopPolling());

  // Until the backend exposes a list-stream WS, we approximate live updates
  // with a 5-second background refresh of the runs list. Each tick only
  // re-fetches if the tab is visible to avoid burning quota in the
  // background.
  function startPolling() {
    if (!browser || pollTimer || !live) return;
    pollTimer = setInterval(() => {
      if (document.visibilityState === 'visible') void loadRuns({ silent: true });
    }, 5000);
  }
  function stopPolling() {
    if (pollTimer) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  }
  function toggleLive() {
    live = !live;
    syncUrl();
    if (live) startPolling();
    else stopPolling();
  }

  function syncUrl() {
    setUrlParams({
      sort: sortKey || undefined,
      dir: sortKey ? sortDir : undefined,
      live: live ? undefined : '0',
      status: statusFilter || undefined,
      q: searchQuery.trim() || undefined,
      offset: offset || undefined,
    });
  }

  async function loadRuns(opts: { silent?: boolean; requestOffset?: number } = {}) {
    const requestOffset = opts.requestOffset ?? offset;
    if (!opts.silent) {
      loading = true;
      error = '';
    }
    try {
      const res = await runs.list(pageSize, requestOffset);
      // Page past the end (e.g. rows were deleted) — snap back to the last
      // populated page so the user never lands on an empty list with a
      // non-zero total.
      const lastPageOffset = res.total > 0 ? Math.max(0, Math.floor((res.total - 1) / pageSize) * pageSize) : 0;
      if (requestOffset > 0 && res.data.length === 0 && res.total > 0 && lastPageOffset !== requestOffset) {
        offset = lastPageOffset;
        const retry = await runs.list(pageSize, lastPageOffset);
        runList = retry.data;
        total = retry.total;
      } else {
        offset = requestOffset;
        runList = res.data;
        total = res.total;
      }
      if (!opts.silent) syncUrl();
      const uniqueWfIds = [...new Set(runList.map((r) => r.workflow_id).filter(Boolean))];
      const missing = uniqueWfIds.filter((id) => !(id in workflowNames));
      if (missing.length > 0) {
        const next = { ...workflowNames };
        await Promise.all(
          missing.map(async (wfId) => {
            try {
              const wfRes = await workflows.get(wfId);
              next[wfId] = wfRes.name;
            } catch {
              // Workflow may have been deleted — keep falling back to id in UI.
            }
          }),
        );
        workflowNames = next;
      }
    } catch (e) {
      if (!opts.silent) {
        error = errorMessage(e);
        toast.fromError(e, 'Couldn’t load runs');
      }
    } finally {
      if (!opts.silent) loading = false;
    }
  }

  // Status + workflow filters narrow the current server page client-side
  // (the /run endpoint only paginates by limit/offset). Pagination + total
  // still reflect the full server set; these filters refine what's visible.
  const filteredRuns = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    return runList.filter((r) => {
      if (statusFilter && r.status !== statusFilter) return false;
      if (q) {
        const name = (workflowNames[r.workflow_id] ?? '').toLowerCase();
        if (!name.includes(q) && !r.workflow_id.toLowerCase().includes(q)) return false;
      }
      return true;
    });
  });

  const sortedRuns = $derived.by(() => {
    if (!sortKey) return filteredRuns;
    const key = sortKey;
    const dir = sortDir === 'asc' ? 1 : -1;
    return [...filteredRuns].sort((a, b) => {
      const av = (a as unknown as Record<string, unknown>)[key];
      const bv = (b as unknown as Record<string, unknown>)[key];
      if (av == null && bv == null) return 0;
      if (av == null) return 1;
      if (bv == null) return -1;
      return String(av).localeCompare(String(bv)) * dir;
    });
  });

  function onSort(column: string) {
    if (sortKey === column) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    else { sortKey = column; sortDir = 'asc'; }
    syncUrl();
  }

  function onStatusFilter() {
    syncUrl();
  }
  function onSearch(v: string) {
    searchQuery = v;
    syncUrl();
  }

  async function goToPrev() { if (offset > 0) await loadRuns({ requestOffset: Math.max(0, offset - pageSize) }); }
  async function goToNext() { if (offset + runList.length < total) await loadRuns({ requestOffset: offset + pageSize }); }

  async function deleteRun(run: WorkflowRun) {
    if (!isAdmin) return;
    const wfName = workflowNames[run.workflow_id] ?? truncate(run.workflow_id, 8);
    const ok = await confirm({
      title: 'Delete run',
      message: `Run ${truncate(run.id, 8)} for "${wfName}" will be removed from history. Its step logs are dropped as well.`,
      confirmLabel: 'Delete',
      cancelLabel: 'Cancel',
      tone: 'danger',
    });
    if (!ok) return;
    deleting = { ...deleting, [run.id]: true };
    try {
      await runs.delete(run.id);
      toast.success('Run deleted');
      await loadRuns({ silent: true });
    } catch (e) {
      toast.fromError(e, 'Couldn’t delete run');
    } finally {
      deleting = { ...deleting, [run.id]: false };
    }
  }

  async function deleteAllRuns() {
    if (!isAdmin) return;
    if (total === 0) return;
    const ok = await confirm({
      title: 'Delete all runs',
      message: `Every run will be permanently removed, along with its step history. This cannot be undone.`,
      confirmLabel: 'Delete all',
      cancelLabel: 'Cancel',
      tone: 'danger',
    });
    if (!ok) return;
    deletingAll = true;
    try {
      const res = await runs.deleteAll();
      offset = 0;
      toast.success(`Deleted ${res.deleted} run${res.deleted === 1 ? '' : 's'}`);
      await loadRuns({ silent: true });
    } catch (e) {
      toast.fromError(e, 'Couldn’t delete runs');
    } finally {
      deletingAll = false;
    }
  }
</script>

<svelte:head><title>Runs · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader title="Runs" description="History of every workflow execution, live and past.">
    {#snippet actions()}
      <Button
        variant={live ? 'secondary' : 'ghost'}
        icon={live ? Wifi : WifiOff}
        onclick={toggleLive}
        title={live ? 'Live updates on — click to pause' : 'Live updates paused — click to resume'}
      >
        {live ? 'Live' : 'Paused'}
      </Button>
      {#if isAdmin && total > 0}
        <Button
          variant="danger"
          icon={Trash2}
          onclick={deleteAllRuns}
          loading={deletingAll}
          title="Delete every run (admin only)"
        >
          Delete all
        </Button>
      {/if}
      <Button icon={RefreshCw} onclick={() => loadRuns()} loading={loading}>Refresh</Button>
    {/snippet}
  </PageHeader>

  {#if error}
    <Alert tone="error" dismissible onDismiss={() => (error = '')}>{error}</Alert>
  {/if}

  <div class="flex items-center justify-between gap-3 flex-wrap">
    <div class="w-40">
      <Select bind:value={statusFilter} onchange={onStatusFilter} label="Status" help="runs.status_filter">
        <option value="">All statuses</option>
        <option value="pending">Pending</option>
        <option value="running">Running</option>
        <option value="completed">Completed</option>
        <option value="failed">Failed</option>
        <option value="cancelled">Cancelled</option>
      </Select>
    </div>
    <SearchInput value={searchQuery} placeholder="Filter by workflow name or id…" onInput={onSearch} />
  </div>

  {#if loading}
    <Skeleton variant="table" count={6} columns={6} />
  {:else if total === 0}
    <Card padding="none">
      <EmptyState icon={Activity} title="No runs yet" description="Run a workflow to see executions appear here.">
        {#snippet actions()}
          <Button variant="primary" href="/workflows">Browse workflows</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else if sortedRuns.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Activity}
        title="No runs match your filters"
        description="Try a different status or clear the search to see this page's runs."
      />
    </Card>
  {:else}
    <DataTable density={prefsStore.density} caption="Workflow run history">
      <thead>
        <tr>
          <th>Run</th>
          <SortableTh column="workflow_id" {sortKey} {sortDir} onSort={onSort}>Workflow</SortableTh>
          <SortableTh column="status" {sortKey} {sortDir} onSort={onSort}>Status</SortableTh>
          <th>Trigger</th>
          <SortableTh column="started_at" {sortKey} {sortDir} onSort={onSort}>Started</SortableTh>
          <th>Duration</th>
          <th class="!text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each sortedRuns as run (run.id)}
          <tr>
            <td>
              <a href="/runs/{run.id}" class="font-mono text-xs text-primary-300 hover:text-primary-200">{truncate(run.id, 8)}</a>
            </td>
            <td class="text-surface-700-300 truncate max-w-xs">
              {workflowNames[run.workflow_id] ?? truncate(run.workflow_id, 8)}
            </td>
            <td><StatusBadge status={run.status} /></td>
            <td class="text-surface-700-300 text-xs">{run.trigger || '—'}</td>
            <td class="text-surface-600-400 text-xs">{formatDateTime(run.started_at)}</td>
            <td class="text-surface-600-400 text-xs tabular-nums">{formatDuration(run.started_at, run.completed_at)}</td>
            <td class="text-right">
              <div class="inline-flex items-center gap-1">
                <Button size="xs" variant="ghost" href="/runs/{run.id}/monitor" iconRight={ExternalLink}>Monitor</Button>
                {#if isAdmin}
                  <IconButton
                    icon={Trash2}
                    label="Delete run"
                    variant="danger"
                    size="xs"
                    disabled={deleting[run.id]}
                    onclick={() => deleteRun(run)}
                  />
                {/if}
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
    <div class="flex items-center justify-between gap-3 flex-wrap">
      <Pagination {offset} {pageSize} {total} label="run" onPrev={goToPrev} onNext={goToNext} />
      {#if live}<span class="text-xs text-surface-500">Auto-refreshing</span>{/if}
    </div>
  {/if}
</div>
