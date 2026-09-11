<script lang="ts">
  import { confirm, toast } from '$lib/components/ui';
  import { onMount, onDestroy, tick } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import {
    runs, workflows, snippets, errorMessage,
    type WorkflowRun, type StepRun, type Workflow,
    type WorkflowNode, type WorkflowEdge, type Snippet,
  } from '$lib/api/client';
  import { connectRunStream, type RunStreamState } from '$lib/api/websocket';
  import { SvelteFlow, Controls, Background, ConnectionMode, type Node, type Edge } from '@xyflow/svelte';
  import '@xyflow/svelte/dist/style.css';
  import DagNode from '$lib/components/workflow/DagNode.svelte';
  import SentinelNode from '$lib/components/workflow/SentinelNode.svelte';
  import { autoLayout, needsAutoLayout } from '$lib/workflow/autoLayout';
  import {
    StatusBadge, Button, Spinner, Alert,
    formatTime, formatDuration, truncate,
    statusTone, toneDotClass,
    FieldHint,
  } from '$lib/components/ui';
  import { ArrowLeft, ChevronDown, ChevronRight, Wand2, Wrench, GitBranch, Download, StopCircle, ArrowDown, CheckCircle2, XCircle, Loader2, MinusCircle, Circle } from 'lucide-svelte';
  import { authStore } from '$lib/stores/auth.svelte';

  const isAdmin = $derived(authStore.session?.role === 'admin');

  let run = $state<WorkflowRun | null>(null);
  let workflow = $state<Workflow | null>(null);
  // Used to resolve each node.snippet_id → human snippet name + type
  // so the DAG matches the editor view (previously nodes showed the
  // bare "node-1", "node-2" ids).
  let serviceMap = $state<Map<string, Snippet>>(new Map());
  let steps = $state<StepRun[]>([]);
  const nodeTypes = { default: DagNode, sentinel: SentinelNode } as Record<string, any>;
  let loading = $state(true);
  let error = $state('');
  let closeWebSocket: (() => void) | null = null;
  let streaming = $state(false);
  let streamState = $state<RunStreamState>('closed');
  let elapsedInterval: ReturnType<typeof setInterval> | null = null;
  let elapsed = $state('');
  let pollInterval: ReturnType<typeof setInterval> | null = null;

  let expandedSteps = $state(new Set<string>());
  let cancelling = $state(false);

  const canCancel = $derived(run?.status === 'running' || run?.status === 'pending');
  // The "Live" pulse only shows when the socket is genuinely open; otherwise
  // we say "Reconnecting…" rather than imply we're receiving live updates.
  const isReconnecting = $derived(streamState === 'connecting' || streamState === 'reconnecting');

  let flowNodes = $state<Node[]>([]);
  let flowEdges = $state<Edge[]>([]);

  let timelineEl = $state<HTMLDivElement | undefined>(undefined);
  // Tracks whether the timeline is scrolled near its bottom. When the user
  // has scrolled up to read an earlier step we stop auto-scrolling and offer
  // a "Jump to latest" affordance instead of yanking them back down.
  let atTimelineBottom = $state(true);

  // Pulls every failing step's message for the header banner. Without
  // this the user saw only "failed" on the status badge with no
  // explanation of why — they had to click each step in the timeline
  // to find the error. Distinct/trimmed so we don't spam the banner
  // with duplicates when multiple devices fail the same way.
  const failedStepErrors = $derived.by(() => {
    if (!run || (run.status !== 'failed' && run.status !== 'failure')) return [] as string[];
    const seen = new Set<string>();
    const out: string[] = [];
    for (const s of steps) {
      if (s.status !== 'failed' && s.status !== 'failure') continue;
      const msg = (s.error || '').trim();
      if (!msg || seen.has(msg)) continue;
      seen.add(msg);
      out.push(msg);
    }
    return out;
  });

  onMount(loadData);
  onDestroy(cleanup);

  function cleanup() {
    if (closeWebSocket) { closeWebSocket(); closeWebSocket = null; }
    if (elapsedInterval) { clearInterval(elapsedInterval); elapsedInterval = null; }
    if (pollInterval) { clearInterval(pollInterval); pollInterval = null; }
    streaming = false;
    streamState = 'closed';
  }

  async function loadData() {
    loading = true;
    error = '';
    try {
      const [runRes, stepsRes] = await Promise.all([
        runs.get(page.params.id!),
        runs.steps(page.params.id!),
      ]);
      run = runRes;
      // runs.steps returns ListResponse<StepRun> ({ data, total, … }); the
      // previous Array.isArray() guard always fell through to [], which is
      // why the timeline appeared empty until the first polling tick
      // overwrote it.
      steps = stepsRes.data ?? [];
      if (run.workflow_id) {
        // Load workflow + service catalog in parallel so the node boxes
        // can render service name/type on first paint — same pattern as
        // the editor view.
        const [wf, svcRes] = await Promise.all([
          workflows.get(run.workflow_id),
          snippets.list(200, 0).catch(() => ({ data: [] as Snippet[] })),
        ]);
        workflow = wf;
        serviceMap = new Map(svcRes.data.map((s: Snippet) => [s.id, s]));
        buildFlow();
      }
      // Pending and running both need live updates — the scheduler can
      // leave a run in "pending" for a moment before the worker picks it
      // up, and the old code only polled on "running", which is why the
      // page looked frozen right after clicking Run.
      if (isLive(run.status)) {
        startElapsedTimer();
        connectLive();
      } else {
        updateElapsed();
        updateFlowFromSteps();
      }
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Action failed');
    } finally {
      loading = false;
    }
  }

  function isLive(status: string | undefined): boolean {
    return status === 'running' || status === 'pending';
  }

  function startElapsedTimer() {
    updateElapsed();
    if (elapsedInterval) clearInterval(elapsedInterval);
    elapsedInterval = setInterval(updateElapsed, 1000);
  }

  function updateElapsed() {
    if (!run?.started_at) { elapsed = '—'; return; }
    elapsed = formatDuration(run.started_at, run.completed_at);
  }

  function connectLive() {
    streaming = true;
    startPolling();
    try {
      closeWebSocket = connectRunStream(page.params.id!, (data) => {
        switch (data.type) {
          case 'run_status':
            if (run) run = { ...run, status: data.status ?? run.status, started_at: data.started_at ?? run.started_at, completed_at: data.completed_at ?? run.completed_at };
            if (data.status && data.status !== 'running') onRunFinished();
            break;
          case 'steps_update':
            if (data.steps) { steps = data.steps as StepRun[]; updateFlowFromSteps(); scrollTimeline(); }
            break;
          case 'step_update':
            if (data.step) {
              const step = data.step as StepRun;
              const idx = steps.findIndex((s) => s.id === step.id);
              if (idx >= 0) steps = [...steps.slice(0, idx), step, ...steps.slice(idx + 1)];
              else steps = [...steps, step];
              updateFlowFromSteps();
              scrollTimeline();
            }
            break;
          case 'run_completed':
            if (run) run = { ...run, status: data.status ?? run.status, completed_at: data.completed_at ?? run.completed_at };
            onRunFinished();
            loadData();
            break;
        }
      }, {
        onStateChange: (s) => (streamState = s),
        // Stop reconnecting once the run reaches a terminal status — polling
        // also stops via onRunFinished, so there's nothing left to stream.
        shouldReconnect: () => isLive(run?.status),
      });
    } catch {
      // WebSocket failed — polling already running.
    }
  }

  function startPolling() {
    if (pollInterval) return;
    pollInterval = setInterval(async () => {
      try {
        const [runRes, stepsRes] = await Promise.all([
          runs.get(page.params.id!),
          runs.steps(page.params.id!),
        ]);
        run = runRes;
        steps = stepsRes.data;
        updateFlowFromSteps();
        scrollTimeline();
        if (!isLive(run.status)) onRunFinished();
      } catch {
        // Polling errors are recoverable — keep trying until the interval is cleared.
      }
    }, 2000);
  }

  function onRunFinished() {
    cleanup();
    updateElapsed();
    updateFlowFromSteps();
  }

  // Distance from the bottom (px) under which we consider the user "pinned"
  // to the latest step and keep auto-scrolling. Above it, we leave their
  // scroll position alone and surface a "Jump to latest" button instead.
  const NEAR_BOTTOM_PX = 80;

  function updateAtBottom() {
    if (!timelineEl) return;
    atTimelineBottom = timelineEl.scrollHeight - timelineEl.scrollTop - timelineEl.clientHeight < NEAR_BOTTOM_PX;
  }

  // Auto-scroll only when the user is already near the bottom, so a new step
  // doesn't yank them away from an earlier one they're reading.
  async function scrollTimeline() {
    if (!atTimelineBottom) return;
    await tick();
    if (timelineEl) timelineEl.scrollTop = timelineEl.scrollHeight;
  }

  // Forced scroll for the "Jump to latest" button — always goes to the
  // bottom and re-pins the timeline.
  async function jumpToLatest() {
    atTimelineBottom = true;
    await tick();
    if (timelineEl) timelineEl.scrollTop = timelineEl.scrollHeight;
  }

  function getNodeStyle(status: string): string {
    // Width matches the editor's 180 px so node labels wrap/truncate the
    // same way in both views. The status color lives on the outer wrapper
    // (DagNode itself renders a transparent inner box in monitor mode).
    const base = 'width: 180px; border-radius: 8px; padding: 8px 12px; border-width: 2px; border-style: solid;';
    switch (status) {
      case 'running': return base + 'background: rgb(59 130 246 / 0.15); border-color: rgb(59 130 246); color: rgb(147 197 253); box-shadow: 0 0 0 4px rgb(59 130 246 / 0.1);';
      case 'completed':
      case 'success': return base + 'background: rgb(16 185 129 / 0.15); border-color: rgb(16 185 129); color: rgb(110 231 183);';
      case 'failure':
      case 'failed': return base + 'background: rgb(239 68 68 / 0.15); border-color: rgb(239 68 68); color: rgb(252 165 165);';
      case 'skipped': return base + 'background: var(--color-surface-100-900); border-color: var(--color-surface-300-700); color: var(--color-surface-500); opacity: 0.6;';
      default: return base + 'background: var(--color-surface-200-800); border-color: var(--color-surface-300-700); color: var(--color-surface-500);';
    }
  }

  function isSuccessStatus(status: string): boolean {
    return status === 'completed' || status === 'success';
  }

  function getEdgeStyle(sourceStatus: string, targetStatus: string): string {
    if (isSuccessStatus(sourceStatus) && (isSuccessStatus(targetStatus) || targetStatus === 'running'))
      return 'stroke: rgb(16 185 129); stroke-width: 2;';
    if (sourceStatus === 'failure' || sourceStatus === 'failed') return 'stroke: rgb(239 68 68); stroke-width: 2;';
    if (sourceStatus === 'running') return 'stroke: rgb(59 130 246); stroke-width: 2;';
    return 'stroke: var(--color-surface-400-600); stroke-width: 1.5;';
  }

  // Compact glyphs (✓ / ✕ / ● / ⊘ / ○) prefixed to each DAG node label so
  // status reads without relying on border color. Mirrors the legend below
  // the DAG and the icon set used in the timeline.
  function statusGlyphChar(status: string): string {
    switch (status) {
      case 'success':
      case 'completed': return '✓';
      case 'failed':
      case 'failure': return '✕';
      case 'running': return '●';
      case 'skipped': return '⊘';
      default: return '○';
    }
  }
  const statusLegend = [
    { status: 'running', label: 'Running', icon: Loader2 },
    { status: 'success', label: 'Success', icon: CheckCircle2 },
    { status: 'failed', label: 'Failed', icon: XCircle },
    { status: 'skipped', label: 'Skipped', icon: MinusCircle },
    { status: 'pending', label: 'Pending', icon: Circle },
  ];

  function buildFlow() {
    if (!workflow) return;

    // A sentinel is identified by its SNIPPET id, not its node id — the engine's
    // rule, and the one the editor now follows. A graph whose start node is
    // `{"id":"start","snippet_id":"__start__"}` (an imported bundle, or a
    // load-test seed) would otherwise draw it as a task node here while the
    // editor drew it as a sentinel, so the same run looked like two graphs.
    const sentinelRename = new Map<string, string>();
    for (const marker of ['__start__', '__end__'] as const) {
      const first = (workflow.nodes || []).find((n: WorkflowNode) => n.snippet_id === marker);
      if (first && first.id !== marker) sentinelRename.set(first.id, marker);
    }
    const canvasId = (id: string) => sentinelRename.get(id) ?? id;

    flowNodes = (workflow.nodes || []).map((n: WorkflowNode) => {
      const isStart = n.snippet_id === '__start__' || n.id === '__start__';
      const isEnd = n.snippet_id === '__end__' || n.id === '__end__';
      if (isStart) return { id: '__start__', position: { x: n.x || 0, y: n.y || 0 }, data: { label: 'Start', kind: 'start' }, type: 'sentinel', style: 'width: 56px; height: 56px; padding: 0; background: transparent; border: none; box-shadow: none;' };
      if (isEnd) return { id: '__end__', position: { x: n.x || 0, y: n.y || 0 }, data: { label: 'End', kind: 'end' }, type: 'sentinel', style: 'width: 56px; height: 56px; padding: 0; background: transparent; border: none; box-shadow: none;' };

      // Mirror the editor's enrichment so DagNode renders identical labels
      // in both views. Integration actions synthesize their label from the
      // configured HTTP method + path. Regular nodes fall back to the
      // snippet description as a third (subtitle) line.
      const svc = serviceMap.get(n.snippet_id);
      const overrides = (n.config_overrides || {}) as Record<string, unknown>;
      const isIntegrationAction = n.snippet_id === 'integration_action';
      let serviceName = svc?.name ?? '';
      let serviceType = svc?.type ?? '';
      let subtitle = svc?.description ? svc.description.slice(0, 60) : '';
      if (isIntegrationAction) {
        serviceName = `${overrides.method || 'GET'} ${overrides.path || ''}`.trim();
        serviceType = 'integration_action';
        subtitle = (overrides.integration_name as string) || '';
      }

      return {
        id: n.id,
        position: { x: n.x || 0, y: n.y || 0 },
        data: {
          label: n.id,
          nodeId: n.id,
          serviceName,
          serviceType,
          subtitle,
          description: svc?.description || '',
          snippetId: n.snippet_id,
          configOverrides: overrides,
        },
        type: 'default',
        style: getNodeStyle('pending'),
      };
    });
    flowEdges = (workflow.edges || []).map((e: WorkflowEdge, i: number) => ({
      // Follow the sentinel rename into the edges: an endpoint left pointing at
      // the stored id would name a node this canvas no longer has, and xyflow
      // drops such an edge without saying so.
      id: `e-${canvasId(e.source)}-${canvasId(e.target)}-${e.type}-${i}`,
      source: canvasId(e.source),
      target: canvasId(e.target),
      // Preserve the per-side handle assignments so the monitor view
      // renders edges from the exact side the editor saved them to.
      sourceHandle: e.source_handle ?? undefined,
      targetHandle: e.target_handle ?? undefined,
      label: e.type === 'success' ? '' : e.type,
      animated: false,
      style: 'stroke: var(--color-surface-400-600); stroke-width: 1.5;',
    }));

    // Match the editor's layout. Agent-built workflows (update_workflow)
    // leave every node at x/y = 0 and edges without handles, so without
    // this the monitor stacks all nodes at the origin while the editor —
    // which runs the same pass — shows them laid out. Run the identical
    // autoLayout so both views agree. updateFlowFromSteps preserves
    // position via {...n}, so this survives live step updates.
    if (needsAutoLayout(flowNodes, flowEdges)) {
      const laid = autoLayout(flowNodes, flowEdges);
      flowNodes = laid.nodes;
      flowEdges = laid.edges;
    }

    updateFlowFromSteps();
  }

  function updateFlowFromSteps() {
    if (!workflow) return;
    const stepMap = new Map<string, StepRun>();
    for (const s of steps) {
      const existing = stepMap.get(s.node_id);
      if (!existing || new Date(s.started_at || 0).getTime() > new Date(existing.started_at || 0).getTime()) {
        stepMap.set(s.node_id, s);
      }
    }
    flowNodes = flowNodes.map((n) => {
      if (n.id === '__start__' || n.id === '__end__') return n;
      const step = stepMap.get(n.id);
      const stepStatus = step?.status || 'pending';
      // Prefix a status glyph onto the node label so the DAG conveys state
      // through a symbol too, not by border color alone (WCAG 1.4.1). The
      // SvelteFlow node identity (`n.id`) is untouched — only the displayed
      // `nodeId` carries the glyph.
      return {
        ...n,
        data: { ...(n.data as Record<string, unknown>), status: stepStatus, nodeId: `${statusGlyphChar(stepStatus)} ${n.id}` },
        style: getNodeStyle(stepStatus),
      };
    });
    flowEdges = flowEdges.map((e) => {
      const srcStep = stepMap.get(e.source);
      const tgtStep = stepMap.get(e.target);
      const srcStatus = srcStep?.status || 'pending';
      const tgtStatus = tgtStep?.status || 'pending';
      return { ...e, animated: srcStatus === 'running' || tgtStatus === 'running', style: getEdgeStyle(srcStatus, tgtStatus) };
    });
  }

  function toggleStep(id: string) {
    const next = new Set(expandedSteps);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expandedSteps = next;
  }

  // Bundles the run + workflow snapshot + every step (with full input, output,
  // logs, error) into a single JSON file. Admin-only because step payloads
  // can contain device-side data we don't surface to lower-privileged roles.
  function exportRunJson() {
    if (!run) return;
    const payload = {
      exported_at: new Date().toISOString(),
      run: {
        id: run.id,
        workflow_id: run.workflow_id,
        status: run.status,
        trigger: run.trigger,
        started_at: run.started_at,
        completed_at: run.completed_at,
        input_payload: run.input_payload,
        target_devices: run.target_devices,
        target_pools: run.target_pools,
        created_at: run.created_at,
      },
      workflow: workflow
        ? {
            id: workflow.id,
            name: workflow.name,
            version: workflow.version,
            environment: workflow.environment,
            nodes: workflow.nodes,
            edges: workflow.edges,
          }
        : null,
      steps: steps.map((s) => ({
        id: s.id,
        node_id: s.node_id,
        device_id: s.device_id,
        status: s.status,
        started_at: s.started_at,
        completed_at: s.completed_at,
        worker_id: s.worker_id,
        input_payload: s.input_payload,
        output_payload: s.output_payload,
        logs: s.logs,
        error: s.error,
      })),
    };

    const blob = new Blob([JSON.stringify(payload, null, 2)], {
      type: 'application/json',
    });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `run-${run.id.slice(0, 8)}-${run.status}.json`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
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
      cleanup();
      toast.success('Run cancelled');
      await loadData();
    } catch (e) {
      toast.fromError(e, 'Cancel failed');
    } finally {
      cancelling = false;
    }
  }

  function editWithAI(step: StepRun) {
    if (!workflow) return;
    const node = (workflow.nodes || []).find((n) => n.id === step.node_id);
    const context = {
      node_id: step.node_id,
      service_id: node?.snippet_id || '',
      run_id: page.params.id,
      workflow_id: workflow?.id || '',
      mode: 'edit',
      output: step.output_payload ? JSON.stringify(step.output_payload).slice(0, 500) : '',
    };
    goto('/ai/chat?fix=' + encodeURIComponent(JSON.stringify(context)));
  }

  function askAIToFix(step: StepRun) {
    if (!workflow) return;
    const node = (workflow.nodes || []).find((n) => n.id === step.node_id);
    const context = {
      node_id: step.node_id,
      error: step.error || '',
      logs: (step.logs || '').slice(0, 500),
      service_id: node?.snippet_id || '',
      run_id: page.params.id,
      workflow_id: workflow?.id || '',
    };
    goto('/ai/chat?fix=' + encodeURIComponent(JSON.stringify(context)));
  }

  // Terminal step statuses emitted by the engine — see Services/Engine/Status.cs.
  // `success` / `failure` are edge-result tokens (not step statuses); kept for
  // safety in case older runs were persisted with either spelling.
  const TERMINAL_STEP_STATUSES = ['completed', 'failed', 'failure', 'success', 'skipped'];
  const completedSteps = $derived(steps.filter((s) => TERMINAL_STEP_STATUSES.includes(s.status)).length);
  // Counter at the top splits node-level vs step-level progress because
  // per_device snippets fan out — a single node can produce N step_runs, so
  // a `9/4` reading was misleading. Showing both lets you see DAG advance
  // and execution work at a glance.
  const realNodes = $derived(
    workflow ? workflow.nodes.filter((n) => n.id !== '__start__' && n.id !== '__end__') : [],
  );
  const totalNodes = $derived(realNodes.length);
  const completedNodes = $derived.by(() => {
    let count = 0;
    for (const node of realNodes) {
      const nodeSteps = steps.filter((s) => s.node_id === node.id);
      if (nodeSteps.length === 0) continue;
      if (nodeSteps.every((s) => TERMINAL_STEP_STATUSES.includes(s.status))) count++;
    }
    return count;
  });
  const totalSteps = $derived(steps.length);
  // Bar tracks step-level progress — finer-grained, animates more often as
  // each per-device fan-out completes.
  const progressPct = $derived(totalSteps > 0 ? Math.round((completedSteps / totalSteps) * 100) : 0);
  const progressBarTone = $derived(run?.status === 'failed' || run?.status === 'failure' ? 'bg-error-500' : run?.status === 'success' ? 'bg-success-500' : 'bg-primary-500');
</script>

<svelte:head><title>Monitor {page.params.id?.slice(0, 8)} · FlowWeaver</title></svelte:head>

<div class="flex flex-col h-screen">
  {#if run}
    <header class="flex items-center justify-between gap-3 px-4 py-3 bg-surface-100-900 border-b border-surface-200-800 flex-wrap">
      <div class="flex items-center gap-3 min-w-0">
        <Button variant="ghost" size="sm" icon={ArrowLeft} href="/runs">Runs</Button>
        <span class="text-surface-400-600">/</span>
        <div class="min-w-0">
          <div class="flex items-center gap-2 flex-wrap">
            {#if workflow}
              <span class="font-semibold tracking-tight text-surface-900-100 truncate">{workflow.name}</span>
            {/if}
            <span class="font-mono text-xs text-surface-500">{truncate(page.params.id, 12)}…</span>
          </div>
          {#if run.trigger}
            <div class="text-xs text-surface-500 mt-0.5">Trigger: {run.trigger}</div>
          {/if}
        </div>
      </div>

      <div class="flex items-center gap-3 flex-wrap">
        <StatusBadge status={run.status} />
        <div class="flex items-center gap-2 min-w-[200px] sm:min-w-[280px] flex-1 sm:flex-none">
          <div class="flex-1 h-1.5 bg-surface-200-800 rounded-full overflow-hidden">
            <div class="h-full transition-all duration-500 {progressBarTone}" style="width: {progressPct}%"></div>
          </div>
          <span class="text-xs text-surface-600-400 font-mono tabular-nums whitespace-nowrap">
            {#if totalNodes > 0}
              {completedNodes}/{totalNodes} nodes · {completedSteps}/{totalSteps} steps
            {:else}
              {completedSteps}/{totalSteps} steps
            {/if}
          </span>
        </div>
      </div>

      <div class="flex items-center gap-3 flex-wrap text-xs text-surface-600-400">
        <div><span class="text-surface-500">Elapsed:</span> <span class="ml-1 font-mono tabular-nums text-surface-800-200">{elapsed}</span></div>
        <div><span class="text-surface-500">Started:</span> <span class="ml-1">{formatTime(run.started_at)}</span></div>
        {#if run.completed_at}
          <div><span class="text-surface-500">Finished:</span> <span class="ml-1">{formatTime(run.completed_at)}</span></div>
        {/if}
        {#if streaming && streamState === 'open'}
          <span class="inline-flex items-center gap-1.5 text-primary-300" title="Live stream connected">
            <span class="w-1.5 h-1.5 rounded-full bg-primary-400 animate-pulse"></span> Live
          </span>
        {:else if streaming && isReconnecting}
          <span class="inline-flex items-center gap-1.5 text-warning-300" title="Reconnecting to live stream">
            <span class="w-1.5 h-1.5 rounded-full bg-warning-400 animate-pulse"></span> Reconnecting…
          </span>
        {/if}
        {#if canCancel}
          <Button size="xs" variant="danger" icon={StopCircle} onclick={cancelRun} loading={cancelling}>Stop run</Button>
        {/if}
        {#if run.workflow_id}
          <Button
            size="xs"
            variant="secondary"
            icon={GitBranch}
            href="/workflows/{run.workflow_id}"
          >
            Back to workflow
          </Button>
        {/if}
        <Button size="xs" variant="ghost" href="/runs/{page.params.id}">Details</Button>
      </div>
    </header>
  {/if}

  {#if error && run}
    <div class="px-4 mt-3"><Alert tone="error">{error}</Alert></div>
  {/if}

  {#if run && (run.status === 'failed' || run.status === 'failure')}
    <div class="px-4 mt-3">
      <Alert tone="error">
        <div class="font-medium">Run failed</div>
        {#if failedStepErrors.length === 0 && run.error}
          <!-- Orchestration failed outside any step (an unresolvable
               snippet_id, a malformed DAG, a target set that resolved to
               nothing). The reason lives on the run because no step exists to
               carry it — before it did, this panel could only say that nothing
               had reported anything, which was a dead end. -->
          <div class="text-xs mt-1 whitespace-pre-wrap break-words">{run.error}</div>
          <div class="text-[11px] mt-1 opacity-70">
            This failure happened while starting the run, so no step ran.
          </div>
        {:else if failedStepErrors.length === 0}
          <div class="text-xs mt-1 opacity-80">
            The engine marked the run as failed but no step reported an error
            message. Open the step details below for logs.
          </div>
        {:else}
          <ul class="mt-2 space-y-1 text-xs list-disc pl-4">
            {#each failedStepErrors as msg}
              <li class="whitespace-pre-wrap break-words">{msg}</li>
            {/each}
          </ul>
        {/if}
      </Alert>
    </div>
  {/if}

  {#if loading && !run}
    <div class="flex-1 flex items-center justify-center"><Spinner size="lg" label="Loading run…" /></div>
  {:else if error && !run}
    <div class="flex-1 flex items-center justify-center p-6">
      <div class="max-w-md w-full">
        <Alert tone="error">
          <div class="font-medium">Couldn’t load this run</div>
          <div class="text-xs mt-1 opacity-80">{error}</div>
          <div class="mt-3">
            <Button size="xs" variant="secondary" onclick={() => loadData()} loading={loading}>Retry</Button>
          </div>
        </Alert>
      </div>
    </div>
  {:else}
    <div class="flex-1 flex flex-col lg:flex-row min-h-0">
      <!-- DAG -->
      <div class="flex-1 min-h-[300px] relative" role="application">
        {#if flowNodes.length > 0}
          <SvelteFlow
            nodes={flowNodes}
            edges={flowEdges}
            {nodeTypes}
            fitView
            connectionMode={ConnectionMode.Loose}
            nodesDraggable={false}
            nodesConnectable={false}
            elementsSelectable={false}
            panOnDrag={true}
            zoomOnScroll={true}
          >
            <Controls />
            <Background
              bgColor="var(--fw-canvas-bg)"
              patternColor="color-mix(in oklab, var(--color-surface-500) 50%, transparent)"
            />
          </SvelteFlow>
          <!-- Status legend: each node also carries a glyph prefix (✓/✕/●/⊘/○)
               so status isn't conveyed by border color alone (WCAG 1.4.1). -->
          <div class="absolute top-3 right-3 z-10 flex flex-wrap items-center gap-x-3 gap-y-1 rounded-md bg-surface-100-900/80 backdrop-blur-sm ring-1 ring-surface-200-800/80 px-2.5 py-1.5 text-[11px] text-surface-600-400">
            {#each statusLegend as item}
              {@const Glyph = item.icon}
              <span class="inline-flex items-center gap-1.5">
                <span class="w-2 h-2 rounded-full {toneDotClass(statusTone(item.status))}"></span>
                <Glyph size={12} class="text-surface-500" />
                {item.label}
              </span>
            {/each}
          </div>
        {:else}
          <div class="flex items-center justify-center h-full text-surface-500 text-sm">No workflow graph available</div>
        {/if}
      </div>

      <!-- Timeline -->
      <div class="w-full lg:w-[400px] shrink-0 border-t lg:border-t-0 lg:border-l border-surface-200-800 flex flex-col bg-surface-100-900/40">
        <div class="flex items-center justify-between px-4 h-10 border-b border-surface-200-800">
          <h2 class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">Step timeline <FieldHint id="runs.monitor_steps" /></h2>
          <div class="flex items-center gap-2">
            {#if isAdmin && run}
              <Button
                size="xs"
                variant="ghost"
                icon={Download}
                onclick={exportRunJson}
                title="Export the full run + steps to a JSON file (admin only)"
              >
                Export JSON
              </Button>
            {/if}
            <span class="text-xs text-surface-500 tabular-nums">{steps.length} step{steps.length === 1 ? '' : 's'}</span>
          </div>
        </div>
        <div class="relative flex-1 min-h-0">
        <div bind:this={timelineEl} onscroll={updateAtBottom} class="h-full overflow-y-auto p-2 space-y-1">
          {#if steps.length === 0}
            <div class="text-center py-8 text-surface-500 text-sm">
              {#if run?.status === 'running'}
                <Spinner size="sm" label="Waiting for steps…" />
              {:else}
                No steps recorded.
              {/if}
            </div>
          {:else}
            {#each steps as step (step.id)}
              {@const isExpanded = expandedSteps.has(step.id)}
              <div class="rounded-md border border-transparent hover:border-surface-300-700/50 transition-colors">
                <button
                  type="button"
                  onclick={() => toggleStep(step.id)}
                  class="w-full text-left flex items-start gap-2 p-2 hover:bg-surface-200-800/40 rounded-md transition-colors"
                >
                  {#if isExpanded}<ChevronDown size={12} class="mt-1.5 text-surface-500 shrink-0" />{:else}<ChevronRight size={12} class="mt-1.5 text-surface-500 shrink-0" />{/if}
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center justify-between gap-2">
                      <span class="text-sm text-surface-900-100 font-mono truncate">{step.node_id}</span>
                      <StatusBadge status={step.status} showDot={false} />
                    </div>
                    <div class="flex items-center gap-3 mt-1 text-[11px] text-surface-500">
                      <span>{formatTime(step.started_at)}</span>
                      <span class="font-mono tabular-nums">{formatDuration(step.started_at, step.completed_at)}</span>
                      {#if step.device_id}<span class="font-mono truncate max-w-[80px]">{truncate(step.device_id, 8)}</span>{/if}
                    </div>
                  </div>
                </button>

                {#if isExpanded}
                  <div class="ml-6 mr-2 mb-2 p-3 bg-surface-50-950 border border-surface-200-800 rounded-md text-xs space-y-3">
                    {#if step.error}
                      <div>
                        <div class="text-error-300 font-medium mb-1">Error</div>
                        <pre class="text-error-300 bg-error-500/10 rounded p-2 overflow-x-auto max-h-32 text-[11px] whitespace-pre-wrap">{step.error}</pre>
                      </div>
                    {/if}
                    {#if step.input_payload && Object.keys(step.input_payload).length > 0}
                      <div>
                        <div class="text-primary-300 font-medium mb-1">Input</div>
                        <pre class="text-surface-700-300 bg-surface-100-900 rounded p-2 overflow-x-auto max-h-40 text-[11px]">{JSON.stringify(step.input_payload, null, 2)}</pre>
                      </div>
                    {/if}
                    {#if step.output_payload && Object.keys(step.output_payload).length > 0}
                      <div>
                        <div class="text-success-300 font-medium mb-1">Output</div>
                        <pre class="text-surface-700-300 bg-surface-100-900 rounded p-2 overflow-x-auto max-h-40 text-[11px]">{JSON.stringify(step.output_payload, null, 2)}</pre>
                      </div>
                    {/if}
                    {#if step.logs}
                      <div>
                        <div class="text-surface-600-400 font-medium mb-1">Logs</div>
                        <pre class="text-surface-600-400 bg-surface-100-900 rounded p-2 overflow-x-auto max-h-32 text-[11px] whitespace-pre-wrap">{step.logs}</pre>
                      </div>
                    {/if}
                    <div class="flex gap-2 pt-1">
                      {#if step.status === 'failed' || step.status === 'failure'}
                        <Button size="xs" variant="danger" icon={Wrench} onclick={() => askAIToFix(step)}>Fix with AI</Button>
                      {/if}
                      {#if step.node_id !== '__start__' && step.node_id !== '__end__'}
                        <Button size="xs" variant="secondary" icon={Wand2} onclick={() => editWithAI(step)}>Edit with AI</Button>
                      {/if}
                    </div>
                    {#if step.worker_id}
                      <div class="text-surface-500 text-[11px]">Worker: <span class="text-surface-700-300 font-mono">{step.worker_id}</span></div>
                    {/if}
                  </div>
                {/if}
              </div>
            {/each}
          {/if}
        </div>
          {#if !atTimelineBottom && steps.length > 0}
            <!-- Shown only when the user has scrolled up; clicking re-pins the
                 timeline to the latest step (auto-scroll resumes). -->
            <button
              type="button"
              onclick={jumpToLatest}
              class="absolute bottom-3 left-1/2 -translate-x-1/2 inline-flex items-center gap-1.5 rounded-full bg-primary-500 text-white text-xs font-medium px-3 py-1.5 shadow-lg hover:bg-primary-400 transition-colors"
            >
              <ArrowDown size={12} /> Jump to latest
            </button>
          {/if}
        </div>
      </div>
    </div>
  {/if}
</div>

<style>
  :global(.svelte-flow) { background-color: var(--color-surface-50-950); }
</style>
