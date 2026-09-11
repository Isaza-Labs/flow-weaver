<script lang="ts">
  import { onMount } from 'svelte';
  import { devices, workflows, runs, promptSkills, jobs, type WorkflowRun } from '$lib/api/client';
  import {
    PageHeader,
    StatCard,
    Card,
    StatusBadge,
    EmptyState,
    Skeleton,
    Button,
    IconButton,
    ErrorState,
    formatRelative,
    truncate,
    FieldHint,
  } from '$lib/components/ui';
  import {
    Server, Workflow, Activity, Sparkles, ArrowRight, Inbox,
    Pencil, Check, X, ChevronUp, ChevronDown, Plus,
  } from 'lucide-svelte';
  import { prefsStore } from '$lib/stores/prefs.svelte';
  import { authStore } from '$lib/stores/auth.svelte';
  import { NAV_DESTS, destByHref, type NavDest } from '$lib/nav';

  // ── Quick access: editable dashboard shortcuts (persisted per browser) ──
  let editingLinks = $state(false);
  let addChoice = $state('');
  const isAdmin = $derived(authStore.session?.role === 'admin');
  // Stored hrefs → catalog destinations; drop unknown routes and admin-only
  // ones this role can't reach.
  const quickDests = $derived.by((): NavDest[] =>
    prefsStore.quickLinks
      .map((href) => destByHref(href))
      .filter((d): d is NavDest => !!d && (!d.adminOnly || isAdmin)),
  );
  // Catalog entries not already pinned, visible to this role.
  const addableDests = $derived.by((): NavDest[] =>
    NAV_DESTS.filter((d) => (!d.adminOnly || isAdmin) && !prefsStore.quickLinks.includes(d.href)),
  );
  function addSelected() {
    if (!addChoice) return;
    prefsStore.addQuickLink(addChoice);
    addChoice = '';
  }

  let stats = $state({ devices: 0, workflows: 0, runs: 0, skills: 0 });
  let queue = $state<Record<string, number>>({});
  let recentRuns = $state<WorkflowRun[]>([]);
  let workflowNames = $state<Record<string, string>>({});
  let loading = $state(true);
  let loadError = $state<unknown>(null);

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      const [d, w, r, s, q, recent] = await Promise.all([
        devices.list(1, 0),
        workflows.list(1, 0),
        runs.list(1, 0),
        // Prompt-skill catalog (markdown fragments injected into the chat
        // system prompt). The legacy `skills` table was a different concept
        // and always reports 0 on a fresh install. The admin-only endpoint
        // can 403 for viewers — swallow the rejection and fall back to 0
        // so the rest of the dashboard still renders.
        promptSkills.list(1, 0).catch(() => ({ total: 0 } as { total: number })),
        jobs.queueStats(),
        runs.list(8, 0),
      ]);
      stats = { devices: d.total, workflows: w.total, runs: r.total, skills: s.total };
      queue = q;
      recentRuns = recent.data;

      const uniqueWfIds = [...new Set(recentRuns.map((r) => r.workflow_id).filter(Boolean))];
      const wfMap: Record<string, string> = {};
      await Promise.all(
        uniqueWfIds.map(async (wfId) => {
          try {
            const wfRes = await workflows.get(wfId);
            wfMap[wfId] = wfRes.name;
          } catch {
            // Workflow may have been deleted; leave name out and fall back to ID in the UI.
          }
        }),
      );
      workflowNames = wfMap;
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }
</script>

<svelte:head><title>Dashboard · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-6">
  <PageHeader title="Dashboard" description="Live overview of your automation fleet" />

  <!-- Quick access — user-editable shortcuts, persisted per browser. -->
  <section aria-label="Quick access">
    <div class="flex items-center justify-between mb-3">
      <h2 class="text-sm font-semibold tracking-tight text-surface-800-200 inline-flex items-center gap-1">Quick access <FieldHint id="dashboard.quick_links" /></h2>
      <Button size="xs" variant="ghost" icon={editingLinks ? Check : Pencil} onclick={() => (editingLinks = !editingLinks)}>
        {editingLinks ? 'Done' : 'Edit'}
      </Button>
    </div>

    {#if quickDests.length === 0 && !editingLinks}
      <Card padding="sm">
        <p class="text-xs text-surface-500">
          No shortcuts pinned.
          <button type="button" class="text-primary-300 hover:text-primary-200 underline" onclick={() => (editingLinks = true)}>Add some</button>
          to jump straight to what you use most.
        </p>
      </Card>
    {:else}
      <div class="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 gap-3">
        {#each quickDests as dest (dest.href)}
          {#if editingLinks}
            <div class="flex items-center gap-2.5 px-3 h-14 rounded-lg border border-surface-200-800 bg-surface-50-950/50">
              <dest.icon size={18} class="text-primary-300 shrink-0" />
              <span class="text-sm font-medium text-surface-900-100 truncate">{dest.label}</span>
              <div class="ml-auto flex items-center gap-0.5 shrink-0">
                <IconButton icon={ChevronUp} label="Move {dest.label} up" onclick={() => prefsStore.moveQuickLink(dest.href, -1)} />
                <IconButton icon={ChevronDown} label="Move {dest.label} down" onclick={() => prefsStore.moveQuickLink(dest.href, 1)} />
                <IconButton icon={X} label="Remove {dest.label}" variant="danger" onclick={() => prefsStore.removeQuickLink(dest.href)} />
              </div>
            </div>
          {:else}
            <a
              href={dest.href}
              class="flex items-center gap-2.5 px-3 h-14 rounded-lg border border-surface-200-800 bg-surface-50-950/50 hover:border-primary-500/40 hover:bg-surface-200-800/30 transition-colors group"
            >
              <dest.icon size={18} class="text-primary-300 shrink-0" />
              <span class="text-sm font-medium text-surface-900-100 truncate">{dest.label}</span>
              <ArrowRight size={14} class="ml-auto text-surface-500 group-hover:text-surface-700-300 transition-colors shrink-0" />
            </a>
          {/if}
        {/each}
      </div>

      {#if editingLinks}
        <div class="mt-3 flex items-center gap-2 flex-wrap">
          <FieldHint id="dashboard.add_shortcut" />
          <select
            bind:value={addChoice}
            class="h-8 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm px-2 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30"
          >
            <option value="">Add a shortcut…</option>
            {#each addableDests as d (d.href)}
              <option value={d.href}>{d.group} · {d.label}</option>
            {/each}
          </select>
          <Button size="xs" variant="secondary" icon={Plus} disabled={!addChoice} onclick={addSelected}>Add</Button>
          {#if addableDests.length === 0}
            <span class="text-xs text-surface-500">All destinations pinned.</span>
          {/if}
        </div>
      {/if}
    {/if}
  </section>

  {#if loading}
    <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
      {#each Array(4) as _, i (i)}<Skeleton variant="stat" />{/each}
    </div>
    <Skeleton variant="table" count={5} columns={4} />
  {:else if loadError}
    <Card padding="none">
      <ErrorState error={loadError} onRetry={load} />
    </Card>
  {:else}
    <!-- Stats -->
    <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
      <StatCard label="Devices" value={stats.devices} icon={Server} href="/devices" tone="primary" />
      <StatCard label="Workflows" value={stats.workflows} icon={Workflow} href="/workflows" tone="success" />
      <StatCard label="Total runs" value={stats.runs} icon={Activity} href="/runs" />
      <StatCard label="AI skills" value={stats.skills} icon={Sparkles} href="/ai/skills" tone="warning" />
    </div>

    <!-- Queue -->
    {#if Object.keys(queue).length > 0}
      <Card>
        <div class="flex items-center justify-between mb-3">
          <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">Job queue <FieldHint id="dashboard.job_queue" /></h3>
        </div>
        <div class="flex flex-wrap items-center gap-x-6 gap-y-2">
          {#each Object.entries(queue) as [status, count]}
            <div class="flex items-center gap-2">
              <StatusBadge {status} showDot />
              <span class="font-mono text-sm font-medium tabular-nums text-surface-800-200">{count}</span>
            </div>
          {/each}
        </div>
      </Card>
    {/if}

    <!-- Recent runs -->
    <section>
      <div class="flex items-center justify-between mb-3">
        <h2 class="text-sm font-semibold tracking-tight text-surface-800-200 inline-flex items-center gap-1">Recent runs <FieldHint id="dashboard.recent_runs" /></h2>
        <a href="/runs" class="inline-flex items-center gap-1 text-xs text-primary-300 hover:text-primary-200 transition-colors">
          View all <ArrowRight size={12} />
        </a>
      </div>

      {#if recentRuns.length === 0}
        <Card padding="none">
          <EmptyState icon={Inbox} title="No runs yet" description="Workflow executions will appear here once you run one.">
            {#snippet actions()}
              <Button variant="primary" href="/workflows">Run a workflow</Button>
            {/snippet}
          </EmptyState>
        </Card>
      {:else}
        <Card padding="none">
          <ul class="divide-y divide-surface-200-800/60">
            {#each recentRuns as run (run.id)}
              <li>
                <a
                  href="/runs/{run.id}"
                  class="flex items-center justify-between gap-3 px-4 py-3 hover:bg-surface-200-800/40 transition-colors group"
                >
                  <div class="min-w-0 flex-1">
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="text-sm font-medium text-surface-900-100 truncate">
                        {workflowNames[run.workflow_id] ?? truncate(run.id, 8)}
                      </span>
                      <span class="text-[11px] font-mono text-surface-500">{truncate(run.id, 8)}</span>
                      {#if run.trigger}
                        <span class="text-[11px] text-surface-500">· {run.trigger}</span>
                      {/if}
                    </div>
                  </div>
                  <div class="flex items-center gap-3 shrink-0">
                    <span class="text-xs text-surface-500 hidden sm:inline">{formatRelative(run.created_at)}</span>
                    <StatusBadge status={run.status} />
                    <ArrowRight size={14} class="text-surface-500 group-hover:text-surface-700-300 transition-colors" />
                  </div>
                </a>
              </li>
            {/each}
          </ul>
        </Card>
      {/if}
    </section>
  {/if}
</div>
