<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import {
    workflows, snippets, runs, integrations, mcpServers, exportWorkflow, errorMessage,
    type Snippet, type WorkflowNode, type WorkflowEdge,
    type StepRun, type IntegrationActionWithIntegration, type Integration, type Workflow,
    type WorkflowRun, type McpServer, type McpTool,
  } from '$lib/api/client';
  import { SvelteFlow, Controls, Background, ConnectionMode, type Node, type Edge, type Viewport } from '@xyflow/svelte';
  import '@xyflow/svelte/dist/style.css';
  import WorkflowNodeComponent from '$lib/components/workflow/DagNode.svelte';
  import SentinelNode from '$lib/components/workflow/SentinelNode.svelte';
  import JsonSchemaForm from '$lib/components/JsonSchemaForm.svelte';
  import DevicePicker from '$lib/components/DevicePicker.svelte';
  import TransformPlayground from '$lib/components/TransformPlayground.svelte';
  import WorkflowNodeDialog from './WorkflowNodeDialog.svelte';
  import {
    Button, IconButton, Badge, Dialog, Alert, Spinner, Input, FieldHint,
    StatusBadge, formatDateTime, formatDuration, truncate, confirm,
  } from '$lib/components/ui';
  import {
    ArrowLeft, Save, Play, ChevronDown, Eye, EyeOff, Download,
    Settings2, X, Maximize2, Minimize2, CalendarClock, Zap, Sparkles,
    ChevronRight, RefreshCw, Trash2, LayoutGrid, FlaskConical, Shield,
    MoreHorizontal, Layers, Wrench, MessageSquare, Copy, Pencil,
  } from 'lucide-svelte';
  import { autoLayout, needsAutoLayout } from '$lib/workflow/autoLayout';
  import { effectiveIdempotency } from '$lib/workflow/idempotency';
  import { buildSchemaDefaults } from '$lib/workflow/schemaDefaults';
  import { workflowSimulation } from '$lib/api/client';
  import { dirtyStore } from '$lib/stores/dirty.svelte';
  import { registerShortcut } from '$lib/utils/shortcuts';
  import { onDestroy } from 'svelte';

  let workflow = $state<Workflow | null>(null);
  let serviceList = $state<Snippet[]>([]);
  let serviceMap = $state<Map<string, Snippet>>(new Map());
  // Free-text filter for the snippets palette. Matches against name +
  // type so a user can find "ssh" snippets either by typing "ssh" (the
  // type) or part of the name they gave the row.
  let snippetSearch = $state('');
  const filteredServiceList = $derived.by(() => {
    // The seeded mcp_call snippet is dragged from the dedicated MCP palette
    // section (with its config), not as a bare snippet.
    const base = serviceList.filter((s) => s.type !== 'mcp_call');
    const q = snippetSearch.trim().toLowerCase();
    if (!q) return base;
    return base.filter(
      (s) =>
        s.name.toLowerCase().includes(q)
        || (s.type ?? '').toLowerCase().includes(q),
    );
  });

  // A snippet earns its place in the palette by having completed at least once
  // (`completed_run_count`, derived from step_runs). Everything else is a draft:
  // half-finished experiments and one-off attempts that never worked pile up
  // over time and make the reusable list useless.
  //
  // They are NOT hidden, they are moved. Hiding them outright would deadlock the
  // platform: a new snippet has zero runs, and the only way it can ever run is
  // by being dragged from this palette into a workflow. So unproven snippets
  // live in a collapsed section — out of the way, still reachable to be
  // exercised the first time.
  const provenServiceList = $derived(
    filteredServiceList.filter((s) => (s.completed_run_count ?? 0) > 0),
  );
  const unprovenServiceList = $derived(
    filteredServiceList.filter((s) => (s.completed_run_count ?? 0) === 0),
  );
  // Collapsed by default — that is the whole point. Auto-expands while a search
  // is active so a name-based search never appears to return nothing.
  let showUnproven = $state(false);
  const unprovenExpanded = $derived(showUnproven || snippetSearch.trim().length > 0);
  // The GUID of the seeded mcp_call snippet — an MCP node references it.
  // Picks THE built-in snippet backing a virtual node type, deterministically.
  //
  // A plain `.find(s => s.type === t)` was wrong: the list arrives ordered by
  // created_at DESCENDING, so it returned the NEWEST snippet of that type. More
  // than one can exist — the agent creates them, imports stub them, users copy
  // them — so the answer changed the moment a newer one appeared, and the same
  // node came out with a different snippet_id on each save. Two consecutive
  // saves of one workflow produced two different ids.
  //
  // Order of preference: the seeded built-in (verified, and named after its
  // type), then the oldest of that type. Both are stable across saves; the
  // oldest is also the one the backend's own seeder keeps when it dedupes.
  function builtinSnippetId(type: string): string | null {
    const ofType = serviceList.filter((s) => s.type === type);
    if (ofType.length === 0) return null;
    const seeded = ofType.find((s) => s.verified && s.name === type);
    if (seeded) return seeded.id;
    return [...ofType].sort((a, b) => (a.created_at ?? '').localeCompare(b.created_at ?? ''))[0].id;
  }

  const mcpCallSnippetId = $derived(builtinSnippetId('mcp_call'));
  // Integration-action nodes run through a seeded snippet of type
  // `integration_action`, the same way MCP nodes run through the seeded
  // `mcp_call` one. The canvas used to store the literal string
  // "integration_action" in snippet_id and save it verbatim — the engine only
  // accepts a GUID or one of its own sentinels there, so those workflows saved
  // cleanly and then died at run time with `invalid snippet_id`.
  const integrationActionSnippetId = $derived(builtinSnippetId('integration_action'));
  // Legacy marker still present in workflows saved before that fix.
  const LEGACY_ACTION_MARKER = 'integration_action';
  let loading = $state(true);
  let error = $state('');
  let saveStatus = $state<'' | 'saving' | 'saved' | 'error'>('');
  let saveError = $state('');
  // Run-data inspector dialog. We load the most recent run for this
  // workflow + its steps when the user clicks "Show run data", then show
  // everything in a modal so nothing gets hidden behind the DAG canvas.
  let showRunDataDialog = $state(false);
  let loadingRunData = $state(false);
  let runDataRun = $state<WorkflowRun | null>(null);
  let runDataSteps = $state<StepRun[]>([]);
  let runDataError = $state<string | null>(null);
  let runDataExpandedStep = $state<string | null>(null);

  let runStatus = $state<'' | 'starting' | 'started' | 'error'>('');
  let runId = $state('');
  let validationError = $state('');
  let leftSidebarOpen = $state(true);

  let integrationActions = $state<IntegrationActionWithIntegration[]>([]);
  let integrationActionMap = $state<Map<string, IntegrationActionWithIntegration>>(new Map());
  let expandedIntegrations = $state(new Set<string>());
  let expandedCategories = $state(new Set<string>());

  // MCP: cached tools + servers for the palette section and the node dialog.
  let mcpTools = $state<McpTool[]>([]);
  let mcpServerList = $state<McpServer[]>([]);
  let mcpServerMap = $state<Map<string, McpServer>>(new Map());
  let expandedMcpServers = $state(new Set<string>());

  let selectedNodeId = $state<string | null>(null);
  let viewport = $state<Viewport>({ x: 0, y: 0, zoom: 1 });
  const nodeTypes = { default: WorkflowNodeComponent, sentinel: SentinelNode } as Record<string, any>;
  let selectedNodeService = $state<Snippet | null>(null);
  let selectedNodeConfig = $state<Record<string, unknown>>({});
  let selectedNodeIsIntegrationAction = $state(false);
  let selectedNodeIsSentinel = $state(false);
  let selectedNodeIsSubflow = $state(false);
  let selectedNodeIsMcpCall = $state(false);
  let selectedIntegrationAction = $state<IntegrationActionWithIntegration | null>(null);
  let nodeDialogOpen = $state(false);

  let transformInput = $state('{}');
  let transformExpression = $state('');
  let loadingLastRun = $state(false);

  let showRunDialog = $state(false);

  // FR-022: dry-run simulation state. Null = never run; otherwise the
  // latest result. Loading flag keeps the button from re-firing while
  // the request is in flight.
  let showSimulateDialog = $state(false);
  let simulateLoading = $state(false);
  let simulateResult = $state<Awaited<ReturnType<typeof workflowSimulation.simulate>> | null>(null);
  let simulateError = $state<string | null>(null);

  async function runSimulate() {
    if (simulateLoading) return;
    simulateLoading = true;
    simulateError = null;
    showSimulateDialog = true;
    try {
      simulateResult = await workflowSimulation.simulate(page.params.id!);
    } catch (e) {
      simulateError = errorMessage(e);
    } finally {
      simulateLoading = false;
    }
  }
  let showExportMenu = $state(false);
  let showMoreMenu = $state(false);
  let selectedDevices = $state<string[]>([]);
  let runInput = $state<Record<string, unknown>>({});

  let edgeContextMenu = $state<{ x: number; y: number; edgeId: string } | null>(null);
  // Right-click menu for nodes. Carries the click coords (used for
  // positioning the floating menu) plus the node id and a `isSentinel`
  // flag so we can disable destructive actions on __start__ / __end__.
  let nodeContextMenu = $state<{
    x: number;
    y: number;
    nodeId: string;
    snippetId: string;
    isSentinel: boolean;
  } | null>(null);

  // Conditional-edge editor: triggered from the right-click menu, lets
  // the user author a non-empty condition expression in a Dialog rather
  // than the native window.prompt (which clashes with the rest of the
  // UI's surface styles).
  let showConditionalDialog = $state(false);
  let conditionalEdgeId = $state<string | null>(null);
  let conditionalExpr = $state('');
  let conditionalError = $state<string | null>(null);

  let flowNodes = $state<Node[]>([]);
  let flowEdges = $state<Edge[]>([]);
  let nodeCounter = 0;

  let integrationListForPicker = $state<Integration[]>([]);

  const isTransformNode = $derived(
    selectedNodeService?.type === 'transform' || selectedNodeService?.type === 'jmespath',
  );

  // Walk the DAG backwards from the selected node to collect every node
  // whose output could legitimately be referenced via {{ steps.X.output }}.
  // Includes transitively reachable upstream nodes — the resolver's
  // completedOutputs map populates as the DAG executes top-down, so any
  // ancestor is fair game. Sentinels (__start__/__end__) are excluded.
  const upstreamNodeIdsForSelected = $derived.by<string[]>(() => {
    if (!selectedNodeId) return [];
    const adjacency = new Map<string, string[]>();
    for (const e of flowEdges) {
      const list = adjacency.get(e.target) ?? [];
      list.push(e.source);
      adjacency.set(e.target, list);
    }
    const seen = new Set<string>();
    const stack = [selectedNodeId];
    while (stack.length > 0) {
      const id = stack.pop()!;
      for (const src of adjacency.get(id) ?? []) {
        if (seen.has(src)) continue;
        seen.add(src);
        stack.push(src);
      }
    }
    seen.delete('__start__');
    seen.delete('__end__');
    return [...seen];
  });

  // Snapshot-based dirty tracking.
  //
  // The previous approach (`$effect` watching flowNodes/flowEdges array
  // reassignment) produced false positives because xyflow re-binds the
  // arrays on selection, hover and dimension events too — not only on
  // meaningful mutations. We now compute a deterministic snapshot of the
  // *persisted* fields (snippet_id, rounded position, config_overrides,
  // edge source/target/type/condition/handles) and compare against the
  // last-saved baseline. Selection and visual-only updates leave the
  // snapshot identical, so dirty stays clean until the user actually
  // edits something.
  let editorReady = false;
  let baselineSnapshot = '';

  function captureSnapshot(): string {
    const n = flowNodes.map((node) => ({
      id: node.id,
      sid: (node.data?.snippetId as string | undefined) ?? '',
      x: Math.round(node.position.x),
      y: Math.round(node.position.y),
      cfg: (node.data?.configOverrides as Record<string, unknown> | undefined) ?? {},
    }));
    const e = flowEdges.map((edge) => ({
      s: edge.source,
      t: edge.target,
      sh: edge.sourceHandle ?? null,
      th: edge.targetHandle ?? null,
      ty: (edge.data?.edgeType as string | undefined) ?? 'success',
      c: (edge.data?.condition as string | null | undefined) ?? null,
    }));
    return JSON.stringify({ n, e });
  }

  onMount(() => {
    Promise.all([loadWorkflow(), loadServices(), loadIntegrationActions(), loadIntegrationsForPicker(), loadMcpTools()]);
    document.addEventListener('click', closeEdgeMenu);
    const offSave = registerShortcut({
      id: 'workflow-save',
      label: 'Save workflow',
      key: 's',
      ctrlOrMeta: true,
      handler: () => { void saveWorkflow(); },
    });
    return () => {
      document.removeEventListener('click', closeEdgeMenu);
      offSave();
    };
  });

  // Compare the live snapshot against the baseline on every reactive read
  // of flowNodes/flowEdges. captureSnapshot() reads both arrays so Svelte
  // tracks them as dependencies automatically.
  //
  // IMPORTANT: we do NOT read `dirtyStore.dirty` here. Doing so would
  // make this effect re-fire when an *external* caller (the layout's
  // beforeNavigate handler) sets dirty=false — and since the snapshot
  // still differs from the baseline at that moment, the effect would
  // immediately re-mark dirty, cancelling the navigation in a loop.
  // The markDirty/markClean methods are idempotent so unconditional
  // calls are safe.
  $effect(() => {
    const current = captureSnapshot();
    if (!editorReady) return;
    if (current !== baselineSnapshot) dirtyStore.markDirty();
    else dirtyStore.markClean();
  });

  onDestroy(() => {
    // The dirty flag is editor-local — clear it on unmount so the next
    // route doesn't inherit "unsaved changes" warnings from this one.
    dirtyStore.markClean();
  });

  function closeEdgeMenu() { edgeContextMenu = null; }

  async function loadWorkflow() {
    loading = true;
    error = '';
    try {
      workflow = await workflows.get(page.params.id!);
      buildFlow();
    } catch (e) {
      error = errorMessage(e);
          toast.fromError(e, 'Couldn’t load workflow');
    } finally {
      loading = false;
    }
  }

  async function loadServices() {
    try {
      const res = await snippets.list(200, 0);
      serviceList = res.data;
      serviceMap = new Map(serviceList.map((s) => [s.id, s]));
      // Rebuild the flow now that service metadata is known. onMount fires
      // loadWorkflow + loadServices in parallel, so the first buildFlow
      // often runs with an empty serviceMap — that's why nodes used to
      // appear as "node1", "node2" with no service name below. Rebuilding
      // here guarantees the enriched view once both have loaded.
      if (workflow) buildFlow();
    } catch (e) {
      console.error('Failed to load services:', e);
    }
  }

  async function loadIntegrationActions() {
    try {
      integrationActions = await integrations.listAllActions();
      integrationActionMap = new Map(integrationActions.map((a) => [a.id, a]));
    } catch (e) {
      console.error('Failed to load integration actions:', e);
    }
  }

  async function loadIntegrationsForPicker() {
    try {
      const res = await integrations.list();
      integrationListForPicker = res.data;
    } catch {
      // Integration list is optional UX — fall back to empty so the picker shows nothing.
    }
  }

  async function loadMcpTools() {
    try {
      const [tools, serversRes] = await Promise.all([
        mcpServers.listAllTools(),
        mcpServers.list(200, 0),
      ]);
      mcpTools = tools;
      mcpServerList = serversRes.data;
      mcpServerMap = new Map(mcpServerList.map((s) => [s.mcp_server_id, s]));
    } catch {
      // MCP is optional / permission-gated — fall back to empty.
    }
  }

  function groupedIntegrationActions(): Map<string, Map<string, IntegrationActionWithIntegration[]>> {
    const grouped = new Map<string, Map<string, IntegrationActionWithIntegration[]>>();
    for (const action of integrationActions) {
      if (!grouped.has(action.integration_name)) grouped.set(action.integration_name, new Map());
      const integrationGroup = grouped.get(action.integration_name)!;
      const cat = action.category || 'general';
      if (!integrationGroup.has(cat)) integrationGroup.set(cat, []);
      integrationGroup.get(cat)!.push(action);
    }
    return grouped;
  }

  function toggleIntegration(name: string) {
    const next = new Set(expandedIntegrations);
    if (next.has(name)) next.delete(name); else next.add(name);
    expandedIntegrations = next;
  }

  function toggleCategory(key: string) {
    const next = new Set(expandedCategories);
    if (next.has(key)) next.delete(key); else next.add(key);
    expandedCategories = next;
  }

  function groupedMcpTools(): Map<string, McpTool[]> {
    const grouped = new Map<string, McpTool[]>();
    for (const t of mcpTools) {
      if (!grouped.has(t.mcp_server_id)) grouped.set(t.mcp_server_id, []);
      grouped.get(t.mcp_server_id)!.push(t);
    }
    return grouped;
  }

  function mcpServerName(id: string): string {
    return mcpServerMap.get(id)?.name ?? 'MCP server';
  }

  function toggleMcpServer(id: string) {
    const next = new Set(expandedMcpServers);
    if (next.has(id)) next.delete(id); else next.add(id);
    expandedMcpServers = next;
  }

  function getEdgeColor(type: string): string {
    switch (type) {
      case 'failure': return 'rgb(239 68 68)';
      case 'always': return 'rgb(168 85 247)';
      case 'conditional': return 'rgb(245 158 11)';
      default: return 'rgb(34 197 94)';
    }
  }

  function truncateExpr(expr: string): string {
    return expr.length > 40 ? expr.slice(0, 37) + '…' : expr;
  }

  const methodTone: Record<string, string> = {
    GET: 'bg-success-500/15 text-success-300',
    POST: 'bg-primary-500/15 text-primary-300',
    PUT: 'bg-warning-500/15 text-warning-300',
    PATCH: 'bg-warning-500/15 text-warning-300',
    DELETE: 'bg-error-500/15 text-error-300',
  };

  function buildFlow() {
    // Pause dirty tracking while we rebuild the graph from server data
    // (load + service-metadata enrichment may both call buildFlow). The
    // microtask at the bottom re-arms it.
    editorReady = false;
    if (!workflow) return;
    // S13.6: which nodes already have a `failure` edge feeding a
    // compensation step. Combined with `idempotency` to decide whether
    // a mutating node gets a yellow warning (no compensation wired)
    // or no badge (compensated).
    const nodesWithFailureEdge = new Set<string>(
      (workflow.edges || [])
        .filter((e: WorkflowEdge) => e.type === 'failure')
        .map((e: WorkflowEdge) => e.source),
    );
    // What makes a node a sentinel is its SNIPPET id, not its node id.
    //
    // The engine has always said so — Dag, the exporters and the simulator all
    // test `snippet_id`, and the node id is free-form. This canvas tested the
    // node id instead. That holds for anything it saved itself (it writes
    // `__start__` as the node id too) and fails for everything else: a bundle
    // imported from Nashira, and FlowWeaver's own load-test seeds, which write
    // `{"id":"start","snippet_id":"__start__"}`.
    //
    // The symptom is not a missing sentinel, it is two: the real one renders as
    // a task node with no service behind it, and then the `hasStart` check below
    // adds a fresh, unconnected `__start__` next to it.
    //
    // So recognise by snippet_id and rename to the canvas's internal ids, which
    // the rest of this file and the save path already assume. Only the first of
    // each is renamed — a graph with two `__start__` snippets is malformed, and
    // collapsing them onto one id would hide that instead of showing it.
    const sentinelRename = new Map<string, string>();
    for (const marker of ['__start__', '__end__'] as const) {
      const first = (workflow.nodes || []).find((n: WorkflowNode) => n.snippet_id === marker);
      if (first && first.id !== marker) sentinelRename.set(first.id, marker);
    }
    const canvasId = (id: string) => sentinelRename.get(id) ?? id;

    flowNodes = (workflow.nodes || []).map((n: WorkflowNode) => {
      const svc = serviceMap.get(n.snippet_id);
      const isIntegrationAction =
        n.snippet_id === LEGACY_ACTION_MARKER || svc?.type === 'integration_action';
      const isStart = n.snippet_id === '__start__' || n.id === '__start__';
      const isEnd = n.snippet_id === '__end__' || n.id === '__end__';
      const overrides = (n.config_overrides || {}) as Record<string, unknown>;

      if (isStart) {
        return {
          id: '__start__',
          position: { x: n.x || 0, y: n.y || 0 },
          data: { label: 'Start', kind: 'start', nodeId: 'Start', serviceName: '', subtitle: '', snippetId: '__start__', serviceType: 'start', configOverrides: {} },
          type: 'sentinel',
          deletable: false,
          // Keep the outer xyflow wrapper transparent — SentinelNode
          // paints its own circular disc + handles.
          style: 'width: 56px; height: 56px; padding: 0; background: transparent; border: none; box-shadow: none;',
        };
      }
      if (isEnd) {
        return {
          id: '__end__',
          position: { x: n.x || 0, y: n.y || 0 },
          data: { label: 'End', kind: 'end', nodeId: 'End', serviceName: '', subtitle: '', snippetId: '__end__', serviceType: 'end', configOverrides: {} },
          type: 'sentinel',
          deletable: false,
          style: 'width: 56px; height: 56px; padding: 0; background: transparent; border: none; box-shadow: none;',
        };
      }

      // Build the three visible slots independently so the node box
      // always shows: the node id (user-facing identifier), the service
      // name (what the node does), and the service type (how it runs).
      // Integration actions are a virtual service — we synthesize a label
      // from the HTTP method + path the operator configured.
      let serviceName = svc?.name ?? '';
      let serviceType = svc?.type ?? '';
      let subtitle = svc?.description ? svc.description.slice(0, 60) : '';

      if (isIntegrationAction) {
        serviceName = `${overrides.method || 'GET'} ${overrides.path || ''}`.trim();
        serviceType = 'integration_action';
        subtitle = (overrides.integration_name as string) || '';
      }

      // MCP call: a real seeded snippet, so svc resolves — synthesize a nicer
      // label from the configured tool + server.
      if (svc?.type === 'mcp_call') {
        serviceName = (overrides.tool_name as string) || svc.name;
        serviceType = 'mcp_call';
        const srvId = overrides.mcp_server_id as string | undefined;
        subtitle = srvId ? mcpServerName(srvId) : 'Click to choose a server + tool';
      }

      // Subflow nodes don't have a backing snippet — they reference a
      // workflow id. Pull the cached display name out of the overrides
      // so the canvas shows e.g. "net.drain_port" instead of falling
      // back to the empty svc fields.
      if (n.snippet_id === 'subflow') {
        const subflowName = (overrides.subflow_name as string | undefined)?.trim();
        const subflowEnv = (overrides.subflow_environment as string | undefined)?.trim();
        serviceName = subflowName || 'Subflow (pick one)';
        serviceType = 'subflow';
        subtitle = subflowEnv
          ? `subflow · ${subflowEnv}`
          : (subflowName ? 'subflow' : 'Click to choose the target subflow');
      }

      nodeCounter = Math.max(nodeCounter, parseInt(n.id.replace(/\D/g, '') || '0') + 1);
      // S13.6: surface the effective rollback policy on the canvas
      // so authors can see at a glance which nodes block rollback.
      // Floor + per-snippet override are merged by the shared helper
      // that the analyzer mirrors on the backend. A `requires_compensation`
      // node is downgraded to "compensated" when it already has an
      // outgoing failure edge so the canvas badge matches the analyzer.
      let idempotency: string = svc
        ? effectiveIdempotency(svc.type, svc.idempotency)
        : 'requires_compensation';
      if (idempotency === 'requires_compensation' && nodesWithFailureEdge.has(n.id)) {
        idempotency = 'compensated';
      }
      return {
        id: n.id,
        position: { x: n.x || 0, y: n.y || 0 },
        data: {
          // `label` stays for back-compat with older consumers (monitor,
          // exports); the custom WorkflowNode component prefers nodeId.
          label: n.id,
          nodeId: n.id,
          serviceName,
          subtitle,
          description: svc?.description || '',
          // Heal the legacy editor marker on load. A workflow saved before the
          // canvas wrote real ids still carries "integration_action" here, and
          // the reference validator now rejects it — so without this, opening
          // such a workflow and saving it would bounce with the very error it
          // is telling you to fix, and there would be no way out of the editor.
          // Resolving it here means opening and saving is the fix.
          //
          // `integrationActionSnippetId` is null until the snippet list loads;
          // buildFlow re-runs once it has (see loadServices), so the marker is
          // kept rather than blanked on that first pass.
          snippetId: isIntegrationAction && integrationActionSnippetId
            ? integrationActionSnippetId
            : n.snippet_id,
          serviceType,
          idempotency,
          configOverrides: overrides,
          // Tells DagNode to paint its own theme-reactive box. Monitor
          // omits this so the status-colored wrapper keeps priority.
          variant: 'editor',
        },
        type: 'default',
        // Pin the outer xyflow wrapper to the exact width DagNode renders.
        // We used to rely on a global CSS override, but xyflow's dist
        // stylesheet loads after app.css and can silently win depending on
        // build order — the handles ended up aligned to the old 150 px
        // default while the content was 180 px. Inline style always wins.
        // Padding/background/border stay empty so the inner DagNode box is
        // the only visible rectangle.
        style: 'width: 180px; padding: 0; background: transparent; border: none; box-shadow: none;',
        selectable: true,
        deletable: true,
      };
    });

    const hasStart = flowNodes.some((n) => n.id === '__start__');
    const hasEnd = flowNodes.some((n) => n.id === '__end__');
    if (!hasStart) {
      const minX = flowNodes.length > 0 ? Math.min(...flowNodes.map((n) => n.position.x)) : 200;
      const avgY = flowNodes.length > 0 ? flowNodes.reduce((s, n) => s + n.position.y, 0) / flowNodes.length : 200;
      flowNodes = [{
        id: '__start__',
        position: { x: minX - 200, y: avgY },
        data: { label: 'Start', kind: 'start', subtitle: '', snippetId: '__start__', serviceType: 'start', configOverrides: {} },
        type: 'sentinel',
        deletable: false,
        style: 'width: 56px; height: 56px; padding: 0; background: transparent; border: none; box-shadow: none;',
      }, ...flowNodes];
    }
    if (!hasEnd) {
      const maxX = flowNodes.length > 0 ? Math.max(...flowNodes.map((n) => n.position.x)) : 200;
      const avgY = flowNodes.length > 0 ? flowNodes.reduce((s, n) => s + n.position.y, 0) / flowNodes.length : 200;
      flowNodes = [...flowNodes, {
        id: '__end__',
        position: { x: maxX + 200, y: avgY },
        data: { label: 'End', kind: 'end', subtitle: '', snippetId: '__end__', serviceType: 'end', configOverrides: {} },
        type: 'sentinel',
        deletable: false,
        style: 'width: 56px; height: 56px; padding: 0; background: transparent; border: none; box-shadow: none;',
      }];
    }

    // Drop duplicates on load. Connecting two nodes used to create TWO
    // identical edges (SvelteFlow added one and the old `onconnect` handler
    // appended another — see onBeforeConnect), so workflows saved before that
    // fix carry every edge twice. Deduping here cleans the canvas and, because
    // the save serialises what the canvas holds, the next save writes the
    // repaired set. Keyed on the endpoints + handles + type, which is exactly
    // what makes two edges the same edge to the engine.
    const seenEdgeKeys = new Set<string>();
    flowEdges = (workflow.edges || []).filter((e: WorkflowEdge) => {
      const key = `${e.source}|${e.source_handle ?? ''}|${e.target}|${e.target_handle ?? ''}|${e.type}`;
      if (seenEdgeKeys.has(key)) return false;
      seenEdgeKeys.add(key);
      return true;
    }).map((e: WorkflowEdge) => {
      // An edge names its endpoints by the node ids in the STORED graph, so a
      // renamed sentinel has to be followed here too. Renaming the node without
      // this leaves the edge pointing at an id the canvas no longer has, and
      // xyflow silently drops it — the graph would arrive with its ends
      // disconnected, which is worse than the duplicate sentinel it replaced.
      const source = canvasId(e.source);
      const target = canvasId(e.target);
      return {
      // Include handle ids in the edge id so the same pair of nodes can
      // hold multiple edges that enter/exit different sides without id
      // collisions (xyflow dedupes by id).
      id: `e-${source}${e.source_handle ? ':' + e.source_handle : ''}-${target}${e.target_handle ? ':' + e.target_handle : ''}-${e.type}`,
      source,
      target,
      sourceHandle: e.source_handle ?? undefined,
      targetHandle: e.target_handle ?? undefined,
      label: e.type === 'success' ? '' : e.type === 'conditional' && e.condition ? `if ${truncateExpr(e.condition)}` : e.type,
      animated: e.type === 'success',
      style: `stroke: ${getEdgeColor(e.type)}; stroke-width: 2;`,
      labelStyle: e.type !== 'success' ? 'fill: rgb(156 163 175); font-size: 10px;' : '',
      data: { edgeType: e.type, condition: e.condition ?? null },
      };
    });

    // Workflows created by the AI agent (or the old "0/0 is fine" skill
    // example) land with every node stacked on the same point. Dagre
    // re-places them into a readable layered layout and assigns proper
    // edge handles before the canvas paints, so the user never sees the
    // ugly-edges state. The computed positions stay in memory only —
    // saving them requires clicking "Auto-layout" (which calls
    // applyAutoLayout below) so we don't silently mutate a user's
    // hand-tuned diagram on every reload.
    if (needsAutoLayout(flowNodes, flowEdges)) {
      const laid = autoLayout(flowNodes, flowEdges);
      flowNodes = laid.nodes;
      flowEdges = laid.edges;
    }
    // The build mutated flowNodes/flowEdges — flush the dirty flag and
    // arm the change tracker. Subsequent edits will now toggle the flag.
    queueMicrotask(() => {
      baselineSnapshot = captureSnapshot();
      dirtyStore.markClean();
      editorReady = true;
    });
  }

  // Toolbar action: re-run the layout and immediately commit to the
  // current state (positions + handles become part of the next save).
  // Separate from the autorun on load — user opts in explicitly, then
  // clicks Save to persist.
  function applyAutoLayout() {
    if (flowNodes.length === 0) return;
    const laid = autoLayout(flowNodes, flowEdges);
    flowNodes = laid.nodes;
    flowEdges = laid.edges;
    toast.success('Auto-layout applied', {
      description: 'Click Save to keep these positions.',
    });
  }

  // Drag and drop
  function onDragStart(e: DragEvent, svc: Snippet) {
    if (!e.dataTransfer) return;
    e.dataTransfer.setData('application/svelteflow', JSON.stringify({
      type: 'service', serviceId: svc.id, serviceName: svc.name, serviceType: svc.type,
    }));
    e.dataTransfer.effectAllowed = 'move';
  }

  function onIntegrationActionDragStart(e: DragEvent, action: IntegrationActionWithIntegration) {
    if (!e.dataTransfer) return;
    e.dataTransfer.setData('application/svelteflow', JSON.stringify({
      type: 'integration_action',
      actionId: action.id,
      integrationId: action.integration_id,
      integrationName: action.integration_name,
      method: action.method,
      path: action.path,
    }));
    e.dataTransfer.effectAllowed = 'move';
  }

  // Subflow nodes have no concrete snippet of their own — they reference
  // another workflow via config_overrides.subflow_id, which the user
  // picks in WorkflowNodeDialog after dropping the node. The literal
  // snippet_id "subflow" is one of the sentinels the engine recognises.
  function onSubflowDragStart(e: DragEvent) {
    if (!e.dataTransfer) return;
    e.dataTransfer.setData('application/svelteflow', JSON.stringify({
      type: 'subflow',
    }));
    e.dataTransfer.effectAllowed = 'move';
  }

  // MCP call: references the seeded mcp_call snippet by GUID and stores
  // the chosen server + tool in config_overrides. Dragged from the MCP palette.
  function onMcpToolDragStart(e: DragEvent, tool: McpTool) {
    if (!e.dataTransfer || !mcpCallSnippetId) return;
    e.dataTransfer.setData('application/svelteflow', JSON.stringify({
      type: 'mcp_call',
      snippetId: mcpCallSnippetId,
      mcpServerId: tool.mcp_server_id,
      toolName: tool.name,
    }));
    e.dataTransfer.effectAllowed = 'move';
  }

  function onDragOver(e: DragEvent) { e.preventDefault(); if (e.dataTransfer) e.dataTransfer.dropEffect = 'move'; }

  function onDrop(e: DragEvent) {
    e.preventDefault();
    if (!e.dataTransfer) return;
    const raw = e.dataTransfer.getData('application/svelteflow');
    if (!raw) return;
    const data = JSON.parse(raw);
    const nodeId = `node-${++nodeCounter}`;
    const flowEl = (e.currentTarget as HTMLElement)?.closest('.svelte-flow');
    const bounds = flowEl?.getBoundingClientRect();
    const screenX = bounds ? e.clientX - bounds.left : e.clientX;
    const screenY = bounds ? e.clientY - bounds.top : e.clientY;
    const x = (screenX - viewport.x) / viewport.zoom;
    const y = (screenY - viewport.y) / viewport.zoom;

    let newNode: Node;
    if (data.type === 'integration_action') {
      newNode = {
        id: nodeId,
        position: { x, y },
        data: {
          label: nodeId,
          nodeId,
          serviceName: `${data.method} ${data.path}`,
          subtitle: data.integrationName,
          // Real snippet id, not the marker — this is what gets saved.
          snippetId: integrationActionSnippetId ?? LEGACY_ACTION_MARKER,
          serviceType: 'integration_action',
          configOverrides: { integration_id: data.integrationId, action_id: data.actionId, integration_name: data.integrationName, method: data.method, path: data.path, params: {}, query: {}, body: {} },
        },
        type: 'default',
      };
    } else if (data.type === 'mcp_call') {
      newNode = {
        id: nodeId,
        position: { x, y },
        data: {
          label: nodeId,
          nodeId,
          serviceName: data.toolName,
          subtitle: mcpServerName(data.mcpServerId),
          snippetId: data.snippetId,
          serviceType: 'mcp_call',
          configOverrides: { mcp_server_id: data.mcpServerId, tool_name: data.toolName, arguments: {} },
        },
        type: 'default',
      };
    } else if (data.type === 'subflow') {
      newNode = {
        id: nodeId,
        position: { x, y },
        data: {
          label: 'Subflow',
          nodeId,
          serviceName: 'Subflow (pick one)',
          subtitle: 'Click to choose the target subflow',
          snippetId: 'subflow',
          serviceType: 'subflow',
          // Empty subflow_workflow_id → the dialog opens in "needs config"
          // mode and forces the user to pick a target before save. The
          // key MUST match what DagParser/WorkflowExecutor read at
          // enqueue time; using `subflow_id` here would persist but fail
          // when the workflow runs.
          configOverrides: { subflow_workflow_id: '', inputs: {} },
        },
        type: 'default',
      };
    } else {
      newNode = {
        id: nodeId,
        position: { x, y },
        data: {
          label: nodeId,
          nodeId,
          serviceName: data.serviceName,
          snippetId: data.serviceId,
          serviceType: data.serviceType,
          configOverrides: {},
        },
        type: 'default',
      };
    }
    flowNodes = [...flowNodes, newNode];
    validationError = '';
  }

  function parsePathParams(path: string): string[] {
    const matches = path.match(/\{([^}]+)\}/g);
    if (!matches) return [];
    return matches.map((m) => m.slice(1, -1));
  }

  // Opens the node editor dialog. Hydrates every piece of state the
  // dialog needs (service metadata, integration action, transform kind,
  // sentinel flag) so the modal can run standalone without re-reading
  // from the SvelteFlow graph.
  function onNodeClick({ node }: { node: Node }) {
    selectedNodeId = node.id;
    const defId = node.data.snippetId as string | undefined;
    const isIntegrationAction = node.data.serviceType === 'integration_action';
    const isSentinel = defId === '__start__' || defId === '__end__';
    // Subflow nodes carry the literal id "subflow"; the actual target is
    // resolved at runtime from config_overrides.subflow_id.
    const isSubflow = defId === 'subflow';

    selectedNodeIsIntegrationAction = isIntegrationAction;
    selectedNodeIsSentinel = isSentinel;
    selectedNodeIsSubflow = isSubflow;
    selectedNodeIsMcpCall = false;
    selectedNodeConfig = (node.data.configOverrides as Record<string, unknown>) || {};

    if (isIntegrationAction) {
      selectedNodeService = null;
      const actionId = selectedNodeConfig.action_id as string | undefined;
      selectedIntegrationAction = actionId ? (integrationActionMap.get(actionId) || null) : null;
    } else {
      selectedIntegrationAction = null;
      const svc = defId ? serviceMap.get(defId) : null;
      selectedNodeService = svc || null;
      selectedNodeIsMcpCall = svc?.type === 'mcp_call';
    }

    nodeDialogOpen = true;
  }

  function onPaneClick() {
    selectedNodeId = null;
    selectedNodeService = null;
    selectedIntegrationAction = null;
    selectedNodeIsIntegrationAction = false;
    selectedNodeIsSentinel = false;
    selectedNodeIsSubflow = false;
    selectedNodeIsMcpCall = false;
    // Close any floating context menu — clicking the pane is the
    // canonical "I'm done" gesture in SvelteFlow.
    edgeContextMenu = null;
    nodeContextMenu = null;
  }

  // Called by the dialog when the user applies node-scope changes.
  // Writes back into the in-memory flowNodes[] — the workflow Save
  // button is still what persists to the backend.
  function onDialogNodeApply(config: Record<string, unknown>) {
    if (!selectedNodeId) return;
    selectedNodeConfig = config;
    flowNodes = flowNodes.map((n) => {
      if (n.id !== selectedNodeId) return n;
      const nextData: Record<string, unknown> = { ...n.data, configOverrides: config };
      // Subflow nodes show the chosen workflow's name on the canvas.
      // The dialog stores subflow_name / subflow_environment alongside
      // subflow_id so we don't have to look the workflow up again here.
      if (n.data.snippetId === 'subflow') {
        const subflowName = (config.subflow_name as string | undefined)?.trim();
        const subflowEnv = (config.subflow_environment as string | undefined)?.trim();
        nextData.serviceName = subflowName || 'Subflow (pick one)';
        nextData.subtitle = subflowEnv
          ? `subflow · ${subflowEnv}`
          : (subflowName ? 'subflow' : 'Click to choose the target subflow');
        nextData.label = subflowName || 'Subflow';
      }
      return { ...n, data: nextData };
    });
  }

  // Called by the dialog after a successful Service / Schemas save.
  // Refreshes the local serviceMap so the other nodes using this same
  // service pick up the new metadata without a page reload.
  function onDialogServiceRefresh(updated: Snippet) {
    const next = new Map(serviceMap);
    next.set(updated.id, updated);
    serviceMap = next;
    serviceList = serviceList.map((s) => (s.id === updated.id ? updated : s));
    if (selectedNodeService?.id === updated.id) {
      selectedNodeService = updated;
    }
  }

  function updateNodeConfig(detail: Record<string, unknown>) {
    selectedNodeConfig = detail;
    if (!selectedNodeId) return;
    flowNodes = flowNodes.map((n) =>
      n.id === selectedNodeId ? { ...n, data: { ...n.data, configOverrides: detail } } : n,
    );
  }

  function updateIntegrationParam(key: string, value: string) {
    const params = { ...((selectedNodeConfig.params as Record<string, string>) || {}), [key]: value };
    updateNodeConfig({ ...selectedNodeConfig, params });
  }

  function updateIntegrationQuery(key: string, value: string) {
    const query = { ...((selectedNodeConfig.query as Record<string, string>) || {}), [key]: value };
    updateNodeConfig({ ...selectedNodeConfig, query });
  }

  function updateIntegrationBody(value: string) {
    try {
      const parsed = JSON.parse(value);
      updateNodeConfig({ ...selectedNodeConfig, body: parsed });
    } catch {
      // Keep raw string while user types invalid JSON; will become valid on next keystroke.
    }
  }

  function saveTransformExpression(expr: string) {
    if (!selectedNodeId) return;
    updateNodeConfig({ ...selectedNodeConfig, expression: expr });
    transformExpression = expr;
  }

  async function loadLastRunData() {
    if (!selectedNodeId || !workflow) return;
    loadingLastRun = true;
    try {
      const runsResp = await runs.list(10, 0);
      const recentRuns = runsResp.data.filter((r) => r.workflow_id === page.params.id);
      if (recentRuns.length === 0) { error = 'No previous runs found for this workflow'; return; }
      const stepsData = await runs.steps(recentRuns[0].id);
      const stepsList: StepRun[] = stepsData.data;
      const upstreamEdge = workflow.edges.find((e: WorkflowEdge) => e.target === selectedNodeId);
      if (!upstreamEdge) { error = 'No upstream edge found for this node'; return; }
      const upstreamStep = stepsList.find((s) => s.node_id === upstreamEdge.source);
      if (!upstreamStep) { error = 'Upstream step not found in last run'; return; }
      transformInput = JSON.stringify(upstreamStep.output_payload, null, 2);
    } catch (e) {
      error = errorMessage(e);
          toast.fromError(e, 'Action failed');
    } finally {
      loadingLastRun = false;
    }
  }

  function onEdgeContextMenu({ edge, event }: { edge: Edge; event: MouseEvent }) {
    event.preventDefault();
    edgeContextMenu = { x: event.clientX, y: event.clientY, edgeId: edge.id };
    nodeContextMenu = null;
  }

  // Right-click on a node opens the custom action menu. The default
  // browser context menu would clobber it, so preventDefault is the
  // first thing we do. We also drop any open edge menu so only one
  // floating menu is visible at a time.
  function onNodeContextMenu({ node, event }: { node: Node; event: MouseEvent }) {
    event.preventDefault();
    const snippetId = (node.data?.snippetId as string | undefined) ?? '';
    const isSentinel = snippetId === '__start__' || snippetId === '__end__';
    nodeContextMenu = {
      x: event.clientX,
      y: event.clientY,
      nodeId: node.id,
      snippetId,
      isSentinel,
    };
    edgeContextMenu = null;
  }

  function closeNodeMenu() { nodeContextMenu = null; }

  // Edit: same path as left-click — populates the dialog state and
  // opens the node editor. Re-use onNodeClick so the two entry points
  // can never drift.
  function editFromMenu() {
    if (!nodeContextMenu) return;
    const target = flowNodes.find((n) => n.id === nodeContextMenu!.nodeId);
    if (target) onNodeClick({ node: target });
    nodeContextMenu = null;
  }

  // Duplicate: clone the node with a fresh id and a small position
  // offset so the copy is visible next to the original. Edges are not
  // copied — the user wires the duplicate manually.
  function duplicateFromMenu() {
    if (!nodeContextMenu) return;
    const src = flowNodes.find((n) => n.id === nodeContextMenu!.nodeId);
    if (!src) { nodeContextMenu = null; return; }
    if (nodeContextMenu.isSentinel) { nodeContextMenu = null; return; }
    const newId = `node-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
    const offset = 40;
    flowNodes = [
      ...flowNodes,
      {
        ...src,
        id: newId,
        position: { x: (src.position?.x ?? 0) + offset, y: (src.position?.y ?? 0) + offset },
        data: {
          ...(src.data as Record<string, unknown>),
          // Reset measured rect so SvelteFlow re-measures the clone.
        },
        selected: false,
        dragging: false,
      },
    ];
    nodeContextMenu = null;
  }

  // Ask AI: open the chat with a generic node-scoped context. The
  // assistant resolves `workflow:<wfid>` via its tool calls (get_workflow_details);
  // we add `node:<nodeid>` so it focuses on that step.
  function askAiFromMenu() {
    if (!nodeContextMenu) return;
    const wfId = page.params.id ?? '';
    const ctx = `workflow:${wfId}:node:${nodeContextMenu.nodeId}`;
    nodeContextMenu = null;
    goto('/ai/chat?context=' + encodeURIComponent(ctx));
  }

  // Fix with AI: structured context the chat page auto-sends as a
  // "fix this" prompt. Matches the shape used by runs/monitor's
  // editWithAI so the chat's existing buildFixPrompt understands it.
  function fixWithAiFromMenu() {
    if (!nodeContextMenu) return;
    const src = flowNodes.find((n) => n.id === nodeContextMenu!.nodeId);
    if (!src) { nodeContextMenu = null; return; }
    const data = (src.data ?? {}) as Record<string, unknown>;
    const context = {
      node_id: src.id,
      service_id: (data.snippetId as string | undefined) ?? '',
      workflow_id: page.params.id ?? '',
      mode: 'edit' as const,
      config: data.configOverrides ?? {},
    };
    nodeContextMenu = null;
    goto('/ai/chat?fix=' + encodeURIComponent(JSON.stringify(context)));
  }

  // Delete: route through the same handler SvelteFlow's keyboard-
  // delete path uses so node + incident edges + dialog state all
  // teardown together.
  async function deleteFromMenu() {
    if (!nodeContextMenu) return;
    if (nodeContextMenu.isSentinel) { nodeContextMenu = null; return; }
    const target = flowNodes.find((n) => n.id === nodeContextMenu!.nodeId);
    nodeContextMenu = null;
    if (!target) return;
    // This path doesn't go through xyflow's deleteElements, so run the same
    // confirm gate the keyboard path gets via onBeforeDelete.
    if (!(await confirmNodeDeletion(new Set([target.id])))) return;
    onDelete({ nodes: [target], edges: [] });
  }

  function setEdgeType(type: 'success' | 'failure' | 'always') {
    if (!edgeContextMenu) return;
    const eid = edgeContextMenu.edgeId;
    flowEdges = flowEdges.map((e) =>
      e.id === eid
        ? { ...e, label: type === 'success' ? '' : type, animated: type === 'success', style: `stroke: ${getEdgeColor(type)}; stroke-width: 2;`, labelStyle: type !== 'success' ? 'fill: rgb(156 163 175); font-size: 10px;' : '', data: { ...e.data, edgeType: type, condition: null } }
        : e,
    );
    edgeContextMenu = null;
  }

  function setEdgeConditional() {
    if (!edgeContextMenu) return;
    const eid = edgeContextMenu.edgeId;
    const current = flowEdges.find((e) => e.id === eid);
    conditionalEdgeId = eid;
    conditionalExpr = (current?.data?.condition as string | null | undefined) ?? '';
    conditionalError = null;
    showConditionalDialog = true;
    edgeContextMenu = null;
  }

  function saveConditional() {
    if (!conditionalEdgeId) return;
    const trimmed = conditionalExpr.trim();
    if (!trimmed) {
      conditionalError = 'Conditional edge needs a non-empty expression.';
      return;
    }
    const eid = conditionalEdgeId;
    flowEdges = flowEdges.map((e) =>
      e.id === eid
        ? { ...e, label: `if ${truncateExpr(trimmed)}`, animated: false, style: `stroke: ${getEdgeColor('conditional')}; stroke-width: 2;`, labelStyle: 'fill: rgb(156 163 175); font-size: 10px;', data: { ...e.data, edgeType: 'conditional', condition: trimmed } }
        : e,
    );
    closeConditionalDialog();
  }

  function closeConditionalDialog() {
    showConditionalDialog = false;
    conditionalEdgeId = null;
    conditionalExpr = '';
    conditionalError = null;
  }

  async function deleteEdgeFromMenu() {
    if (!edgeContextMenu) return;
    const eid = edgeContextMenu.edgeId;
    edgeContextMenu = null;
    const ok = await confirm({
      tone: 'danger',
      title: 'Delete edge?',
      message: 'This removes the connection. This can be undone only by leaving without saving.',
    });
    if (!ok) return;
    flowEdges = flowEdges.filter((e) => e.id !== eid);
  }

  // Shapes the edge BEFORE SvelteFlow adds it, and is the only place an edge
  // gets created by dragging.
  //
  // It used to be `onconnect`, which is the wrong hook: SvelteFlow's Handle does
  //     store.addEdge(edge); store.onconnect?.(connection);
  // — it adds the edge itself and THEN notifies. So the handler appended a
  // second copy, whose duplicate check never fired because it compared against
  // its own composite id while SvelteFlow's copy carried an auto-generated one.
  // Every connection produced exactly two identical edges, which is what the
  // saved workflows show.
  //
  // `onbeforeconnect` returns the edge SvelteFlow will add (or null to refuse),
  // so there is one edge, carrying our type/styling, and the duplicate check
  // actually prevents the duplicate.
  function onBeforeConnect(connection: {
    source: string | null;
    target: string | null;
    sourceHandle?: string | null;
    targetHandle?: string | null;
  }) {
    if (!connection.source || !connection.target) return null;
    const sh = connection.sourceHandle ?? undefined;
    const th = connection.targetHandle ?? undefined;
    // Composite id so re-connecting the same pair via different sides
    // doesn't clobber the existing edge.
    const id = `e-${connection.source}${sh ? ':' + sh : ''}-${connection.target}${th ? ':' + th : ''}`;

    // Same endpoints AND handles: refuse rather than stack a second edge.
    // Compared on the endpoints, not the id, so an edge that arrived by any
    // other route (a load, an older save) is still recognised.
    const duplicate = flowEdges.some((e) =>
      e.source === connection.source
      && e.target === connection.target
      && (e.sourceHandle ?? undefined) === sh
      && (e.targetHandle ?? undefined) === th);
    if (duplicate) return null;

    validationError = '';
    return {
      id,
      source: connection.source,
      target: connection.target,
      sourceHandle: sh,
      targetHandle: th,
      label: '',
      animated: true,
      style: `stroke: ${getEdgeColor('success')}; stroke-width: 2;`,
      data: { edgeType: 'success', condition: null },
    };
  }

  // Shared confirm gate for destructive node deletion. Returns true when
  // the delete may proceed. A node with no incident edges deletes silently
  // (nothing else is lost); a node that drags edges down asks first because
  // there's no undo short of reloading without saving.
  // `alreadyCountedEdgeIds` lets callers exclude edges that the same delete
  // event removes explicitly (xyflow's built-in path already folds the
  // incident edges into its edge list) so we don't under/over-count.
  async function confirmNodeDeletion(
    nodeIds: Set<string>,
    alreadyCountedEdgeIds: Set<string> = new Set(),
  ): Promise<boolean> {
    const incident = flowEdges.filter(
      (e) =>
        !alreadyCountedEdgeIds.has(e.id)
        && (nodeIds.has(e.source) || nodeIds.has(e.target)),
    );
    if (incident.length === 0) return true;
    return confirm({
      tone: 'danger',
      title: 'Delete node?',
      message: `This removes the node and its ${incident.length} connected edge(s). This can be undone only by leaving without saving.`,
    });
  }

  // xyflow's deletion gate. Runs *before* the store mutates, for both the
  // Delete/Backspace keyboard path and any other built-in delete trigger.
  // Returning false aborts the whole operation (nothing is removed);
  // returning true lets it through to onDelete. `edges` already contains the
  // edges xyflow would drop alongside the nodes (incident + explicitly
  // selected), but we recompute from flowEdges in the shared helper for a
  // stable count. Edge-only deletions (no nodes) proceed without a prompt.
  async function onBeforeDelete({ nodes: toDelete }: { nodes: Node[]; edges: Edge[] }): Promise<boolean> {
    if (toDelete.length === 0) return true;
    const ids = new Set(toDelete.map((n) => n.id));
    return confirmNodeDeletion(ids);
  }

  // Post-delete teardown. By the time xyflow's keyboard / built-in delete
  // path invokes this, it has *already* mutated flowNodes/flowEdges (the
  // gate lives in onBeforeDelete). The filters below are therefore
  // idempotent — they also cover the context-menu path, which calls this
  // directly after its own confirm — and the real job here is clearing the
  // selection + closing the dialog when the open node disappears.
  function onDelete({ nodes: deletedNodes, edges: deletedEdges }: { nodes: Node[]; edges: Edge[] }) {
    if (deletedNodes.length > 0) {
      const deletedIds = new Set(deletedNodes.map((n) => n.id));
      flowNodes = flowNodes.filter((n) => !deletedIds.has(n.id));
      flowEdges = flowEdges.filter((e) => !deletedIds.has(e.source) && !deletedIds.has(e.target));
    }
    if (deletedEdges.length > 0) {
      const ids = new Set(deletedEdges.map((e) => e.id));
      flowEdges = flowEdges.filter((e) => !ids.has(e.id));
    }
    if (selectedNodeId && deletedNodes.some((n) => n.id === selectedNodeId)) {
      selectedNodeId = null;
      selectedNodeService = null;
      selectedIntegrationAction = null;
      selectedNodeIsIntegrationAction = false;
      selectedNodeIsSentinel = false;
      selectedNodeIsSubflow = false;
      selectedNodeIsMcpCall = false;
      nodeDialogOpen = false;
    }
  }

  function validateWorkflow(): string | null {
    const realNodes = flowNodes.filter((n) => n.id !== '__start__' && n.id !== '__end__');
    if (realNodes.length === 0) return 'Workflow must have at least one step.';
    const startEdges = flowEdges.filter((e) => e.source === '__start__' && e.target !== '__end__');
    if (startEdges.length === 0) return 'Connect the Start node to your first step.';
    const endEdges = flowEdges.filter((e) => e.target === '__end__' && e.source !== '__start__');
    if (endEdges.length === 0) return 'Connect your last step to the End node.';
    for (const e of startEdges) {
      if (!realNodes.some((n) => n.id === e.target)) return `Start connects to unknown node: ${e.target}`;
    }
    return null;
  }

  function hasIntegrationFields(schema: Record<string, unknown> | undefined | null): boolean {
    if (!schema?.properties) return false;
    const integrationFields = ['netbox_url', 'netbox_token', 'base_url', 'api_url', 'api_token', 'token', 'url'];
    return Object.keys(schema.properties as Record<string, unknown>).some((k) => integrationFields.includes(k));
  }

  function filterIntegrationFields(schema: Record<string, unknown>): Record<string, unknown> {
    if (!schema?.properties) return schema;
    const hidden = ['netbox_url', 'netbox_token', 'base_url', 'api_url', 'api_token', 'token', 'url', '_integration_id', '_integration_name'];
    const props = { ...(schema.properties as Record<string, unknown>) };
    for (const f of hidden) delete props[f];
    const filtered: Record<string, unknown> = { ...schema, properties: props };
    if (Array.isArray(schema.required)) {
      filtered.required = (schema.required as string[]).filter((r) => !hidden.includes(r));
    }
    return filtered;
  }

  // Whether the user has tagged this workflow as reusable. Driven by
  // `metadata.is_subflow`. When true, the workflow shows up at
  // /subflows and inside any subflow-node picker.
  const isReusableSubflow = $derived(
    !!(workflow?.metadata as Record<string, unknown> | undefined)?.is_subflow,
  );

  async function toggleSubflowTag() {
    if (!workflow) return;
    const nextValue = !isReusableSubflow;
    const nextMetadata = { ...(workflow.metadata ?? {}), is_subflow: nextValue };
    try {
      const updated = await workflows.update(workflow.id, { metadata: nextMetadata });
      workflow = updated;
      toast.success(
        nextValue
          ? 'Tagged as reusable subflow'
          : 'Removed subflow tag',
        {
          description: nextValue
            ? 'Now appears in /subflows and in any subflow-node picker.'
            : 'No longer listed at /subflows. Existing callers keep working — they reference by id.',
        },
      );
    } catch (e) {
      toast.fromError(e, "Couldn't update workflow metadata");
    } finally {
      showMoreMenu = false;
    }
  }

  async function saveWorkflow() {
    saveStatus = 'saving';
    const vError = validateWorkflow();
    if (vError) { validationError = vError; saveStatus = ''; return; }
    validationError = '';
    try {
      const wfNodes: WorkflowNode[] = flowNodes.map((n) => ({
        id: n.id,
        snippet_id: n.data.snippetId as string,
        x: Math.round(n.position.x),
        y: Math.round(n.position.y),
        config_overrides: (n.data.configOverrides as Record<string, unknown>) || {},
      }));
      const wfEdges: WorkflowEdge[] = flowEdges.map((e) => {
        const type = ((e.data?.edgeType as 'success' | 'failure' | 'always' | 'conditional') ?? 'success');
        const condition = e.data?.condition as string | null | undefined;
        return {
          source: e.source,
          target: e.target,
          type,
          // Conditional edges carry the expression the engine evaluates
          // against the source step's output. The backend rejects
          // type='conditional' without a non-empty condition string.
          ...(type === 'conditional' && condition ? { condition } : {}),
          // Persist the handles so reopening the workflow restores the
          // exact side each edge was connected to. Omit when undefined so
          // the backend schema's optional field stays truly optional.
          ...(e.sourceHandle ? { source_handle: e.sourceHandle } : {}),
          ...(e.targetHandle ? { target_handle: e.targetHandle } : {}),
        };
      });
      await workflows.update(page.params.id!, { nodes: wfNodes, edges: wfEdges });
      saveStatus = 'saved';
      // Re-baseline so the diff resets — without this the effect would
      // keep reporting dirty against the *pre-save* snapshot.
      baselineSnapshot = captureSnapshot();
      dirtyStore.markClean();
      setTimeout(() => { saveStatus = ''; }, 2000);
    } catch (e) {
      saveStatus = 'error';
      saveError = errorMessage(e);
      toast.fromError(e, 'Couldn’t save workflow');
    }
  }

  // True when at least one node in the workflow points at a service whose
  // target_mode is `per_device`. When every node is `once` (python snippet,
  // transform, rest_call without device context, etc.) we surface the
  // device picker as strictly optional so the user isn't misled into
  // picking devices that the workflow will never use.
  const workflowRequiresDevices = $derived.by(() => {
    const nodes = (workflow?.nodes as Array<{ snippet_id?: string }> | undefined) ?? [];
    for (const n of nodes) {
      if (!n?.snippet_id) continue;
      const svc = serviceMap.get(n.snippet_id);
      if (svc?.target_mode === 'per_device') return true;
    }
    return false;
  });

  function toggleRunDialog() {
    showRunDialog = !showRunDialog;
    if (showRunDialog) {
      selectedDevices = [];
      runInput = buildSchemaDefaults(workflow?.input_schema);
    }
  }

  async function runWorkflow() {
    runStatus = 'starting';
    runId = '';
    showRunDialog = false;
    const vError = validateWorkflow();
    if (vError) { validationError = vError; runStatus = ''; return; }
    validationError = '';
    // A run always uses the last *saved* version on the backend. Warn (but
    // still proceed) when the canvas has edits that haven't been persisted
    // so the user isn't surprised the run ignored them.
    if (dirtyStore.dirty) {
      toast.warning('Run uses the last saved version — you have unsaved changes.');
    }
    try {
      const payload: { target_devices?: string[]; input?: Record<string, unknown> } = {};
      if (selectedDevices.length > 0) payload.target_devices = selectedDevices;
      if (workflow?.input_schema && Object.keys(workflow.input_schema).length > 0) payload.input = runInput;
      const res = await workflows.run(page.params.id!, payload);
      runId = res.id;
      runStatus = 'started';
      // SPA navigation (not window.location.href) so the dirty-guard's
      // beforeNavigate handler fires before we leave the editor.
      goto(`/runs/${res.id}/monitor`);
    } catch (e) {
      runStatus = 'error';
      error = errorMessage(e);toast.fromError(e, 'Couldn’t run workflow');
    }
  }

  // Opens the run-data dialog: fetches the latest run for this workflow
  // + all its steps in one shot so the user can inspect inputs / outputs /
  // logs without navigating away from the editor.
  async function openRunDataDialog() {
    showRunDataDialog = true;
    runDataError = null;
    runDataExpandedStep = null;
    loadingRunData = true;
    try {
      const runsResp = await runs.list(10, 0);
      const wfRuns = runsResp.data.filter((r) => r.workflow_id === page.params.id);
      if (wfRuns.length === 0) {
        runDataRun = null;
        runDataSteps = [];
        runDataError = 'No runs found for this workflow yet. Run it at least once to see data here.';
        return;
      }
      runDataRun = wfRuns[0];
      const stepsResp = await runs.steps(runDataRun.id);
      runDataSteps = stepsResp.data;
    } catch (e) {
      runDataError = errorMessage(e);
      toast.fromError(e, "Couldn't load run data");
    } finally {
      loadingRunData = false;
    }
  }

  function toggleRunDataStep(stepId: string) {
    runDataExpandedStep = runDataExpandedStep === stepId ? null : stepId;
  }

  const placeholderVarRef = 'Value or {{steps.prev.output.id}}';
  const placeholderQueryRef = 'Value or {{steps.prev.output.field}}';
  const placeholderJsonBody = '{"key": "value"}';
</script>

<svelte:head><title>{workflow?.name ?? 'Workflow'} · FlowWeaver</title></svelte:head>

<div class="flex h-[calc(100vh-0rem)] relative">
  <!-- LEFT: palette -->
  {#if leftSidebarOpen}
    <aside class="w-64 shrink-0 bg-surface-100-900 border-r border-surface-200-800 flex flex-col">
      <div class="flex items-center justify-between px-3 h-10 border-b border-surface-200-800">
        <h2 class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">Palette <FieldHint id="workflows.palette" /></h2>
        <IconButton icon={X} label="Hide palette" size="xs" onclick={() => (leftSidebarOpen = false)} />
      </div>
      <div class="flex-1 overflow-y-auto p-2 space-y-1">
        <!-- Composition primitive. Stays at the top of the palette so it's
             always reachable regardless of how long the snippet/integration
             lists grow. Dropping it onto the canvas creates a placeholder
             subflow node; the user picks the target workflow in the dialog. -->
        <div class="text-[10px] font-semibold uppercase tracking-wider text-surface-500 px-1 pt-1 pb-0.5">Composition</div>
        <div
          class="p-2 bg-surface-50-950 rounded-md border border-surface-200-800/70 hover:border-primary-400/60 transition-colors cursor-grab active:cursor-grabbing select-none"
          draggable="true"
          ondragstart={onSubflowDragStart}
          role="listitem"
          title="Drag onto the canvas to call another workflow as a step"
        >
          <div class="font-medium text-xs text-surface-900-100 flex items-center gap-1.5">
            <Layers size={12} class="text-primary-400" />
            Subflow
          </div>
          <div class="text-[10px] text-surface-500 mt-0.5">
            Call another workflow as a single step.
          </div>
        </div>

        <div class="flex items-center justify-between px-1 pt-3 pb-0.5 border-t border-surface-200-800 mt-2">
          <span class="text-[10px] font-semibold uppercase tracking-wider text-surface-500">Snippets</span>
          <!-- python_snippet (and other code types) aren't seeded built-ins —
               they appear here once created. This shortcut opens the snippet
               editor (Type: Python snippet) without hunting for the page. -->
          <a href="/snippets/new" class="text-[10px] text-primary-300 hover:underline" title="Create a new snippet (Python, etc.)">+ New</a>
        </div>
        <div class="px-1 pb-1.5">
          <Input
            bind:value={snippetSearch}
            placeholder="Search snippets…"
            aria-label="Search snippets" help="workflows.snippet_search"
            class="h-7 text-xs"
          />
        </div>
        {#each provenServiceList as svc}
          <div
            class="p-2 bg-surface-50-950 rounded-md border border-surface-200-800/70 hover:border-surface-300-700 transition-colors cursor-grab active:cursor-grabbing select-none"
            draggable="true"
            ondragstart={(e) => onDragStart(e, svc)}
            role="listitem"
          >
            <div class="font-medium text-xs text-surface-900-100">{svc.name}</div>
            <div class="text-[10px] text-surface-500 mt-0.5 flex items-center gap-1">
              <span class="px-1 py-0.5 bg-surface-200-800/60 rounded">{svc.type}</span>
              {#if svc.target_mode}<span class="text-surface-500">{svc.target_mode}</span>{/if}
            </div>
          </div>
        {/each}
        {#if filteredServiceList.length === 0}
          {#if serviceList.length === 0}
            <p class="text-xs text-surface-500 p-2 text-center">No services available</p>
          {:else}
            <p class="text-xs text-surface-500 p-2 text-center">No matches for "{snippetSearch}"</p>
          {/if}
        {:else if provenServiceList.length === 0}
          <p class="text-xs text-surface-500 p-2 text-center">
            None have completed a run yet — see <em>Unproven</em> below.
          </p>
        {/if}

        <!-- Unproven: never completed a run. Collapsed so the list above stays
             the set of blocks known to work, but reachable so a new snippet can
             be dragged in and exercised for the first time. -->
        {#if unprovenServiceList.length > 0}
          <button
            onclick={() => (showUnproven = !showUnproven)}
            class="w-full text-left px-2 py-1.5 mt-1 text-xs text-surface-600-400 hover:bg-surface-200-800/40 rounded flex items-center gap-1.5 transition-colors"
            title="Snippets that have never completed a run. Drag one in to try it — once a step using it completes, it moves up."
          >
            <ChevronDown size={10} class={unprovenExpanded ? '' : '-rotate-90'} />
            <span class="font-medium">Unproven</span>
            <span class="text-surface-500 ml-auto tabular-nums">{unprovenServiceList.length}</span>
          </button>
          {#if unprovenExpanded}
            <p class="text-[10px] text-surface-500 px-2 pb-1">
              Never completed a run. Drag one in and run it — it moves up once a step
              using it completes.
            </p>
            <div class="space-y-1">
              {#each unprovenServiceList as svc}
                <div
                  class="p-2 bg-surface-50-950/50 rounded-md border border-dashed border-surface-200-800/70 hover:border-surface-300-700 transition-colors cursor-grab active:cursor-grabbing select-none"
                  draggable="true"
                  ondragstart={(e) => onDragStart(e, svc)}
                  role="listitem"
                >
                  <div class="font-medium text-xs text-surface-700-300">{svc.name}</div>
                  <div class="text-[10px] text-surface-500 mt-0.5 flex items-center gap-1">
                    <span class="px-1 py-0.5 bg-surface-200-800/60 rounded">{svc.type}</span>
                    {#if svc.target_mode}<span class="text-surface-500">{svc.target_mode}</span>{/if}
                  </div>
                </div>
              {/each}
            </div>
          {/if}
        {/if}

        {#if integrationActions.length > 0}
          <div class="text-[10px] font-semibold uppercase tracking-wider text-surface-500 px-1 pt-3 pb-0.5 border-t border-surface-200-800 mt-2">Integrations</div>
          {#each [...groupedIntegrationActions()] as [integrationName, categories]}
            <div>
              <button
                onclick={() => toggleIntegration(integrationName)}
                class="w-full text-left px-2 py-1.5 text-xs text-surface-700-300 hover:bg-surface-200-800/40 rounded flex items-center gap-1.5 transition-colors"
              >
                <ChevronDown size={10} class={expandedIntegrations.has(integrationName) ? '' : '-rotate-90'} />
                <span class="font-medium">{integrationName}</span>
                <span class="text-surface-500 ml-auto tabular-nums">{[...categories.values()].reduce((s, a) => s + a.length, 0)}</span>
              </button>
              {#if expandedIntegrations.has(integrationName)}
                <div class="ml-2">
                  {#each [...categories] as [category, actions]}
                    <div>
                      <button
                        onclick={() => toggleCategory(`${integrationName}:${category}`)}
                        class="w-full text-left px-2 py-1 text-[10px] text-surface-600-400 hover:bg-surface-200-800/40 rounded flex items-center gap-1.5 transition-colors"
                      >
                        <ChevronDown size={9} class={expandedCategories.has(`${integrationName}:${category}`) ? '' : '-rotate-90'} />
                        <span>{category}</span>
                        <span class="text-surface-500 ml-auto tabular-nums">{actions.length}</span>
                      </button>
                      {#if expandedCategories.has(`${integrationName}:${category}`)}
                        <div class="ml-2 space-y-0.5">
                          {#each actions as action}
                            <div
                              class="p-1.5 bg-surface-50-950 rounded border border-surface-200-800/70 hover:border-surface-300-700 transition-colors cursor-grab active:cursor-grabbing select-none"
                              draggable="true"
                              ondragstart={(e) => onIntegrationActionDragStart(e, action)}
                              role="listitem"
                            >
                              <div class="font-medium text-[10px] text-surface-700-300 flex items-center gap-1.5">
                                <span class="px-1 py-0.5 rounded text-[9px] font-mono {methodTone[action.method] ?? 'bg-surface-200-800 text-surface-500'}">{action.method}</span>
                                <span class="truncate font-mono">{action.path}</span>
                              </div>
                            </div>
                          {/each}
                        </div>
                      {/if}
                    </div>
                  {/each}
                </div>
              {/if}
            </div>
          {/each}
        {/if}

        {#if mcpTools.length > 0 && mcpCallSnippetId}
          <div class="text-[10px] font-semibold uppercase tracking-wider text-surface-500 px-1 pt-3 pb-0.5 border-t border-surface-200-800 mt-2">MCP tools</div>
          {#each [...groupedMcpTools()] as [serverId, serverTools]}
            <div>
              <button
                onclick={() => toggleMcpServer(serverId)}
                class="w-full text-left px-2 py-1.5 text-xs text-surface-700-300 hover:bg-surface-200-800/40 rounded flex items-center gap-1.5 transition-colors"
              >
                <ChevronDown size={10} class={expandedMcpServers.has(serverId) ? '' : '-rotate-90'} />
                <span class="font-medium">{mcpServerName(serverId)}</span>
                <span class="text-surface-500 ml-auto tabular-nums">{serverTools.length}</span>
              </button>
              {#if expandedMcpServers.has(serverId)}
                <div class="ml-2 space-y-0.5">
                  {#each serverTools as tool (tool.mcp_tool_id)}
                    <div
                      class="p-1.5 bg-surface-50-950 rounded border border-surface-200-800/70 hover:border-surface-300-700 transition-colors cursor-grab active:cursor-grabbing select-none"
                      draggable="true"
                      ondragstart={(e) => onMcpToolDragStart(e, tool)}
                      role="listitem"
                      title={tool.description ?? tool.name}
                    >
                      <div class="font-medium text-[10px] text-surface-700-300 font-mono truncate">{tool.name}</div>
                    </div>
                  {/each}
                </div>
              {/if}
            </div>
          {/each}
        {/if}
      </div>
    </aside>
  {/if}

  <!-- MAIN -->
  <div class="flex-1 flex flex-col min-w-0">
    <!-- Toolbar.
         Responsive strategy: `min-h-12` (not fixed `h-12`) lets the bar grow
         when actions wrap — the original cut wrapped buttons in half.
         Below `xl` (≈1280 px, a typical laptop screen) the secondary
         actions collapse to icon-only by hiding their labels via the
         `.btn-label` helper class (`hidden xl:inline`). Save and Run keep
         their labels at all sizes so the primary actions remain readable. -->
    <div class="flex items-center justify-between gap-2 px-3 sm:px-4 min-h-12 py-1.5 bg-surface-100-900 border-b border-surface-200-800 flex-wrap">
      <div class="flex items-center gap-2 min-w-0">
        <Button variant="ghost" size="sm" icon={ArrowLeft} href="/workflows">
          <span class="hidden sm:inline">Workflows</span>
        </Button>
        <span class="text-surface-400-600 hidden sm:inline">/</span>
        {#if workflow}
          <span class="text-sm font-medium text-surface-900-100 truncate max-w-[40vw] sm:max-w-none">{workflow.name}</span>
        {/if}
        {#if !leftSidebarOpen}
          <Button size="xs" variant="ghost" onclick={() => (leftSidebarOpen = true)}>
            <span class="hidden md:inline">Show palette</span>
            <span class="md:hidden">Palette</span>
          </Button>
        {/if}
      </div>

      <div class="flex items-center gap-1.5 sm:gap-2 flex-wrap justify-end">
        {#if saveStatus === 'saved'}
          <span class="save-pill text-xs text-success-300 inline-flex items-center gap-1">
            <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 6 9 17l-5-5"/></svg>
            Saved
          </span>
        {:else if saveStatus === 'saving'}
          <span class="text-xs text-warning-300 inline-flex items-center gap-1">
            <span class="inline-block w-2 h-2 rounded-full bg-warning-400 animate-pulse"></span>
            Saving…
          </span>
        {:else if saveStatus === 'error'}
          <span class="text-xs text-error-300" title={saveError}>Save failed</span>
        {:else if dirtyStore.dirty}
          <span class="text-xs text-warning-300 inline-flex items-center gap-1" title="You have unsaved changes (Ctrl/Cmd+S to save)">
            <span class="inline-block w-1.5 h-1.5 rounded-full bg-warning-400 animate-pulse"></span>
            Unsaved
          </span>
        {/if}

        {#if runStatus === 'started' && runId}
          <a href="/runs/{runId}" class="text-xs text-success-300 hover:text-success-200 transition-colors">Run: {runId.slice(0, 8)}…</a>
        {:else if runStatus === 'starting'}
          <span class="text-xs text-warning-300">Starting…</span>
        {:else if runStatus === 'error'}
          <span class="text-xs text-error-300">Run failed</span>
        {/if}

        <Button size="sm" variant="ghost" icon={Eye} onclick={openRunDataDialog} loading={loadingRunData} title="Show run data">
          <span class="hidden xl:inline">Show run data</span>
        </Button>

        <!-- Hands the workflow id off to the agent so it can pull the DAG
             (get_workflow_details) and apply edits via update_workflow_node_config. -->
        <Button
          size="sm"
          variant="secondary"
          icon={Sparkles}
          href={'/ai/chat?context=workflow:' + page.params.id}
          title="Ask AI"
        >
          <span class="hidden xl:inline">Ask AI</span>
        </Button>

        <Button size="sm" variant="ghost" icon={LayoutGrid} onclick={applyAutoLayout} title="Re-layout the DAG with Dagre (left-to-right)">
          <span class="hidden xl:inline">Auto-layout</span>
        </Button>

        <Button
          size="sm"
          variant="ghost"
          icon={FlaskConical}
          loading={simulateLoading}
          onclick={runSimulate}
          title="Static DAG validation without executing any handler"
        >
          <span class="hidden xl:inline">Simulate</span>
        </Button>

        <!-- Save button with a "dirty" badge (top-right dot). The badge
             pulses while there are unsaved changes so the user can see at
             a glance whether the persisted state matches the canvas. -->
        <div class="relative">
          <Button size="sm" variant="primary" icon={Save} onclick={saveWorkflow} loading={saveStatus === 'saving'} title={dirtyStore.dirty ? 'Unsaved changes — Ctrl/Cmd+S to save' : 'Save workflow (Ctrl/Cmd+S)'}>Save</Button>
          {#if dirtyStore.dirty && saveStatus !== 'saving'}
            <span class="dirty-dot pointer-events-none absolute -top-1 -right-1 inline-flex">
              <span class="absolute inline-flex h-2.5 w-2.5 rounded-full bg-warning-400 opacity-60 animate-ping"></span>
              <span class="relative inline-flex h-2.5 w-2.5 rounded-full bg-warning-400 ring-2 ring-surface-100-900"></span>
            </span>
          {/if}
        </div>

        <div class="relative">
          <Button size="sm" variant="secondary" icon={Download} iconRight={ChevronDown} onclick={() => (showExportMenu = !showExportMenu)} title="Export">
            <span class="hidden lg:inline">Export</span>
          </Button>
          {#if showExportMenu}
            <div
              class="absolute right-0 top-full mt-1 w-48 bg-surface-100-900 border border-surface-300-700 rounded-md shadow-xl z-50 py-1"
              onclick={(e) => e.stopPropagation()}
              onkeydown={(e) => { if (e.key === 'Escape') showExportMenu = false; }}
              role="menu"
              tabindex="-1"
            >
              <!-- The only format that survives crossing instances: it carries
                   each snippet's definition and a stable slug per
                   integration/action, so the far side binds to its own rows.
                   yaml/json carry per-instance GUIDs and resolve to nothing
                   elsewhere. -->
              <button
                onclick={() => { void exportWorkflow.download(page.params.id!, 'bundle').catch((e) => toast.fromError(e, 'Export failed')); showExportMenu = false; }}
                class="w-full text-left px-3 py-1.5 text-xs text-surface-800-200 hover:bg-surface-200-800/60 transition-colors"
                title="Portable: includes snippet definitions and integration identities. Use this to share a workflow with another FlowWeaver instance."
              >Bundle (share / portable)</button>
              <div class="my-1 border-t border-surface-300-700"></div>
              <button
                onclick={() => { void exportWorkflow.download(page.params.id!, 'yaml').catch((e) => toast.fromError(e, 'Export failed')); showExportMenu = false; }}
                class="w-full text-left px-3 py-1.5 text-xs text-surface-800-200 hover:bg-surface-200-800/60 transition-colors"
                title="Same-instance only — references are GUIDs."
              >YAML (this instance)</button>
              <button
                onclick={() => { void exportWorkflow.download(page.params.id!, 'json').catch((e) => toast.fromError(e, 'Export failed')); showExportMenu = false; }}
                class="w-full text-left px-3 py-1.5 text-xs text-surface-800-200 hover:bg-surface-200-800/60 transition-colors"
              >JSON (this instance)</button>
              <div class="my-1 border-t border-surface-300-700"></div>
              <button
                onclick={() => { void exportWorkflow.download(page.params.id!, 'python').catch((e) => toast.fromError(e, 'Export failed')); showExportMenu = false; }}
                class="w-full text-left px-3 py-1.5 text-xs text-surface-800-200 hover:bg-surface-200-800/60 transition-colors"
              >Python script</button>
              <button
                onclick={() => { void exportWorkflow.download(page.params.id!, 'ansible').catch((e) => toast.fromError(e, 'Export failed')); showExportMenu = false; }}
                class="w-full text-left px-3 py-1.5 text-xs text-surface-800-200 hover:bg-surface-200-800/60 transition-colors"
              >Ansible playbook</button>
            </div>
          {/if}
        </div>

        <Button size="sm" variant="success" icon={Play} onclick={toggleRunDialog} disabled={runStatus === 'starting'} title="Run workflow">Run</Button>

        <!-- Governance / scheduling shortcuts collapsed into a single dropdown
             so the toolbar stays one row at 100 % zoom. Schedules / Triggers /
             Permissions all navigate away from the editor canvas; grouping
             them avoids dilution of the primary actions to their left. -->
        <div class="relative">
          <Button
            size="sm"
            variant="ghost"
            icon={MoreHorizontal}
            iconRight={ChevronDown}
            onclick={() => (showMoreMenu = !showMoreMenu)}
            title="Schedules, triggers and permissions"
          >
            <span class="hidden lg:inline">More</span>
          </Button>
          {#if showMoreMenu}
            <div
              class="absolute right-0 top-full mt-1 w-48 bg-surface-100-900 border border-surface-300-700 rounded-md shadow-xl z-50 py-1"
              onclick={(e) => e.stopPropagation()}
              onkeydown={(e) => { if (e.key === 'Escape') showMoreMenu = false; }}
              role="menu"
              tabindex="-1"
            >
              <a
                href="/workflows/{page.params.id}/schedules"
                onclick={() => (showMoreMenu = false)}
                class="flex items-center gap-2 px-3 py-1.5 text-xs text-surface-800-200 hover:bg-surface-200-800/60 transition-colors"
              >
                <CalendarClock size={14} /> Schedules
              </a>
              <a
                href="/workflows/{page.params.id}/triggers"
                onclick={() => (showMoreMenu = false)}
                class="flex items-center gap-2 px-3 py-1.5 text-xs text-surface-800-200 hover:bg-surface-200-800/60 transition-colors"
              >
                <Zap size={14} /> Triggers
              </a>
              <div class="my-1 border-t border-surface-300-700"></div>
              <a
                href="/workflows/{page.params.id}/permissions"
                onclick={() => (showMoreMenu = false)}
                class="flex items-center gap-2 px-3 py-1.5 text-xs text-surface-800-200 hover:bg-surface-200-800/60 transition-colors"
              >
                <Shield size={14} /> Permissions
              </a>
              <div class="my-1 border-t border-surface-300-700"></div>
              <button
                onclick={toggleSubflowTag}
                class="w-full text-left flex items-center gap-2 px-3 py-1.5 text-xs text-surface-800-200 hover:bg-surface-200-800/60 transition-colors"
                role="menuitemcheckbox"
                aria-checked={isReusableSubflow}
                title={isReusableSubflow
                  ? 'Untag — workflow will leave /subflows'
                  : 'Tag — workflow will appear at /subflows and in subflow-node pickers'}
              >
                <Layers size={14} class={isReusableSubflow ? 'text-primary-400' : ''} />
                <span class="flex-1">Reusable as subflow</span>
                {#if isReusableSubflow}
                  <span class="text-[10px] text-success-400">ON</span>
                {:else}
                  <span class="text-[10px] text-surface-500">OFF</span>
                {/if}
              </button>
            </div>
          {/if}
        </div>
      </div>
    </div>

    {#if validationError}
      <div class="px-4 pt-2"><Alert tone="error" dismissible onDismiss={() => (validationError = '')}>{validationError}</Alert></div>
    {/if}

    {#if loading}
      <div class="flex-1 flex items-center justify-center"><Spinner size="lg" label="Loading workflow…" /></div>
    {:else if error && !workflow}
      <div class="flex-1 flex items-center justify-center p-4"><Alert tone="error">{error}</Alert></div>
    {:else}
      <div class="flex-1" role="application">
        <SvelteFlow
          bind:nodes={flowNodes}
          bind:edges={flowEdges}
          bind:viewport
          fitView
          {nodeTypes}
          connectionMode={ConnectionMode.Loose}
          onnodeclick={onNodeClick}
          onpaneclick={onPaneClick}
          onbeforeconnect={onBeforeConnect}
          onnodecontextmenu={onNodeContextMenu}
          onedgecontextmenu={onEdgeContextMenu}
          onbeforedelete={onBeforeDelete}
          ondelete={onDelete}
          ondragover={onDragOver}
          ondrop={onDrop}
          defaultEdgeOptions={{ animated: true, selectable: true, deletable: true }}
        >
          <Controls />
          <Background
            bgColor="var(--fw-canvas-bg)"
            patternColor="color-mix(in oklab, var(--color-surface-500) 50%, transparent)"
          />
        </SvelteFlow>
        <!-- Edge type legend -->
        <div class="absolute bottom-3 left-1/2 -translate-x-1/2 flex items-center gap-3 bg-surface-100-900/90 backdrop-blur-sm ring-1 ring-surface-200-800/60 rounded-md px-3 py-1.5 text-[10px] text-surface-500 z-10 pointer-events-none select-none">
          <span class="flex items-center gap-1"><span class="w-3 h-0.5 rounded bg-green-500 inline-block"></span>success</span>
          <span class="flex items-center gap-1"><span class="w-3 h-0.5 rounded bg-red-500 inline-block"></span>failure</span>
          <span class="flex items-center gap-1"><span class="w-3 h-0.5 rounded bg-purple-500 inline-block"></span>always</span>
          <span class="text-surface-500/60">·</span>
          <span>Right-click edge to change type · node for actions</span>
        </div>
      </div>
    {/if}
  </div>

  <!-- Node config now lives in a dialog instead of a side panel — see
       WorkflowNodeDialog for the full editor (node config / service /
       schemas) with deferred vs immediate save semantics. -->
  <WorkflowNodeDialog
    bind:open={nodeDialogOpen}
    nodeId={selectedNodeId}
    initialConfig={selectedNodeConfig}
    service={selectedNodeService}
    integrationAction={selectedIntegrationAction}
    isSentinel={selectedNodeIsSentinel}
    isIntegrationAction={selectedNodeIsIntegrationAction}
    isSubflow={selectedNodeIsSubflow}
    isTransform={isTransformNode}
    isMcpCall={selectedNodeIsMcpCall}
    integrationList={integrationListForPicker}
    integrationActions={integrationActions}
    mcpServerList={mcpServerList}
    mcpTools={mcpTools}
    upstreamNodeIds={upstreamNodeIdsForSelected}
    onNodeApply={onDialogNodeApply}
    onServiceRefresh={onDialogServiceRefresh}
  />

  <!-- Node context menu — right-click on any node in the canvas.
       Sentinel nodes (__start__/__end__) keep Ask AI but lose Edit /
       Duplicate / Fix / Delete since none of those make sense for
       graph terminators. -->
  {#if nodeContextMenu}
    <div
      class="fixed bg-surface-100-900 border border-surface-300-700 rounded-md shadow-xl z-50 py-1 min-w-[180px]"
      style="left: {nodeContextMenu.x}px; top: {nodeContextMenu.y}px;"
      onclick={(e) => e.stopPropagation()}
      onkeydown={(e) => { if (e.key === 'Escape') closeNodeMenu(); }}
      role="menu"
      tabindex="-1"
    >
      <div class="px-3 py-1 text-[10px] uppercase tracking-wider text-surface-500 truncate" title={nodeContextMenu.nodeId}>
        {nodeContextMenu.isSentinel ? 'Sentinel' : 'Node'} · {nodeContextMenu.nodeId}
      </div>

      <button
        onclick={editFromMenu}
        disabled={nodeContextMenu.isSentinel}
        class="w-full text-left px-3 py-1.5 text-xs hover:bg-surface-200-800/60 transition-colors flex items-center gap-2 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:bg-transparent"
      >
        <Pencil size={12} /> Edit
      </button>

      <button
        onclick={fixWithAiFromMenu}
        disabled={nodeContextMenu.isSentinel}
        class="w-full text-left px-3 py-1.5 text-xs text-primary-400 hover:bg-surface-200-800/60 transition-colors flex items-center gap-2 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:bg-transparent"
      >
        <Wrench size={12} /> Fix with AI
      </button>

      <button
        onclick={askAiFromMenu}
        class="w-full text-left px-3 py-1.5 text-xs text-primary-400 hover:bg-surface-200-800/60 transition-colors flex items-center gap-2"
      >
        <MessageSquare size={12} /> Ask AI
      </button>

      <button
        onclick={duplicateFromMenu}
        disabled={nodeContextMenu.isSentinel}
        class="w-full text-left px-3 py-1.5 text-xs hover:bg-surface-200-800/60 transition-colors flex items-center gap-2 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:bg-transparent"
      >
        <Copy size={12} /> Duplicate
      </button>

      <div class="border-t border-surface-200-800 my-1"></div>

      <button
        onclick={deleteFromMenu}
        disabled={nodeContextMenu.isSentinel}
        class="w-full text-left px-3 py-1.5 text-xs text-error-300 hover:bg-error-500/10 transition-colors flex items-center gap-2 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:bg-transparent"
      >
        <Trash2 size={12} /> Delete
      </button>
    </div>
  {/if}

  <!-- Edge context menu -->
  {#if edgeContextMenu}
    <div
      class="fixed bg-surface-100-900 border border-surface-300-700 rounded-md shadow-xl z-50 py-1 min-w-[140px]"
      style="left: {edgeContextMenu.x}px; top: {edgeContextMenu.y}px;"
      onclick={(e) => e.stopPropagation()}
      onkeydown={(e) => { if (e.key === 'Escape') edgeContextMenu = null; }}
      role="menu"
      tabindex="-1"
    >
      <div class="px-3 py-1 text-[10px] uppercase tracking-wider text-surface-500">Edge type</div>
      <button onclick={() => setEdgeType('success')} class="w-full text-left px-3 py-1.5 text-xs text-success-400 hover:bg-surface-200-800/60 transition-colors">Success</button>
      <button onclick={() => setEdgeType('failure')} class="w-full text-left px-3 py-1.5 text-xs text-error-400 hover:bg-surface-200-800/60 transition-colors">Failure</button>
      <button onclick={() => setEdgeType('always')} class="w-full text-left px-3 py-1.5 text-xs text-primary-400 hover:bg-surface-200-800/60 transition-colors">Always</button>
      <button onclick={setEdgeConditional} class="w-full text-left px-3 py-1.5 text-xs text-warning-400 hover:bg-surface-200-800/60 transition-colors">Conditional…</button>
      <div class="border-t border-surface-200-800 my-1"></div>
      <button
        onclick={deleteEdgeFromMenu}
        class="w-full text-left px-3 py-1.5 text-xs text-error-300 hover:bg-error-500/10 transition-colors flex items-center gap-2"
      >
        <Trash2 size={12} /> Delete edge
      </button>
    </div>
  {/if}
</div>

<!-- Run dialog -->
<Dialog bind:open={showRunDialog} title="Run workflow" size="lg">
  <div class="space-y-4">
    {#if workflow?.input_schema && Object.keys(workflow.input_schema).length > 0}
      <div>
        <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 mb-2 inline-flex items-center gap-1">Runtime inputs <FieldHint id="workflow.run.inputs" /></h3>
        <JsonSchemaForm schema={workflow.input_schema} bind:value={runInput} />
      </div>
    {/if}

    <div>
      <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 mb-2">
        Target devices{workflowRequiresDevices ? '' : ' (optional)'}
        <FieldHint id="workflow.run.targets" />
      </h3>
      {#if !workflowRequiresDevices}
        <p class="text-[11px] text-surface-500 mb-2">
          No node in this workflow runs per device — leave empty to execute once without a device context.
        </p>
      {/if}
      <DevicePicker bind:selected={selectedDevices} environment={workflow?.environment ?? ''} />
    </div>
  </div>

  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showRunDialog = false)}>Cancel</Button>
    <Button variant="success" icon={Play} onclick={runWorkflow}>
      {#if selectedDevices.length > 0}
        Run on {selectedDevices.length} device{selectedDevices.length === 1 ? '' : 's'}
      {:else if workflowRequiresDevices}
        Run (no devices selected)
      {:else}
        Run without devices
      {/if}
    </Button>
  {/snippet}
</Dialog>

<!-- Run data inspector. Opens the most recent WorkflowRun + its steps for
     this workflow in a single scrollable dialog so the operator can eyeball
     payloads + errors without leaving the editor. -->
<Dialog bind:open={showRunDataDialog} title="Last run data" size="xl">
  {#if loadingRunData}
    <div class="py-10 flex justify-center"><Spinner size="lg" /></div>
  {:else if runDataError}
    <Alert tone="warning">{runDataError}</Alert>
  {:else if runDataRun}
    <div class="space-y-3 p-1">
      <div class="flex items-center justify-between gap-3 flex-wrap">
        <div class="min-w-0">
          <a
            href="/runs/{runDataRun.id}/monitor"
            class="text-sm font-mono text-primary-300 hover:underline"
          >Run {truncate(runDataRun.id, 12)}…</a>
          <div class="text-[11px] text-surface-500 mt-0.5">
            Trigger: {runDataRun.trigger} ·
            Started {formatDateTime(runDataRun.started_at)} ·
            Duration {formatDuration(runDataRun.started_at, runDataRun.completed_at)}
          </div>
        </div>
        <StatusBadge status={runDataRun.status} />
      </div>

      {#if runDataRun.input_payload && Object.keys(runDataRun.input_payload).length > 0}
        <div>
          <div class="text-[11px] font-semibold uppercase tracking-wide text-surface-500 mb-1">Run input</div>
          <pre class="bg-surface-100-900 rounded p-2 text-[11px] overflow-auto max-h-40">{JSON.stringify(runDataRun.input_payload, null, 2)}</pre>
        </div>
      {/if}

      <div>
        <div class="flex items-center justify-between mb-2">
          <div class="text-[11px] font-semibold uppercase tracking-wide text-surface-500">
            Steps ({runDataSteps.length})
          </div>
          <span class="text-[11px] text-error-300 tabular-nums">
            {runDataSteps.filter((s) => s.status === 'failed' || s.status === 'failure').length} failed
          </span>
        </div>
        {#if runDataSteps.length === 0}
          <div class="text-xs text-surface-500 py-4 text-center">No steps recorded.</div>
        {:else}
          <div class="space-y-1">
            {#each runDataSteps as step (step.id)}
              {@const open = runDataExpandedStep === step.id}
              <div class="rounded border border-surface-200-800/60">
                <button
                  type="button"
                  onclick={() => toggleRunDataStep(step.id)}
                  class="w-full text-left flex items-start gap-2 px-3 py-2 hover:bg-surface-200-800/40 transition-colors"
                >
                  {#if open}
                    <ChevronDown size={12} class="mt-1 shrink-0 text-surface-500" />
                  {:else}
                    <ChevronRight size={12} class="mt-1 shrink-0 text-surface-500" />
                  {/if}
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center justify-between gap-2">
                      <span class="text-xs font-mono text-surface-900-100 truncate">{step.node_id}</span>
                      <StatusBadge status={step.status} showDot={false} />
                    </div>
                    <div class="flex items-center gap-2 mt-0.5 text-[10px] text-surface-500 tabular-nums">
                      <span>{formatDateTime(step.started_at)}</span>
                      <span>·</span>
                      <span>{formatDuration(step.started_at, step.completed_at)}</span>
                      {#if step.device_id}
                        <span>·</span>
                        <span class="font-mono">{truncate(step.device_id, 8)}</span>
                      {/if}
                    </div>
                  </div>
                </button>

                {#if open}
                  <div class="border-t border-surface-200-800/60 px-3 py-2 space-y-2 text-xs bg-surface-50-950/60">
                    {#if step.error}
                      <div>
                        <div class="text-[10px] font-semibold uppercase tracking-wide text-error-300 mb-1">Error</div>
                        <pre class="bg-error-500/10 text-error-300 rounded p-2 text-[11px] overflow-auto max-h-32 whitespace-pre-wrap">{step.error}</pre>
                      </div>
                    {/if}
                    {#if step.input_payload && Object.keys(step.input_payload).length > 0}
                      <div>
                        <div class="text-[10px] font-semibold uppercase tracking-wide text-surface-500 mb-1">Input</div>
                        <pre class="bg-surface-100-900 rounded p-2 text-[11px] overflow-auto max-h-40">{JSON.stringify(step.input_payload, null, 2)}</pre>
                      </div>
                    {/if}
                    {#if step.output_payload && Object.keys(step.output_payload).length > 0}
                      <div>
                        <div class="text-[10px] font-semibold uppercase tracking-wide text-surface-500 mb-1">Output</div>
                        <pre class="bg-surface-100-900 rounded p-2 text-[11px] overflow-auto max-h-40">{JSON.stringify(step.output_payload, null, 2)}</pre>
                      </div>
                    {/if}
                    {#if step.logs}
                      <div>
                        <div class="text-[10px] font-semibold uppercase tracking-wide text-surface-500 mb-1">Logs</div>
                        <pre class="bg-surface-100-900 rounded p-2 text-[11px] overflow-auto max-h-32 whitespace-pre-wrap">{step.logs}</pre>
                      </div>
                    {/if}
                    {#if step.worker_id}
                      <div class="text-[10px] text-surface-500">Worker: <span class="font-mono text-surface-700-300">{step.worker_id}</span></div>
                    {/if}
                  </div>
                {/if}
              </div>
            {/each}
          </div>
        {/if}
      </div>
    </div>
  {/if}

  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showRunDataDialog = false)}>Close</Button>
    <Button variant="ghost" icon={RefreshCw} loading={loadingRunData} onclick={openRunDataDialog}>Refresh</Button>
    {#if runDataRun}
      <Button variant="primary" icon={Eye} href="/runs/{runDataRun.id}/monitor">Open full monitor</Button>
    {/if}
  {/snippet}
</Dialog>

<Dialog bind:open={showSimulateDialog} title="Workflow simulation" size="xl">
  {#if simulateLoading && !simulateResult}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Analysing DAG…" /></div>
  {:else if simulateError}
    <Alert tone="error">{simulateError}</Alert>
  {:else if simulateResult}
    <div class="space-y-4">
      <div class="flex items-center gap-2">
        <StatusBadge
          status={simulateResult.ok ? 'success' : 'failure'}
          label={simulateResult.ok ? 'Ready to run' : 'Needs attention'}
        />
        <span class="text-xs text-surface-500">
          env={simulateResult.environment} · v{simulateResult.version} · {simulateResult.node_count} node(s)
        </span>
      </div>

      {#if simulateResult.issues.length > 0}
        <div>
          <h4 class="text-xs font-semibold uppercase tracking-wide text-error-300 mb-2">
            Issues ({simulateResult.issue_count})
          </h4>
          <ul class="space-y-1 text-sm">
            {#each simulateResult.issues as issue}
              <li class="p-2 rounded bg-error-500/10 border border-error-500/30 font-mono text-xs">
                {JSON.stringify(issue)}
              </li>
            {/each}
          </ul>
        </div>
      {/if}

      {#if simulateResult.warnings.length > 0}
        <div>
          <h4 class="text-xs font-semibold uppercase tracking-wide text-warning-300 mb-2">
            Warnings ({simulateResult.warning_count})
          </h4>
          <ul class="space-y-1 text-sm">
            {#each simulateResult.warnings as warning}
              <li class="p-2 rounded bg-warning-500/10 border border-warning-500/30 font-mono text-xs">
                {JSON.stringify(warning)}
              </li>
            {/each}
          </ul>
        </div>
      {/if}

      {#if simulateResult.notes.length > 0}
        <div>
          <h4 class="text-xs font-semibold uppercase tracking-wide text-surface-500 mb-2">Not covered</h4>
          <ul class="space-y-1 text-xs text-surface-600-400 list-disc list-inside">
            {#each simulateResult.notes as note}<li>{note}</li>{/each}
          </ul>
        </div>
      {/if}
    </div>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showSimulateDialog = false)}>Close</Button>
    <Button variant="secondary" icon={RefreshCw} loading={simulateLoading} onclick={runSimulate}>Re-run</Button>
  {/snippet}
</Dialog>

<Dialog
  bind:open={showConditionalDialog}
  title="Conditional edge"
  description="The expression is evaluated against the source step's output. The edge fires only when it resolves to true."
  size="md"
  onClose={closeConditionalDialog}
>
  <div class="space-y-4 p-1">
    {#if conditionalError}
      <Alert tone="error">{conditionalError}</Alert>
    {/if}
    <Input
      label="Condition expression"
      help="edges.condition"
      placeholder="steps.classify.output.ssh_ok == true"
      bind:value={conditionalExpr}
      onkeydown={(e: KeyboardEvent) => { if (e.key === 'Enter') { e.preventDefault(); saveConditional(); } }}
    />
    <div class="text-xs text-surface-500 space-y-1">
      <p>Operators: <code>==</code>, <code>!=</code>, <code>&gt;=</code>, <code>&lt;=</code>, <code>&gt;</code>, <code>&lt;</code>, <code>&amp;&amp;</code>, <code>||</code>.</p>
      <p>Templates: <code>{'{{ steps.<node-id>.output.<path> }}'}</code> and <code>{'{{ device.<field> }}'}</code> resolve at runtime.</p>
      <p class="font-mono text-[11px] text-surface-600-400">
        Examples:<br />
        &nbsp;&nbsp;{'{{ steps.rest.output.status_code }}'} &gt;= 200 &amp;&amp; {'{{ steps.rest.output.status_code }}'} &lt; 300<br />
        &nbsp;&nbsp;{'{{ steps.classify.output.vendor }}'} == 'cisco_ios'
      </p>
    </div>
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={closeConditionalDialog}>Cancel</Button>
    <Button variant="primary" onclick={saveConditional}>Save</Button>
  {/snippet}
</Dialog>

<style>
  :global(.svelte-flow) { background-color: var(--color-surface-50-950); }

  /* "Saved" pill: small pop-in when the save succeeds. Keeps the success
     feedback noticeable without a full toast. */
  .save-pill {
    animation: save-pop 320ms cubic-bezier(0.34, 1.56, 0.64, 1);
  }
  @keyframes save-pop {
    0%   { opacity: 0; transform: scale(0.6) translateY(2px); }
    60%  { opacity: 1; transform: scale(1.08) translateY(0); }
    100% { opacity: 1; transform: scale(1) translateY(0); }
  }

  /* Subtle bobbing dot used on the Save button when there are unsaved
     changes. The Tailwind `animate-ping` ring handles the radial pulse;
     this just gives the inner dot a gentle scale loop so it feels alive
     instead of static. */
  .dirty-dot > span:last-child {
    animation: dirty-breathe 1.8s ease-in-out infinite;
  }
  @keyframes dirty-breathe {
    0%, 100% { transform: scale(1); }
    50%      { transform: scale(1.18); }
  }

  /* Honour user preference — kill the animation loop so we don't keep
     repainting for users who asked for reduced motion. */
  @media (prefers-reduced-motion: reduce) {
    .save-pill,
    .dirty-dot > span:last-child { animation: none; }
    :global(.dirty-dot .animate-ping) { animation: none; }
  }
</style>
