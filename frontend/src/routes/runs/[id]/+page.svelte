<script lang="ts">
  import { confirm, toast } from '$lib/components/ui';
  import { onMount, onDestroy } from 'svelte';
  import { page } from '$app/state';
  import { runs, workflows, errorMessage, type StepRun, type Workflow, type WorkflowEdge, type WorkflowRun } from '$lib/api/client';
  import { connectRunStream, type RunStreamState } from '$lib/api/websocket';
  import StepDetail from '$lib/components/StepDetail.svelte';
  import TransformPlayground from '$lib/components/TransformPlayground.svelte';
  import {
    PageHeader, Card, Button, StatusBadge, Spinner, Alert, Dialog,
    formatDateTime, formatDuration, truncate,
    statusTone, toneDotClass,
    FieldHint,
  } from '$lib/components/ui';
  import { Activity, Eye, ArrowRight, StopCircle, CheckCircle2, XCircle, Loader2, MinusCircle, Circle } from 'lucide-svelte';

  let run = $state<WorkflowRun | null>(null);
  let steps = $state<StepRun[]>([]);
  let workflow = $state<Workflow | null>(null);
  let loading = $state(true);
  let error = $state('');
  let closeWebSocket: (() => void) | null = null;
  let streaming = $state(false);
  let streamState = $state<RunStreamState>('closed');

  let showPlayground = $state(false);
  let playgroundInput = $state('{}');
  let playgroundExpression = $state('');

  let showDataFlow = $state(true);
  let cancelling = $state(false);

  const canCancel = $derived(run?.status === 'running' || run?.status === 'pending');
  // Terminal statuses stop the stream from reconnecting — once a run is done
  // there's nothing more to push.
  const isTerminal = $derived(
    !!run && run.status !== 'running' && run.status !== 'pending',
  );
  // The "Live" pulse may only show when the socket is actually open; while
  // the stream is (re)connecting we say so instead of implying live data.
  const isReconnecting = $derived(streamState === 'connecting' || streamState === 'reconnecting');

  // Status glyphs let the DAG/data-flow convey state without relying on
  // color alone. Shared by the per-node badge and the legend below it.
  function statusGlyph(status: string) {
    switch (status) {
      case 'success':
      case 'completed': return CheckCircle2;
      case 'failed':
      case 'failure': return XCircle;
      case 'running': return Loader2;
      case 'skipped': return MinusCircle;
      default: return Circle;
    }
  }
  const statusLegend = [
    { status: 'running', label: 'Running', icon: Loader2 },
    { status: 'success', label: 'Success', icon: CheckCircle2 },
    { status: 'failed', label: 'Failed', icon: XCircle },
    { status: 'skipped', label: 'Skipped', icon: MinusCircle },
    { status: 'pending', label: 'Pending', icon: Circle },
  ];

  onMount(loadData);
  onDestroy(() => stopStreaming());

  async function loadData() {
    loading = true;
    error = '';
    try {
      const [runRes, stepsRes] = await Promise.all([
        runs.get(page.params.id!),
        runs.steps(page.params.id!),
      ]);
      run = runRes;
      steps = stepsRes.data;
      if (run.workflow_id) {
        try { workflow = await workflows.get(run.workflow_id); } catch { /* workflow may have been deleted */ }
      }
      if (run.status === 'running' && !closeWebSocket) startStreaming();
    } catch (e) {
      error = errorMessage(e);
          toast.fromError(e, 'Action failed');
    } finally {
      loading = false;
    }
  }

  function startStreaming() {
    if (closeWebSocket) return;
    streaming = true;
    closeWebSocket = connectRunStream(page.params.id!, (data) => {
      switch (data.type) {
        case 'run_status':
          if (run) run = { ...run, status: data.status ?? run.status, started_at: data.started_at ?? run.started_at, completed_at: data.completed_at ?? run.completed_at };
          break;
        case 'steps_update':
          if (data.steps) steps = data.steps as StepRun[];
          break;
        case 'step_update':
          if (data.step) {
            const step = data.step as StepRun;
            const idx = steps.findIndex((s) => s.id === step.id);
            if (idx >= 0) steps = [...steps.slice(0, idx), step, ...steps.slice(idx + 1)];
            else steps = [...steps, step];
          }
          break;
        case 'run_completed':
          if (run) run = { ...run, status: data.status ?? run.status };
          stopStreaming();
          loadData();
          break;
        case 'error':
          error = data.message || 'Stream error';
          stopStreaming();
          break;
      }
    }, {
      onStateChange: (s) => (streamState = s),
      // Stop reconnecting once the run is terminal — there's no polling
      // fallback on this page, so the only live source is this socket.
      shouldReconnect: () => !isTerminal,
    });
  }

  function stopStreaming() {
    if (closeWebSocket) { closeWebSocket(); closeWebSocket = null; }
    streaming = false;
    streamState = 'closed';
  }

  const orderedNodes = $derived.by(() => {
    if (steps.length === 0) return [];
    const seen = new Set<string>();
    const result: { nodeId: string; step: StepRun }[] = [];
    for (const step of steps) {
      if (!seen.has(step.node_id)) {
        seen.add(step.node_id);
        result.push({ nodeId: step.node_id, step });
      }
    }
    return result;
  });

  const dataFlowEdges = $derived.by(() => {
    if (!workflow?.edges || steps.length === 0) return [];
    const stepMap = new Map(steps.map((s) => [s.node_id, s]));
    return workflow.edges.map((edge: WorkflowEdge) => ({
      source: edge.source,
      target: edge.target,
      type: edge.type,
      sourceStep: stepMap.get(edge.source),
      targetStep: stepMap.get(edge.target),
    }));
  });

  function openPlayground(step: StepRun) {
    playgroundInput = JSON.stringify(step.output_payload, null, 2) || '{}';
    playgroundExpression = '';
    showPlayground = true;
  }

  async function cancelRun() {
    if (!run || cancelling) return;
    const ok = await confirm({
      tone: 'danger',
      title: 'Stop run?',
      message: 'In-flight steps are left to finish.',
      confirmLabel: 'Stop run',
    });
    if (!ok) return;
    cancelling = true;
    try {
      const updated = await runs.cancel(run.id);
      run = updated;
      stopStreaming();
      toast.success('Run cancelled');
      await loadData();
    } catch (e) {
      toast.fromError(e, 'Cancel failed');
    } finally {
      cancelling = false;
    }
  }
</script>

<svelte:head><title>Run {page.params.id?.slice(0, 8)} · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title={truncate(page.params.id, 12) + '…'}
    breadcrumbs={[{ label: 'Runs', href: '/runs' }, { label: 'Detail' }]}
  >
    {#snippet actions()}
      {#if streaming && streamState === 'open'}
        <span class="inline-flex items-center gap-1.5 text-xs text-primary-300" title="Live stream connected">
          <span class="w-1.5 h-1.5 rounded-full bg-primary-400 animate-pulse"></span>
          Live
        </span>
      {:else if streaming && isReconnecting}
        <span class="inline-flex items-center gap-1.5 text-xs text-warning-300" title="Reconnecting to live stream">
          <span class="w-1.5 h-1.5 rounded-full bg-warning-400 animate-pulse"></span>
          Reconnecting…
        </span>
      {/if}
      {#if canCancel}
        <Button variant="danger" icon={StopCircle} onclick={cancelRun} loading={cancelling}>Stop run</Button>
      {/if}
      <Button variant="secondary" icon={Activity} href="/runs/{page.params.id}/monitor">Monitor</Button>
    {/snippet}
  </PageHeader>

  {#if error}<Alert tone="error">{error}</Alert>{/if}

  {#if loading && !run}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if run}
    <Card>
      <div class="grid grid-cols-2 md:grid-cols-4 gap-4">
        <div>
          <div class="text-xs text-surface-500 mb-1.5">Status</div>
          <StatusBadge status={run.status} />
        </div>
        <div>
          <div class="text-xs text-surface-500 mb-1.5">Trigger</div>
          <div class="text-sm text-surface-900-100">{run.trigger || '—'}</div>
        </div>
        <div>
          <div class="text-xs text-surface-500 mb-1.5">Started</div>
          <div class="text-sm text-surface-900-100">{formatDateTime(run.started_at)}</div>
        </div>
        <div>
          <div class="text-xs text-surface-500 mb-1.5">Duration</div>
          <div class="text-sm font-mono tabular-nums text-surface-900-100">{formatDuration(run.started_at, run.completed_at)}</div>
        </div>
      </div>
      {#if run.workflow_id}
        <div class="mt-4 pt-4 border-t border-surface-200-800">
          <span class="text-xs text-surface-500">Workflow:</span>
          <a href="/workflows/{run.workflow_id}" class="ml-2 text-xs font-mono text-primary-300 hover:text-primary-200">{run.workflow_id}</a>
        </div>
      {/if}
    </Card>

    {#if workflow && orderedNodes.length > 0}
      <section>
        <div class="flex items-center justify-between mb-3">
          <h2 class="text-sm font-semibold tracking-tight text-surface-800-200 inline-flex items-center gap-1">Data flow <FieldHint id="runs.data_flow" /></h2>
          <Button size="xs" variant="ghost" icon={Eye} onclick={() => (showDataFlow = !showDataFlow)}>
            {showDataFlow ? 'Hide' : 'Show'}
          </Button>
        </div>
        {#if showDataFlow}
          <Card>
            <!-- Legend: status is conveyed by color + glyph + label so it
                 doesn't rely on color alone (WCAG 1.4.1). -->
            <div class="flex flex-wrap items-center gap-x-4 gap-y-1.5 mb-3 text-[11px] text-surface-600-400">
              {#each statusLegend as item}
                {@const Glyph = item.icon}
                <span class="inline-flex items-center gap-1.5">
                  <span class="w-2 h-2 rounded-full {toneDotClass(statusTone(item.status))}"></span>
                  <Glyph size={12} class="text-surface-500" />
                  {item.label}
                </span>
              {/each}
            </div>
            <div class="overflow-x-auto">
              <div class="flex items-center gap-2 min-w-max py-2">
                {#each orderedNodes as { nodeId, step }, i}
                  {@const tone = step.status === 'success' ? 'success' : step.status === 'failed' || step.status === 'failure' ? 'error' : step.status === 'running' ? 'info' : 'neutral'}
                  {@const colorMap = { success: 'border-success-500/40 bg-success-500/10 text-success-300', error: 'border-error-500/40 bg-error-500/10 text-error-300', info: 'border-primary-500/40 bg-primary-500/10 text-primary-300', neutral: 'border-surface-300-700 bg-surface-200-800/40 text-surface-600-400' }}
                  {@const StatusGlyph = statusGlyph(step.status)}
                  <div class="flex flex-col items-center gap-1">
                    <div class="px-3 py-2 rounded-md border text-xs font-mono min-w-[110px] text-center inline-flex items-center justify-center gap-1.5 {colorMap[tone]}">
                      <StatusGlyph size={12} class="shrink-0" />
                      {nodeId}
                    </div>
                    <div class="text-[10px] text-surface-500">{step.status}</div>
                  </div>
                  {#if i < orderedNodes.length - 1}
                    {@const edge = dataFlowEdges.find((e) => e.source === nodeId)}
                    {@const arrowColor = edge?.type === 'failure' ? 'text-error-500' : edge?.type === 'always' ? 'text-primary-400' : 'text-success-500'}
                    <ArrowRight size={14} class={arrowColor} />
                  {/if}
                {/each}
              </div>
            </div>
          </Card>
        {/if}
      </section>
    {/if}

    <section>
      <h2 class="text-sm font-semibold tracking-tight text-surface-800-200 mb-3 inline-flex items-center gap-1">Steps <FieldHint id="runs.steps" /></h2>
      {#if steps.length === 0}
        <Card><p class="text-sm text-surface-500 text-center py-6">No steps recorded yet.</p></Card>
      {:else}
        <div class="space-y-2">
          {#each steps as step (step.id)}
            <StepDetail {step} onOpenPlayground={openPlayground} />
          {/each}
        </div>
        <p class="text-xs text-surface-500 mt-2">{steps.length} step{steps.length === 1 ? '' : 's'}</p>
      {/if}
    </section>
  {/if}
</div>

<Dialog bind:open={showPlayground} title="Transform playground" size="full">
  <div class="h-[60vh]">
    <TransformPlayground bind:inputData={playgroundInput} bind:expression={playgroundExpression} />
  </div>
</Dialog>
