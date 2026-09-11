<script lang="ts">
  import { onMount } from 'svelte';
  import { adminMetrics, type SloEntry, errorMessage } from '$lib/api/client';
  import { PageHeader, Card, Spinner, Button, Select, Alert, toast, FieldHint } from '$lib/components/ui';
  import { Activity, RefreshCw } from 'lucide-svelte';

  let slos = $state<SloEntry[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let days = $state<number>(7);
  let from = $state<string>('');

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      const res = await adminMetrics.slo(days);
      slos = res.slos;
      from = res.from;
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, 'Failed to load SLOs');
    } finally {
      loading = false;
    }
  }

  // Threshold tiers: green if better than target, yellow within 25 % of
  // breach, red beyond. "Better=lower" flips the comparison.
  function tone(slo: SloEntry): 'green' | 'yellow' | 'red' | 'gray' {
    if (slo.value === null || slo.value === undefined) return 'gray';
    const v = slo.value;
    const t = slo.target;
    if (slo.better === 'lower') {
      if (v <= t) return 'green';
      if (v <= t * 1.25) return 'yellow';
      return 'red';
    }
    if (v >= t) return 'green';
    if (v >= t * 0.75) return 'yellow';
    return 'red';
  }

  function fmtValue(slo: SloEntry): string {
    if (slo.value === null || slo.value === undefined) return '—';
    if (slo.unit === 'ratio') return `${(slo.value * 100).toFixed(2)}%`;
    if (slo.unit === 's' && slo.value > 3600) return `${(slo.value / 3600).toFixed(1)} h`;
    if (slo.unit === 's' && slo.value > 60) return `${(slo.value / 60).toFixed(1)} min`;
    if (slo.unit === 's') return `${slo.value.toFixed(1)} s`;
    return `${slo.value.toFixed(2)} ${slo.unit}`;
  }

  function fmtTarget(slo: SloEntry): string {
    return `${slo.better === 'lower' ? '≤' : '≥'} ${fmtValue({ ...slo, value: slo.target })}`;
  }

  const TONE_BG: Record<string, string> = {
    green: 'bg-success-500',
    yellow: 'bg-warning-500',
    red: 'bg-error-500',
    gray: 'bg-surface-400',
  };
</script>

<svelte:head><title>SLOs · Admin · Flow Weaver</title></svelte:head>

<div class="p-6 max-w-6xl mx-auto space-y-5">
  <PageHeader
    title="Service-level objectives"
    description="Operational targets the platform commits to. Values are aggregated over the selected window."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
    {/snippet}
  </PageHeader>

  <div class="flex items-end gap-3">
    <Select label="Window (days)" help="slo.window" bind:value={days}>
      <option value={1}>1 day</option>
      <option value={7}>7 days</option>
      <option value={30}>30 days</option>
      <option value={90}>90 days</option>
    </Select>
    <Button variant="ghost" onclick={load}>Apply</Button>
    {#if from}
      <span class="text-xs text-surface-500">Window starts {from.slice(0, 10)} UTC</span>
    {/if}
  </div>

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading SLOs…" /></div>
  {:else if loadError}
    <Alert tone="error" title="Couldn't load SLOs">
      <div class="flex items-start justify-between gap-3">
        <span class="text-xs opacity-90">{loadError}</span>
        <Button size="sm" variant="ghost" icon={RefreshCw} onclick={load}>Retry</Button>
      </div>
    </Alert>
  {:else}
    <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-3">
      {#each slos as slo (slo.key)}
        {@const t = tone(slo)}
        <Card>
          <div class="p-4 space-y-3">
            <div class="flex items-center gap-2">
              <span class="w-2.5 h-2.5 rounded-full {TONE_BG[t]}"></span>
              <span class="text-xs uppercase tracking-wide text-surface-500">{slo.label}</span>
            </div>
            <div class="text-2xl font-mono tabular-nums">{fmtValue(slo)}</div>
            <div class="text-xs text-surface-500">target {fmtTarget(slo)}</div>
          </div>
        </Card>
      {/each}
    </div>

    <Card>
      <div class="p-4 space-y-2 text-sm">
        <div class="flex items-center gap-2">
          <Activity size={14} class="text-surface-500" />
          <h2 class="font-semibold inline-flex items-center gap-1">How these are computed <FieldHint id="admin.slo_method" /></h2>
        </div>
        <ul class="list-disc list-inside text-surface-700-300 space-y-1">
          <li><span class="font-mono text-xs">run_latency_p95_seconds</span> — 95th percentile of <span class="font-mono">CompletedAt − StartedAt</span> across runs in the window.</li>
          <li><span class="font-mono text-xs">error_rate</span> — fraction of runs whose status is <span class="font-mono">failed</span> or <span class="font-mono">failure</span>.</li>
          <li><span class="font-mono text-xs">throughput_jobs_per_hour</span> — succeeded jobs in the window divided by hours elapsed.</li>
          <li><span class="font-mono text-xs">promotion_latency_seconds</span> — median elapsed time between a workflow's first row and its qa/production version.</li>
        </ul>
        <p class="text-xs text-surface-500">
          Targets are platform-wide defaults. To negotiate custom SLOs, override
          via <span class="font-mono">Slo:Targets</span> in the deployment config (open follow-up).
        </p>
      </div>
    </Card>
  {/if}
</div>
