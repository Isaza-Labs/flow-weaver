<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import {
    jobs, adminMetrics,
    type RunMetrics, type AuthMetrics,
  } from '$lib/api/client';
  import {
    Card, PageHeader, StatCard, StackedBarChart,
    Spinner, ErrorState, Select, Button,
    FieldHint,
  } from '$lib/components/ui';
  import {
    Activity, Clock, CheckCircle2, AlertTriangle, Users, ShieldAlert,
    ArrowUpRight, BarChart3, LogIn, TrendingDown, Pause, Play,
    Eye, FileText, Settings, Gauge, Palette,
  } from 'lucide-svelte';

  let queue = $state<Record<string, number> | null>(null);
  let runMetrics = $state<RunMetrics | null>(null);
  let authMetricsData = $state<AuthMetrics | null>(null);
  let windowDays = $state(7);
  let loading = $state(true);
  let loadError = $state<unknown>(null);

  // Auto-refresh: 30 s polling. WebSocket would require a dedicated backend
  // stream; polling reuses the existing REST endpoints and is plenty
  // responsive for a dashboard that aggregates whole-day buckets. The user
  // can pause it to avoid flicker while reading the charts.
  const REFRESH_MS = 30_000;
  let autoRefresh = $state(true);
  let refreshInterval: ReturnType<typeof setInterval> | null = null;
  let lastRefreshed = $state<Date | null>(null);

  // Status → color mapping for the runs chart. We draw bottom → top in this
  // order so "completed" sits at the base of each bar and failures stack on
  // top — the visual reads as "good foundation, failures stick out".
  const RUN_SERIES = ['completed', 'failed', 'running', 'pending'] as const;
  const RUN_COLORS: Record<string, string> = {
    completed: 'rgb(16 185 129)',   // success-500
    failed: 'rgb(239 68 68)',       // error-500
    failure: 'rgb(239 68 68)',      // alias
    running: 'rgb(59 130 246)',     // primary-500
    pending: 'rgb(245 158 11)',     // warning-500
  };

  // Auth event kinds we render. Order matters: login_success at the bottom,
  // failures/lockouts on top so operators can spot spikes at a glance.
  const AUTH_SERIES = ['login_success', 'login_failure', 'lockout'] as const;
  const AUTH_COLORS: Record<string, string> = {
    login_success: 'rgb(16 185 129)',
    login_failure: 'rgb(239 68 68)',
    lockout: 'rgb(168 85 247)',     // purple — distinct from failure red
    logout: 'rgb(148 163 184)',
    password_change: 'rgb(59 130 246)',
    refresh: 'rgb(100 116 139)',
    token_revoked: 'rgb(245 158 11)',
  };

  const runSeries = $derived(
    (runMetrics?.series ?? []).map((b) => ({
      label: shortDay(b.date),
      counts: b.counts,
    })),
  );

  const authSeries = $derived(
    (authMetricsData?.series ?? []).map((b) => ({
      label: shortDay(b.date),
      counts: b.counts,
    })),
  );

  // Aggregate totals for the sub-headers — gives the eye a number to anchor
  // the chart against instead of forcing summation by reading bars.
  const runTotals = $derived(aggregate(runSeries.map((s) => s.counts)));
  const authTotals = $derived(aggregate(authSeries.map((s) => s.counts)));

  onMount(() => {
    load();
    startPolling();
  });

  onDestroy(stopPolling);

  // `silent=true` on scheduled refreshes so the page doesn't flash its
  // loading spinner every 30 s — we only show the spinner on the initial
  // mount or when the user explicitly triggers a reload.
  async function load(silent = false) {
    if (!silent) loading = true;
    loadError = null;
    try {
      const [q, r, a] = await Promise.all([
        jobs.queueStats(),
        adminMetrics.runs(windowDays),
        adminMetrics.auth(windowDays),
      ]);
      queue = q;
      runMetrics = r;
      authMetricsData = a;
      lastRefreshed = new Date();
    } catch (e) {
      loadError = e;
    } finally {
      if (!silent) loading = false;
    }
  }

  function startPolling() {
    if (refreshInterval) return;
    if (!autoRefresh) return;
    refreshInterval = setInterval(() => load(true), REFRESH_MS);
  }

  function stopPolling() {
    if (refreshInterval) {
      clearInterval(refreshInterval);
      refreshInterval = null;
    }
  }

  function toggleAutoRefresh() {
    autoRefresh = !autoRefresh;
    if (autoRefresh) {
      startPolling();
      load(true);
    } else {
      stopPolling();
    }
  }

  async function onWindowChange(next: number) {
    windowDays = next;
    await load();
  }

  // "Apr 17" — compact enough that 7–30 day windows fit on a small bar.
  function shortDay(iso: string): string {
    const d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  function aggregate(buckets: Record<string, number>[]): Record<string, number> {
    const out: Record<string, number> = {};
    for (const b of buckets) {
      for (const [k, v] of Object.entries(b)) {
        out[k] = (out[k] ?? 0) + v;
      }
    }
    return out;
  }

  function runColor(key: string): string {
    return RUN_COLORS[key] ?? 'rgb(100 116 139)';
  }

  function authColor(key: string): string {
    return AUTH_COLORS[key] ?? 'rgb(100 116 139)';
  }
</script>

<svelte:head>
  <title>Admin · FlowWeaver</title>
</svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-6">
  <PageHeader
    title="Admin"
    description="Health signals: queue depth, recent runs, sign-in activity, and the audit trail."
  >
    {#snippet actions()}
      {#if lastRefreshed}
        <span class="text-[11px] text-surface-500 tabular-nums hidden md:inline">
          Updated {lastRefreshed.toLocaleTimeString()}
        </span>
      {/if}
      <Button
        size="sm"
        variant="ghost"
        icon={autoRefresh ? Pause : Play}
        onclick={toggleAutoRefresh}
        title={autoRefresh ? 'Pause auto-refresh' : 'Resume auto-refresh'}
      >
        {autoRefresh ? 'Live' : 'Paused'}
      </Button>
      <div class="w-36">
        <Select value={String(windowDays)} onchange={(e) => onWindowChange(Number((e.target as HTMLSelectElement).value))}>
          <option value="7">Last 7 days</option>
          <option value="14">Last 14 days</option>
          <option value="30">Last 30 days</option>
        </Select>
      </div>
    {/snippet}
  </PageHeader>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading…" /></div>
  {:else if loadError}
    <Card padding="none">
      <ErrorState error={loadError} onRetry={load} />
    </Card>
  {:else}
    <!-- Queue depth — unchanged source, still the quickest health signal. -->
    {#if queue}
      <section>
        <h2 class="text-xs font-semibold uppercase tracking-[0.08em] text-surface-500 mb-3 inline-flex items-center gap-1">Queue <FieldHint id="admin.health_section" /></h2>
        <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
          <StatCard label="Pending" value={queue.pending ?? 0} icon={Clock} tone="warning" />
          <StatCard label="Claimed" value={queue.claimed ?? 0} icon={Activity} tone="primary" />
          <StatCard label="Completed" value={queue.completed ?? 0} icon={CheckCircle2} tone="success" />
          <StatCard label="Failed" value={queue.failed ?? 0} icon={AlertTriangle} tone="error" />
        </div>
      </section>
    {/if}

    <!-- Admin tools: users, audit log, traces, reports, SLO and global
         settings. Every card here is live. -->
    <section>
      <h2 class="text-xs font-semibold uppercase tracking-[0.08em] text-surface-500 mb-3 inline-flex items-center gap-1">Tools <FieldHint id="admin.management_section" /></h2>
      <div class="grid grid-cols-1 lg:grid-cols-3 gap-3">
        <a href="/admin/users" class="block bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg p-4 transition-all duration-150 hover:ring-surface-300-700 hover:bg-surface-100-900 cursor-pointer group">
          <div class="flex items-start gap-3">
            <div class="w-9 h-9 rounded-md bg-primary-500/10 text-primary-300 flex items-center justify-center shrink-0 ring-1 ring-primary-500/30">
              <Users size={16} />
            </div>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-1.5">
                <div class="text-sm font-semibold text-surface-900-100">Users</div>
                <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300 transition-colors" />
              </div>
              <p class="text-xs text-surface-500 mt-0.5">
                Create accounts, assign roles (admin / operator / viewer), disable access.
              </p>
            </div>
          </div>
        </a>
        <a href="/admin/audit" class="block bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg p-4 transition-all duration-150 hover:ring-surface-300-700 hover:bg-surface-100-900 cursor-pointer group">
          <div class="flex items-start gap-3">
            <div class="w-9 h-9 rounded-md bg-warning-500/10 text-warning-400 flex items-center justify-center shrink-0 ring-1 ring-warning-500/30">
              <ShieldAlert size={16} />
            </div>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-1.5">
                <div class="text-sm font-semibold text-surface-900-100">Audit log</div>
                <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300 transition-colors" />
              </div>
              <p class="text-xs text-surface-500 mt-0.5">
                Sign-ins, role changes, and sensitive writes.
              </p>
            </div>
          </div>
        </a>
        <a href="/admin/traces" class="block bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg p-4 transition-all duration-150 hover:ring-surface-300-700 hover:bg-surface-100-900 cursor-pointer group">
          <div class="flex items-start gap-3">
            <div class="w-9 h-9 rounded-md bg-primary-500/10 text-primary-300 flex items-center justify-center shrink-0 ring-1 ring-primary-500/30">
              <Eye size={16} />
            </div>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-1.5">
                <div class="text-sm font-semibold text-surface-900-100">Traces</div>
                <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300 transition-colors" />
              </div>
              <p class="text-xs text-surface-500 mt-0.5">
                Application action trail — chat, tools, auth, workflow runs. Live-tail for debugging.
              </p>
            </div>
          </div>
        </a>
        <a href="/admin/artifacts" class="block bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg p-4 transition-all duration-150 hover:ring-surface-300-700 hover:bg-surface-100-900 cursor-pointer group">
          <div class="flex items-start gap-3">
            <div class="w-9 h-9 rounded-md bg-info-500/10 text-info-300 flex items-center justify-center shrink-0 ring-1 ring-info-500/30">
              <FileText size={16} />
            </div>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-1.5">
                <div class="text-sm font-semibold text-surface-900-100">Artifacts</div>
                <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300 transition-colors" />
              </div>
              <p class="text-xs text-surface-500 mt-0.5">
                Generated documents — who, when, from which prompt or run. Audit trail for data exfiltration review.
              </p>
            </div>
          </div>
        </a>
        <a href="/admin/slo" class="block bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg p-4 transition-all duration-150 hover:ring-surface-300-700 hover:bg-surface-100-900 cursor-pointer group">
          <div class="flex items-start gap-3">
            <div class="w-9 h-9 rounded-md bg-warning-500/10 text-warning-300 flex items-center justify-center shrink-0 ring-1 ring-warning-500/30">
              <Gauge size={16} />
            </div>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-1.5">
                <div class="text-sm font-semibold text-surface-900-100">SLO</div>
                <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300 transition-colors" />
              </div>
              <p class="text-xs text-surface-500 mt-0.5">
                Service-level objectives — run latency, error rate, throughput and promotion latency.
              </p>
            </div>
          </div>
        </a>
        <a href="/admin/settings" class="block bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg p-4 transition-all duration-150 hover:ring-surface-300-700 hover:bg-surface-100-900 cursor-pointer group">
          <div class="flex items-start gap-3">
            <div class="w-9 h-9 rounded-md bg-surface-500/10 text-surface-700-300 flex items-center justify-center shrink-0 ring-1 ring-surface-500/30">
              <Settings size={16} />
            </div>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-1.5">
                <div class="text-sm font-semibold text-surface-900-100">Settings</div>
                <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300 transition-colors" />
              </div>
              <p class="text-xs text-surface-500 mt-0.5">
                Global toggles — granular RBAC overlay, feature flags.
              </p>
            </div>
          </div>
        </a>
        <a href="/themes" class="block bg-surface-100-900/70 backdrop-blur-sm ring-1 ring-surface-200-800/80 rounded-lg p-4 transition-all duration-150 hover:ring-surface-300-700 hover:bg-surface-100-900 cursor-pointer group">
          <div class="flex items-start gap-3">
            <div class="w-9 h-9 rounded-md bg-tertiary-500/10 text-tertiary-700-300 flex items-center justify-center shrink-0 ring-1 ring-tertiary-500/30">
              <Palette size={16} />
            </div>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-1.5">
                <div class="text-sm font-semibold text-surface-900-100">Themes</div>
                <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300 transition-colors" />
              </div>
              <p class="text-xs text-surface-500 mt-0.5">
                Build colour themes from a base palette and publish them to every user's picker.
              </p>
            </div>
          </div>
        </a>
      </div>
    </section>

    <!-- Run activity: stacked-bar time series + top failing workflows side by side. -->
    <section class="grid grid-cols-1 lg:grid-cols-3 gap-3">
      <div class="lg:col-span-2">
        <Card>
          <div class="flex items-start justify-between gap-3 mb-3">
            <div>
              <div class="text-xs font-semibold uppercase tracking-[0.08em] text-surface-500">Runs activity</div>
              <div class="text-sm text-surface-700-300 mt-0.5">
                <span class="font-semibold text-surface-900-100 tabular-nums">{(runTotals.completed ?? 0) + (runTotals.failed ?? 0) + (runTotals.running ?? 0) + (runTotals.pending ?? 0)}</span>
                runs · last {windowDays} day{windowDays === 1 ? '' : 's'}
              </div>
            </div>
            <BarChart3 size={16} class="text-surface-500" />
          </div>
          <div class="h-48">
            <StackedBarChart
              data={runSeries}
              series={[...RUN_SERIES]}
              colorFor={runColor}
              ariaLabel="Runs per day grouped by status"
            />
          </div>
          <div class="flex flex-wrap gap-3 mt-2 text-[11px] text-surface-500">
            {#each RUN_SERIES as key}
              <div class="inline-flex items-center gap-1.5">
                <span class="w-2.5 h-2.5 rounded-sm" style="background: {runColor(key)}"></span>
                <span>{key}</span>
                <span class="tabular-nums text-surface-700-300">{runTotals[key] ?? 0}</span>
              </div>
            {/each}
          </div>
        </Card>
      </div>

      <Card>
        <div class="flex items-start justify-between gap-3 mb-3">
          <div>
            <div class="text-xs font-semibold uppercase tracking-[0.08em] text-surface-500">Top failing</div>
            <div class="text-sm text-surface-700-300 mt-0.5">Workflows with most failed runs</div>
          </div>
          <TrendingDown size={16} class="text-error-300" />
        </div>
        {#if (runMetrics?.top_failing ?? []).length === 0}
          <div class="text-xs text-surface-500 py-4 text-center">No failures in this window. 🎉</div>
        {:else}
          <ul class="space-y-2">
            {#each runMetrics?.top_failing ?? [] as wf}
              <li class="flex items-center justify-between gap-3">
                <a href="/workflows/{wf.workflow_id}" class="text-sm text-surface-900-100 hover:text-primary-300 transition-colors truncate">
                  {wf.workflow_name}
                </a>
                <span class="text-xs tabular-nums text-error-300 shrink-0">{wf.failed_count}</span>
              </li>
            {/each}
          </ul>
        {/if}
      </Card>
    </section>

    <!-- Auth activity: one panel, stacked bars by event kind. -->
    <section>
      <Card>
        <div class="flex items-start justify-between gap-3 mb-3">
          <div>
            <div class="text-xs font-semibold uppercase tracking-[0.08em] text-surface-500">Sign-in activity</div>
            <div class="text-sm text-surface-700-300 mt-0.5">
              <span class="font-semibold text-surface-900-100 tabular-nums">{authTotals.login_success ?? 0}</span> successes ·
              <span class="font-semibold text-error-300 tabular-nums">{authTotals.login_failure ?? 0}</span> failures ·
              <span class="font-semibold text-warning-300 tabular-nums">{authTotals.lockout ?? 0}</span> lockouts
            </div>
          </div>
          <LogIn size={16} class="text-surface-500" />
        </div>
        <div class="h-48">
          <StackedBarChart
            data={authSeries}
            series={[...AUTH_SERIES]}
            colorFor={authColor}
            ariaLabel="Auth events per day grouped by event kind"
          />
        </div>
        <div class="flex flex-wrap gap-3 mt-2 text-[11px] text-surface-500">
          {#each AUTH_SERIES as key}
            <div class="inline-flex items-center gap-1.5">
              <span class="w-2.5 h-2.5 rounded-sm" style="background: {authColor(key)}"></span>
              <span>{key}</span>
              <span class="tabular-nums text-surface-700-300">{authTotals[key] ?? 0}</span>
            </div>
          {/each}
        </div>
      </Card>
    </section>
  {/if}
</div>
