<script lang="ts">
  // Application trace timeline. Every critical handler (AI chat, tool
  // dispatch, auth login, workflow enqueue, …) writes a row here via
  // ITraceLogger. The page is geared for live debugging:
  //   - Filters narrow to one module or one user in seconds.
  //   - Live mode re-fetches every 5s so new rows stream in.
  //   - Expanding a row shows the raw metadata JSON — the single most
  //     useful thing when a chat is "stuck" and you need to know why.
  //
  // Works together with `docker logs`: each row carries a request_id that
  // shows up in the Serilog output, so you can pivot from a trace row to
  // the structured log lines that produced it.

  import { onMount, onDestroy } from 'svelte';
  import { copyText } from '$lib/utils/clipboard';
  import {
    traces, users,
    type TraceEvent, type User,
    errorMessage,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Input, Select, Alert, Spinner,
    EmptyState, StatusBadge, formatDateTime, truncate, toast,
  } from '$lib/components/ui';
  import {
    Activity, ChevronDown, ChevronRight, RefreshCw, Pause, Play,
    Filter, Copy,
  } from 'lucide-svelte';

  let rows = $state<TraceEvent[]>([]);
  let userMap = $state<Map<string, User>>(new Map());
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  // Filters.
  let category = $state('');
  let action = $state('');
  let status = $state('');
  let userId = $state('');
  let requestId = $state('');
  let from = $state('');
  let to = $state('');

  // Live mode. Off by default so a stable filter doesn't keep jumping.
  const REFRESH_MS = 5_000;
  let live = $state(false);
  let refreshInterval: ReturnType<typeof setInterval> | null = null;

  let expanded = $state<Set<string>>(new Set());

  onMount(async () => {
    try {
      const list = await users.list();
      userMap = new Map(list.map((u) => [u.user_id, u]));
    } catch {
      // Non-fatal — usernames just fall back to truncated uuid.
    }
    await load();
  });

  onDestroy(stopPolling);

  async function load(silent = false) {
    if (!silent) loading = true;
    loadError = null;
    try {
      rows = await traces.list({
        category: category || undefined,
        action: action || undefined,
        status: status || undefined,
        user_id: userId || undefined,
        request_id: requestId || undefined,
        from: dayStart(from),
        to: dayEnd(to),
        limit: 200,
      });
    } catch (e) {
      loadError = errorMessage(e);
      if (!silent) toast.fromError(e, "Couldn't load traces");
    } finally {
      if (!silent) loading = false;
    }
  }

  function dayStart(v: string): string | undefined {
    return v ? new Date(`${v}T00:00:00Z`).toISOString() : undefined;
  }
  function dayEnd(v: string): string | undefined {
    return v ? new Date(`${v}T23:59:59Z`).toISOString() : undefined;
  }

  function toggleLive() {
    live = !live;
    if (live) startPolling();
    else stopPolling();
  }

  function startPolling() {
    if (refreshInterval) return;
    refreshInterval = setInterval(() => load(true), REFRESH_MS);
  }
  function stopPolling() {
    if (refreshInterval) { clearInterval(refreshInterval); refreshInterval = null; }
  }

  async function applyFilters() {
    await load();
  }

  async function reset() {
    category = '';
    action = '';
    status = '';
    userId = '';
    requestId = '';
    from = '';
    to = '';
    await load();
  }

  function toggleRow(id: string) {
    const next = new Set(expanded);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expanded = next;
  }

  function resolveUser(id: string | null): string {
    if (!id) return '—';
    return userMap.get(id)?.username ?? truncate(id, 8);
  }

  function statusTone(s: string): 'success' | 'failed' | 'info' | 'pending' {
    if (s === 'completed') return 'success';
    if (s === 'failed' || s === 'timeout') return 'failed';
    if (s === 'started') return 'pending';
    return 'info';
  }

  function categoryColor(c: string): string {
    switch (c) {
      case 'ai': return 'bg-primary-500/15 text-primary-300 ring-primary-500/30';
      case 'auth': return 'bg-warning-500/15 text-warning-300 ring-warning-500/30';
      case 'tool': return 'bg-success-500/15 text-success-300 ring-success-500/30';
      case 'workflow': return 'bg-info-500/15 text-info-300 ring-info-500/30';
      case 'admin': return 'bg-error-500/15 text-error-300 ring-error-500/30';
      default: return 'bg-surface-200-800 text-surface-700-300 ring-surface-300-700';
    }
  }

  async function copyRequestId(id: string | null) {
    if (!id) return;
    if (await copyText(id)) toast.success('Request id copied', { description: id });
    else toast.info('Copy failed — id: ' + id);
  }

  const KNOWN_CATEGORIES = ['ai', 'auth', 'tool', 'workflow', 'admin', 'worker', 'system'];
  const KNOWN_STATUSES = ['started', 'completed', 'failed', 'timeout'];
</script>

<svelte:head><title>Traces · Admin · Flow Weaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Traces"
    description="Every critical action emits one row here. Duration is recorded on close, so an `started` row older than a few seconds means the handler is still running or crashed silently."
    breadcrumbs={[{ label: 'Admin', href: '/admin' }, { label: 'Traces' }]}
  >
    {#snippet actions()}
      <Button
        size="sm"
        variant="ghost"
        icon={live ? Pause : Play}
        onclick={toggleLive}
        title={live ? 'Pause live refresh' : 'Resume live refresh'}
      >
        {live ? 'Live' : 'Paused'}
      </Button>
      <Button variant="ghost" icon={RefreshCw} onclick={() => load()}>Refresh</Button>
    {/snippet}
  </PageHeader>

  <Card>
    <div class="p-4 grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-3">
      <Select label="Category" help="traces.category" bind:value={category}>
        <option value="">All</option>
        {#each KNOWN_CATEGORIES as c (c)}<option value={c}>{c}</option>{/each}
      </Select>
      <Select label="Status" help="traces.status" bind:value={status}>
        <option value="">All</option>
        {#each KNOWN_STATUSES as s (s)}<option value={s}>{s}</option>{/each}
      </Select>
      <Input label="Action" help="traces.action" bind:value={action} placeholder="ai.chat.stream" />
      <Input label="User id" help="audit.user_id" bind:value={userId} placeholder="uuid of actor" />
      <Input label="Request id" help="traces.request_id" bind:value={requestId} placeholder="correlation id" />
      <div class="grid grid-cols-2 gap-2">
        <Input label="From" help="audit.date_range" type="date" bind:value={from} />
        <Input label="To" help="audit.date_range" type="date" bind:value={to} />
      </div>
      <div class="flex items-end gap-2 md:col-span-2">
        <Button variant="primary" onclick={applyFilters}>Apply</Button>
        <Button variant="ghost" onclick={reset}>Reset</Button>
        <span class="text-[11px] text-surface-500 inline-flex items-center gap-1">
          <Filter size={10} /> Filters AND together.
        </span>
      </div>
    </div>
  </Card>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if rows.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Activity}
        title="No traces match"
        description="Widen the date range, or trigger a chat / workflow run to generate some."
      />
    </Card>
  {:else}
    <Card padding="none">
      <table class="w-full text-sm">
        <thead class="border-b border-surface-200-800">
          <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
            <th class="px-4 py-2 w-8"></th>
            <th class="px-4 py-2">When</th>
            <th class="px-4 py-2">Category</th>
            <th class="px-4 py-2">Action</th>
            <th class="px-4 py-2">Status</th>
            <th class="px-4 py-2">Duration</th>
            <th class="px-4 py-2">User</th>
            <th class="px-4 py-2">Request</th>
          </tr>
        </thead>
        <tbody>
          {#each rows as r (r.trace_event_id)}
            {@const open = expanded.has(r.trace_event_id)}
            <tr class="border-b border-surface-200-800/50 hover:bg-surface-100-900/50 cursor-pointer"
                onclick={() => toggleRow(r.trace_event_id)}>
              <td class="px-4 py-2 align-top text-surface-500">
                {#if open}<ChevronDown size={12} />{:else}<ChevronRight size={12} />{/if}
              </td>
              <td class="px-4 py-2 text-surface-700-300 font-mono tabular-nums whitespace-nowrap">{formatDateTime(r.at)}</td>
              <td class="px-4 py-2">
                <span class="inline-flex items-center rounded px-1.5 py-0.5 text-[11px] ring-1 {categoryColor(r.category)}">
                  {r.category}
                </span>
              </td>
              <td class="px-4 py-2 font-mono text-[12px] text-surface-900-100">{r.action}</td>
              <td class="px-4 py-2"><StatusBadge status={statusTone(r.status)} label={r.status} showDot={false} /></td>
              <td class="px-4 py-2 text-surface-700-300 font-mono tabular-nums">
                {r.duration_ms !== null ? `${r.duration_ms} ms` : '—'}
              </td>
              <td class="px-4 py-2 text-surface-700-300">{resolveUser(r.user_id)}</td>
              <td class="px-4 py-2 text-[11px] font-mono text-surface-500">
                {#if r.request_id}
                  <span
                    role="button"
                    tabindex="0"
                    class="cursor-pointer hover:text-primary-300 inline-flex items-center gap-1"
                    title="Copy full request id"
                    onclick={(e) => { e.stopPropagation(); copyRequestId(r.request_id); }}
                    onkeydown={(e) => { if (e.key === 'Enter') { e.stopPropagation(); copyRequestId(r.request_id); } }}
                  >
                    {truncate(r.request_id, 8)} <Copy size={10} />
                  </span>
                {:else}
                  —
                {/if}
              </td>
            </tr>
            {#if open}
              <tr class="bg-surface-50-950 border-b border-surface-200-800/50">
                <td colspan="8" class="px-4 py-3 space-y-2 text-xs">
                  {#if r.error_message}
                    <div>
                      <div class="text-[11px] font-semibold uppercase tracking-wide text-error-300 mb-1">Error</div>
                      <pre class="bg-error-500/10 text-error-300 rounded p-2 overflow-auto max-h-32 text-[11px] whitespace-pre-wrap">{r.error_message}</pre>
                    </div>
                  {/if}
                  <div>
                    <div class="text-[11px] font-semibold uppercase tracking-wide text-surface-500 mb-1">Metadata</div>
                    <pre class="bg-surface-100-900 rounded p-2 text-[11px] overflow-auto max-h-56">{JSON.stringify(r.metadata ?? {}, null, 2)}</pre>
                  </div>
                  {#if r.request_id}
                    <div class="text-[11px] text-surface-500">
                      Grep the backend logs with this request_id to get every log line emitted during the same HTTP request:
                      <code class="text-surface-700-300 font-mono ml-1">{r.request_id}</code>
                    </div>
                  {/if}
                </td>
              </tr>
            {/if}
          {/each}
        </tbody>
      </table>
    </Card>
    <div class="text-[11px] text-surface-500">
      {rows.length} trace{rows.length === 1 ? '' : 's'} shown · {live ? `auto-refresh every ${REFRESH_MS / 1000}s` : 'refresh paused'}
    </div>
  {/if}
</div>
