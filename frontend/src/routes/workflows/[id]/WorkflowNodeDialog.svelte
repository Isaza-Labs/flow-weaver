<script lang="ts">
  // Replacement for the old right-side node panel. Three tabs:
  //   1. Node   — config_overrides for this node inside THIS workflow.
  //               Deferred save: applied to the in-memory flowNodes, the
  //               user still has to click the workflow's Save to persist.
  //   2. Service — name/description/code/timeout of the referenced
  //                Snippet. Immediate save (PUT /snippet)
  //                guarded by a second confirm modal (global impact).
  //   3. Schemas — input_schema + output_schema + retry_policy via the
  //                visual SchemaEditor. Immediate save + same confirm.
  //
  // Sentinels (__start__, __end__): renders a "not editable" message, no tabs.
  // Integration actions: Tab 1 is the integration form; Tabs 2/3 disabled.

  import { tick } from 'svelte';
  import {
    Dialog, Tabs, Button, Input, Textarea, Select, Alert, MermaidDiagram, toast, confirm,
    FieldHint,
  } from '$lib/components/ui';
  import JsonSchemaForm from '$lib/components/JsonSchemaForm.svelte';
  import ParamsEditor from '$lib/components/ParamsEditor.svelte';
  import TransformPlayground from '$lib/components/TransformPlayground.svelte';
  import SchemaEditor from '$lib/components/SchemaEditor.svelte';
  import {
    snippets, workflows, type Snippet, type Integration, type Workflow,
    type IntegrationActionWithIntegration, type McpServer, type McpTool, errorMessage,
  } from '$lib/api/client';
  import { authStore } from '$lib/stores/auth.svelte';
  import { AlertTriangle, Save, Check, ExternalLink, GitBranch } from 'lucide-svelte';
  import { lintConfig, type LintIssue } from '$lib/workflow/templates';

  // network_enabled (interactive-SSH sandbox) is admin-gated on the
  // backend, so only show the toggle to admins. Mirrors /snippets/[id].
  const isAdmin = $derived(authStore.session?.role === 'admin');

  type IntegrationAction = {
    id: string; name: string; method?: string; path?: string;
    query_params?: unknown[]; integration_id?: string; integration_name?: string;
  };

  let {
    open = $bindable(false),
    nodeId,
    initialConfig,
    service,
    integrationAction = null,
    isSentinel = false,
    isIntegrationAction = false,
    isSubflow = false,
    isTransform = false,
    isMcpCall = false,
    integrationList = [],
    integrationActions = [],
    mcpServerList = [],
    mcpTools = [],
    upstreamNodeIds = [],
    hasDeviceContext = true,
    hasInputContext = true,
    onNodeApply,
    onServiceRefresh,
  }: {
    open: boolean;
    nodeId: string | null;
    initialConfig: Record<string, unknown>;
    service: Snippet | null;
    integrationAction?: IntegrationAction | null;
    isSentinel?: boolean;
    isIntegrationAction?: boolean;
    isSubflow?: boolean;
    isTransform?: boolean;
    isMcpCall?: boolean;
    integrationList?: Integration[];
    integrationActions?: IntegrationActionWithIntegration[];
    mcpServerList?: McpServer[];
    mcpTools?: McpTool[];
    upstreamNodeIds?: string[];
    hasDeviceContext?: boolean;
    hasInputContext?: boolean;
    onNodeApply: (config: Record<string, unknown>) => void;
    onServiceRefresh: (updated: Snippet) => void;
  } = $props();

  // ─── Subflow picker state ──────────────────────────────────────────
  // Loaded once on first open of a subflow node. The list is bounded to
  // the tagged workflows so the cost is small enough to fetch
  // synchronously rather than paginate.
  //
  // `subflowsLoaded` is a one-shot flag separate from the array length:
  // the earlier guard `availableSubflows.length > 0` blew up when a
  // there were zero subflows tagged. Reading the array length inside
  // the effect created a reactive dependency, and assigning an empty
  // array re-triggered the effect on every fetch — turning the picker
  // into a request flood that tripped the rate limiter.
  let availableSubflows = $state<Workflow[]>([]);
  let subflowsLoading = $state(false);
  let subflowsLoaded = $state(false);
  let subflowsError = $state<string | null>(null);

  async function loadSubflows() {
    if (!isSubflow || subflowsLoading || subflowsLoaded) return;
    subflowsLoading = true;
    subflowsError = null;
    try {
      availableSubflows = await workflows.subflows();
      subflowsLoaded = true;
    } catch (e) {
      subflowsError = errorMessage(e);
    } finally {
      subflowsLoading = false;
    }
  }

  $effect(() => {
    if (open && isSubflow && !subflowsLoaded) void loadSubflows();
  });

  // ─── Tab state ──────────────────────────────────────────────────────

  type TabKey = 'node' | 'service' | 'schemas';
  let tab = $state<TabKey>('node');

  // ─── Tab 1: node draft ──────────────────────────────────────────────

  // Local working copy of the node's config_overrides. The parent feeds
  // us `initialConfig` on open; we mutate this draft locally and only
  // push back when the user hits Apply.
  let nodeDraft = $state<Record<string, unknown>>({});
  let transformExpression = $state('');
  let transformInput = $state('{}');

  // The selected subflow's input_schema drives the preview below. We
  // resolve from the loaded list to avoid a second fetch.
  // The canonical config_overrides key is `subflow_workflow_id` —
  // that's what DagParser/WorkflowExecutor read at enqueue time
  // (see Services/Engine/DagParser.cs and WorkflowExecutor.cs). The
  // companion `subflow_name` / `subflow_environment` keys are display
  // hints the canvas uses to render the chosen target without a second
  // fetch; the runtime ignores them.
  const selectedSubflow = $derived.by<Workflow | null>(() => {
    if (!isSubflow) return null;
    const id = nodeDraft.subflow_workflow_id as string | undefined;
    if (!id) return null;
    return availableSubflows.find((w) => w.id === id) ?? null;
  });

  // Re-hydrate drafts when the dialog opens for a new node.
  let lastSeenNodeId = $state<string | null>(null);
  $effect(() => {
    if (!open) return;
    if (nodeId === lastSeenNodeId) return;
    lastSeenNodeId = nodeId;
    nodeDraft = JSON.parse(JSON.stringify(initialConfig ?? {}));
    tab = 'node';
    if (isTransform) {
      transformExpression = (nodeDraft.expression as string) ?? '';
      transformInput = (nodeDraft._lastInput as string) ?? '{}';
    }
    serviceDraft = service ? { ...service } : null;
    schemasDraft = service ? {
      input_schema: JSON.parse(JSON.stringify(service.input_schema ?? {})),
      output_schema: JSON.parse(JSON.stringify(service.output_schema ?? {})),
      retry_policy: service.retry_policy
        ? JSON.stringify(service.retry_policy, null, 2)
        : '',
    } : { input_schema: {}, output_schema: {}, retry_policy: '' };
    retryPolicyError = null;
  });

  const nodeDirty = $derived(
    JSON.stringify(nodeDraft) !== JSON.stringify(initialConfig ?? {}),
  );

  // Lint every `{{ … }}` reference inside the current draft. Errors block
  // save; warnings do not (schemas are advisory). Mirrors the backend's
  // VariableResolver grammar so the runtime agrees with what we accept here.
  const templateIssues = $derived.by<Array<{ location: string; issue: LintIssue }>>(() => {
    if (!nodeDraft || isSentinel) return [];
    return lintConfig(nodeDraft, {
      upstreamNodeIds,
      hasDeviceContext,
      hasInputContext,
    });
  });
  const templateBlocking = $derived(templateIssues.some((x) => x.issue.severity === 'error'));

  function applyNode() {
    if (!nodeDirty) return;
    if (templateBlocking) {
      const first = templateIssues.find((x) => x.issue.severity === 'error');
      toast.error('Variable reference errors', {
        description: first?.issue.message ?? 'Fix template errors before applying.',
      });
      return;
    }
    onNodeApply(JSON.parse(JSON.stringify(nodeDraft)));
    toast.success('Node changes applied', {
      description: 'Save the workflow to persist.',
    });
  }

  function updateIntegrationParam(param: string, value: string) {
    const params = { ...(nodeDraft.params as Record<string, string> | undefined ?? {}), [param]: value };
    nodeDraft = { ...nodeDraft, params };
  }

  // ── MCP call writers ──
  // Changing the server invalidates the chosen tool + its arguments.
  function selectMcpServer(serverId: string) {
    nodeDraft = { ...nodeDraft, mcp_server_id: serverId, tool_name: '', arguments: {} };
  }
  function selectMcpTool(toolName: string) {
    if (toolName === nodeDraft.tool_name) return;
    // A different tool has a different input schema — drop the old arguments so
    // stale keys aren't silently persisted and sent at run time.
    nodeDraft = { ...nodeDraft, tool_name: toolName, arguments: {} };
  }
  // Bindable wrapper so ParamsEditor can two-way bind the `arguments` object —
  // `bind:value={nodeDraft.arguments}` (a member expression) isn't a writable
  // reference and throws props_invalid_value. Mirrors KeyValueEditor.makeChildBinding.
  function makeMcpArgsBinding() {
    return {
      get current() {
        const v = nodeDraft.arguments;
        return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : {};
      },
      set current(v: Record<string, unknown>) {
        nodeDraft = { ...nodeDraft, arguments: v };
      },
    };
  }

  // Apply an action pick to nodeDraft. Splices the action's id, method,
  // path, and parent integration name into config_overrides AND
  // redistributes any flat key/value pairs into params (path), query,
  // or body buckets so the canvas card + runtime dispatch both see the
  // same shape the backend would produce on auto-resolution. Empty
  // selection clears the resolved fields but keeps the integration_id
  // intact (user can pick a different action without re-dropping the
  // node).
  function applyActionSelection(actionId: string, candidates: IntegrationActionWithIntegration[]) {
    if (!actionId) {
      const { action_id: _a, method: _m, path: _p, ...rest } = nodeDraft;
      nodeDraft = rest;
      return;
    }
    const picked = candidates.find((a) => a.id === actionId);
    if (!picked) return;
    const method = picked.method ?? 'GET';
    const path = picked.path ?? '';
    const redistributed = redistributeForEndpoint(nodeDraft, method, path);
    nodeDraft = {
      ...redistributed,
      action_id: picked.id,
      action_name: picked.name,
      method,
      path,
      integration_id: picked.integration_id,
      integration_name: picked.integration_name,
    };
  }

  // Display-only / runtime-reserved keys we never redistribute. Must
  // mirror the backend's preservedRootKeys + the runtime bucket names.
  const PRESERVED_ROOT_KEYS = new Set([
    'action_id', 'action_name', 'method', 'path',
    'integration_id', 'integration_name',
    'summary', 'description', 'task_name', 'view', 'scheduled',
  ]);

  function parsePathParamNamesFor(path: string): Set<string> {
    const out = new Set<string>();
    const re = /\{([^}]+)\}/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(path)) !== null) {
      const name = m[1].trim();
      if (name) out.add(name);
    }
    return out;
  }

  // Bucket every non-reserved property of `draft` into params/query/body
  // depending on method + path. Existing `params`/`query`/`body` keys
  // are unwrapped and re-routed too so importing a GET that arrived
  // with the payload under `params` ends up with it under `query`.
  function redistributeForEndpoint(
    draft: Record<string, unknown>,
    method: string,
    path: string,
  ): Record<string, unknown> {
    const sendsBody = ['POST', 'PUT', 'PATCH'].includes(method.toUpperCase());
    const pathNames = parsePathParamNamesFor(path);
    const params: Record<string, unknown> = {};
    const query: Record<string, unknown> = {};
    const body: Record<string, unknown> = {};
    let existingBody: unknown = undefined;
    const next: Record<string, unknown> = {};

    function route(name: string, value: unknown) {
      if (pathNames.has(name)) params[name] = value;
      else if (sendsBody) body[name] = value;
      else query[name] = value;
    }

    for (const [k, v] of Object.entries(draft)) {
      if (PRESERVED_ROOT_KEYS.has(k)) {
        next[k] = v;
        continue;
      }
      if (k === 'params' && v && typeof v === 'object' && !Array.isArray(v)) {
        for (const [pk, pv] of Object.entries(v as Record<string, unknown>)) route(pk, pv);
        continue;
      }
      if (k === 'query' && v && typeof v === 'object' && !Array.isArray(v)) {
        for (const [qk, qv] of Object.entries(v as Record<string, unknown>)) query[qk] = qv;
        continue;
      }
      if (k === 'body') {
        existingBody = v;
        continue;
      }
      route(k, v);
    }

    if (Object.keys(params).length > 0) next.params = params;
    if (Object.keys(query).length > 0) next.query = query;
    if (existingBody !== undefined) next.body = existingBody;
    else if (Object.keys(body).length > 0) next.body = body;
    return next;
  }
  function updateIntegrationQuery(name: string, value: string) {
    const query = { ...(nodeDraft.query as Record<string, string> | undefined ?? {}), [name]: value };
    nodeDraft = { ...nodeDraft, query };
  }
  function updateIntegrationBody(raw: string) {
    try {
      nodeDraft = { ...nodeDraft, body: raw.trim() ? JSON.parse(raw) : {} };
    } catch {
      // Keep raw text while the user is mid-edit; validate on save.
      nodeDraft = { ...nodeDraft, _bodyRaw: raw };
    }
  }
  function saveTransform(expr: string) {
    transformExpression = expr;
    nodeDraft = { ...nodeDraft, expression: expr };
  }

  function parsePathParams(path: string): string[] {
    const out: string[] = [];
    const re = /\{([^}]+)\}/g;
    let m;
    while ((m = re.exec(path))) out.push(m[1]);
    return out;
  }

  // ─── Tab 2: service draft ───────────────────────────────────────────

  let serviceDraft = $state<Snippet | null>(null);
  let savingService = $state(false);

  const serviceDirty = $derived.by(() => {
    if (!service || !serviceDraft) return false;
    const fields: (keyof Snippet)[] = [
      'name', 'description', 'code', 'script_language',
      'target_mode', 'max_parallel', 'timeout_seconds',
      // S13.6 follow-up: per-snippet rollback policy override.
      'idempotency',
      // python_snippet interactive-SSH sandbox flag (admin-gated).
      'network_enabled',
    ];
    return fields.some((k) => (service[k] ?? '') !== (serviceDraft![k] ?? ''));
  });

  async function saveService() {
    if (!service || !serviceDraft || !serviceDirty) return;
    const ok = await confirm({
      title: 'Edit snippet?',
      message:
        `"${service.name}" is used by every workflow that references it. ` +
        `Changes take effect immediately on their next run.`,
      confirmLabel: 'Yes, save service',
      tone: 'danger',
    });
    if (!ok) return;

    savingService = true;
    try {
      const updated = await snippets.update(service.id, {
        name: serviceDraft.name,
        description: serviceDraft.description,
        code: serviceDraft.code,
        script_language: serviceDraft.script_language,
        target_mode: serviceDraft.target_mode,
        max_parallel: serviceDraft.max_parallel,
        timeout_seconds: serviceDraft.timeout_seconds,
        idempotency: serviceDraft.idempotency,
        network_enabled: serviceDraft.network_enabled,
      });
      onServiceRefresh(updated);
      toast.success('Service saved');
    } catch (e) {
      toast.fromError(e, "Couldn't save service");
    } finally {
      savingService = false;
    }
  }

  // ─── Tab 3: schemas draft ───────────────────────────────────────────

  let schemasDraft = $state<{
    input_schema: Record<string, unknown>;
    output_schema: Record<string, unknown>;
    retry_policy: string;
  }>({ input_schema: {}, output_schema: {}, retry_policy: '' });
  let retryPolicyError = $state<string | null>(null);
  let savingSchemas = $state(false);

  const schemasDirty = $derived.by(() => {
    if (!service) return false;
    if (JSON.stringify(schemasDraft.input_schema) !== JSON.stringify(service.input_schema ?? {})) return true;
    if (JSON.stringify(schemasDraft.output_schema) !== JSON.stringify(service.output_schema ?? {})) return true;
    const current = service.retry_policy ? JSON.stringify(service.retry_policy, null, 2) : '';
    if (schemasDraft.retry_policy.trim() !== current.trim()) return true;
    return false;
  });

  async function saveSchemas() {
    if (!service || !schemasDirty) return;
    retryPolicyError = null;

    let retry: Record<string, unknown> | null = null;
    if (schemasDraft.retry_policy.trim()) {
      try { retry = JSON.parse(schemasDraft.retry_policy); }
      catch (e) {
        retryPolicyError = e instanceof Error ? e.message : 'Invalid JSON';
        return;
      }
    }

    const ok = await confirm({
      title: 'Edit service schemas?',
      message:
        `Input / output schemas and the retry policy live on the service ` +
        `"${service.name}". Every workflow using it will see the change ` +
        `starting with its next run.`,
      confirmLabel: 'Yes, save schemas',
      tone: 'danger',
    });
    if (!ok) return;

    savingSchemas = true;
    try {
      const updated = await snippets.update(service.id, {
        input_schema: schemasDraft.input_schema,
        output_schema: schemasDraft.output_schema,
        retry_policy: retry,
      });
      onServiceRefresh(updated);
      toast.success('Schemas saved');
    } catch (e) {
      toast.fromError(e, "Couldn't save schemas");
    } finally {
      savingSchemas = false;
    }
  }

  // ─── Close handling (warn on dirty node draft) ──────────────────────

  async function handleClose() {
    if (nodeDirty) {
      const ok = await confirm({
        title: 'Discard node changes?',
        message: "You have unsaved config changes on this node. Close anyway?",
        confirmLabel: 'Discard',
        tone: 'danger',
      });
      if (!ok) return;
    }
    open = false;
    lastSeenNodeId = null;
  }

  // ─── integration-field filter helpers (integration-action, mirror old panel) ─

  function hasIntegrationFields(schema: Record<string, unknown>): boolean {
    const props = (schema?.properties ?? {}) as Record<string, unknown>;
    return 'netbox_url' in props || 'netbox_token' in props;
  }

  function filterIntegrationFields(schema: Record<string, unknown>): Record<string, unknown> {
    if (!schema?.properties) return schema;
    const props = { ...(schema.properties as Record<string, unknown>) };
    delete props.netbox_url;
    delete props.netbox_token;
    return { ...schema, properties: props };
  }

  // HTTP method badge tint (matches the old panel).
  const methodTone: Record<string, string> = {
    GET: 'bg-info-500/15 text-info-300',
    POST: 'bg-success-500/15 text-success-300',
    PUT: 'bg-warning-500/15 text-warning-300',
    PATCH: 'bg-warning-500/15 text-warning-300',
    DELETE: 'bg-error-500/15 text-error-300',
  };

  // Tabs wrapper accepts a `value` prop and a list; we guard here so
  // integration/sentinel branches render a collapsed layout without
  // breaking the generic component.
  const tabsConfig = $derived<{ value: TabKey; label: string; disabled?: boolean }[]>([
    { value: 'node', label: 'Node' },
    { value: 'service', label: 'Service', disabled: isIntegrationAction || isSubflow || isMcpCall || !service },
    { value: 'schemas', label: 'Schemas', disabled: isIntegrationAction || isSubflow || isMcpCall || !service },
  ]);

  // Svelte's Tabs component doesn't accept a `disabled` flag per entry —
  // it filters the list; but we want disabled tabs visible + greyed out.
  // Quick hack: only show enabled tabs (plus a hint below the header).
  const enabledTabs = $derived(tabsConfig.filter((t) => !t.disabled));
  const disabledTabNames = $derived(tabsConfig.filter((t) => t.disabled).map((t) => t.label));
</script>

<Dialog bind:open title="Node: {nodeId ?? ''}" size="xl" onClose={handleClose}>
  {#if isSentinel}
    <div class="py-4">
      <Alert tone="info">
        This is a sentinel node ({nodeId}). It marks the start or end of the
        DAG and has no configurable parameters. You can move it around on
        the canvas but nothing needs editing here.
      </Alert>
    </div>
  {:else}
    <!-- Tabs header -->
    <div class="flex items-center justify-between gap-3 mb-3 flex-wrap">
      <Tabs bind:value={tab} tabs={enabledTabs as { value: TabKey; label: string }[]} />
      {#if disabledTabNames.length > 0}
        <span class="text-[11px] text-surface-500">
          {disabledTabNames.join(' + ')} hidden — integration actions are managed in /integrations
        </span>
      {/if}
    </div>

    <!-- Tab 1: Node -->
    {#if tab === 'node'}
      <div class="space-y-3">
        {#if isSubflow}
          <!-- S14.1: subflow picker. Selecting a subflow stamps three
               keys onto config_overrides:
                 subflow_id          → workflow_id of the chosen subflow
                 subflow_name        → display name (cached for diffs)
                 subflow_environment → environment of the chosen subflow
               Plus the full input_schema preview helps the author wire
               their parent run's data into the subflow's expected shape. -->
          <div class="flex items-center gap-2 text-xs text-surface-500">
            <GitBranch size={14} />
            <span>Subflow node — pick a reusable workflow to invoke.</span>
          </div>

          {#if subflowsLoading}
            <Alert tone="info">Loading available subflows…</Alert>
          {:else if subflowsError}
            <Alert tone="error">{subflowsError}</Alert>
          {:else if availableSubflows.length === 0}
            <Alert tone="warning">
              No workflows tagged as subflows yet. Open any workflow's metadata
              and tick <code>is_subflow = true</code>, then it will show up here.
              <a class="text-primary-400 hover:underline ml-1" href="/subflows">View catalogue</a>
            </Alert>
          {:else}
            <Select
              label="Subflow"
              help="node.subflow"
              value={(nodeDraft.subflow_workflow_id as string) ?? ''}
              onchange={(e: Event) => {
                const target = e.target as HTMLSelectElement;
                const wf = availableSubflows.find((w) => w.id === target.value);
                if (wf) {
                  nodeDraft = {
                    ...nodeDraft,
                    subflow_workflow_id: wf.id,
                    subflow_name: wf.name,
                    subflow_environment: wf.environment,
                  };
                } else {
                  // Cleared selection.
                  const { subflow_workflow_id: _a, subflow_name: _b, subflow_environment: _c, ...rest } = nodeDraft;
                  nodeDraft = rest;
                }
              }}
            >
              <option value="">— choose a subflow —</option>
              {#each availableSubflows as wf}
                <option value={wf.id}>{wf.name} (v{wf.version} · {wf.environment})</option>
              {/each}
            </Select>
          {/if}

          {#if selectedSubflow}
            <div class="border-t border-surface-200-800 pt-3 space-y-2">
              <div class="flex items-center justify-between">
                <div class="text-xs font-semibold text-surface-700-300">Selected subflow</div>
                <a
                  href={`/workflows/${selectedSubflow.id}`}
                  target="_blank"
                  class="text-[11px] text-primary-400 hover:underline inline-flex items-center gap-1"
                >Open <ExternalLink size={10} /></a>
              </div>
              <div class="text-xs text-surface-700-300">
                <span class="font-mono">{selectedSubflow.name}</span>
                <span class="text-surface-500"> · v{selectedSubflow.version} · {selectedSubflow.environment}</span>
              </div>

              <div class="text-[10px] uppercase tracking-wider text-surface-500 pt-2">Inputs the subflow expects</div>
              <pre class="bg-surface-100-900 rounded p-2 text-[11px] overflow-auto max-h-48">{JSON.stringify(selectedSubflow.input_schema ?? {}, null, 2)}</pre>
              <p class="text-[11px] text-surface-500">
                Map upstream values to these fields under <code>input</code> in the config below
                (e.g. <code>&#123;&#123; steps.detect.output.device_id &#125;&#125;</code>).
              </p>
            </div>
          {/if}

          <!-- Always allow the user to also tweak raw config_overrides
               (e.g. the input mapping object) below the picker. -->
          <div class="border-t border-surface-200-800 pt-3 space-y-2">
            <div class="text-xs font-semibold text-surface-700-300">Input mapping</div>
            <Textarea
              rows={6}
              value={JSON.stringify((nodeDraft.input as Record<string, unknown>) ?? {}, null, 2)}
              oninput={(e: Event) => {
                const text = (e.target as HTMLTextAreaElement).value;
                try {
                  nodeDraft = { ...nodeDraft, input: JSON.parse(text) };
                } catch {
                  // Ignore invalid JSON until the user finishes editing.
                }
              }}
            />
            <p class="text-[11px] text-surface-500">JSON object passed as the subflow's <code>input_payload</code>.</p>
          </div>
        {:else if isIntegrationAction}
          {@const currentIntegrationId = (nodeDraft.integration_id as string | undefined) ?? ''}
          {@const currentActionId = (nodeDraft.action_id as string | undefined) ?? ''}
          {@const actionsForIntegration = currentIntegrationId
            ? integrationActions.filter((a) => a.integration_id === currentIntegrationId)
            : []}
          {@const actionResolved = !!currentActionId && actionsForIntegration.some((a) => a.id === currentActionId)}
          {@const resolvedIntegrationName = (nodeDraft.integration_name as string | undefined)
            ?? actionsForIntegration[0]?.integration_name
            ?? integrationList.find((i) => i.id === currentIntegrationId)?.name
            ?? 'Unknown'}

          <div>
            <div class="text-[10px] uppercase tracking-wider text-surface-500 mb-0.5">Integration</div>
            <div class="text-sm text-surface-900-100">{resolvedIntegrationName}</div>
          </div>

          <!--
            Action picker. Imported integration_action nodes routinely
            arrive with `action_name` (string label) but no `action_id`
            because foreign translators can't reach the DB. Without a
            picker the user had no way to wire the action without
            re-creating the node from the palette. We list every
            IntegrationAction under the current integration so the user
            can pick / change it inline; the selected row's id, method,
            path, and parent integration name get spliced into
            nodeDraft so the canvas + runtime stay in sync.
          -->
          <div>
            <div class="text-[10px] uppercase tracking-wider text-surface-500 mb-0.5 flex items-center justify-between gap-2">
              <span>Action</span>
              {#if !actionResolved}
                <span class="inline-flex items-center gap-1 text-[10px] text-warning-300">
                  <AlertTriangle size={10} />
                  pick one
                </span>
              {/if}
            </div>
            {#if !currentIntegrationId}
              <Alert tone="warning">
                <div class="text-xs">
                  No integration is wired on this node. Drop the action from the palette again to get a fresh
                  node, or set <code>integration_id</code> manually under <code>config_overrides</code>.
                </div>
              </Alert>
            {:else if actionsForIntegration.length === 0}
              <Alert tone="warning">
                <div class="text-xs">
                  Integration <strong>{resolvedIntegrationName}</strong> has no actions defined yet. Open
                  <a class="underline" href="/integrations/{currentIntegrationId}" target="_blank" rel="noreferrer">/integrations/{currentIntegrationId}</a>
                  to add one.
                </div>
              </Alert>
            {:else}
              <Select
                value={currentActionId}
                onchange={(e: Event) => applyActionSelection((e.target as HTMLSelectElement).value, actionsForIntegration)}
              >
                <option value="">— pick an action —</option>
                {#each actionsForIntegration as a}
                  <option value={a.id}>
                    {(a.method ?? 'GET')} {a.path ?? ''} — {a.name}
                  </option>
                {/each}
              </Select>
              {#if !actionResolved && nodeDraft.action_name}
                <p class="text-[11px] text-surface-500 mt-1">
                  Imported with action label <code class="font-mono">{nodeDraft.action_name}</code> but the runtime needs a concrete <code>action_id</code>. Pick the matching row above.
                </p>
              {/if}
            {/if}
          </div>

          <div>
            <div class="text-[10px] uppercase tracking-wider text-surface-500 mb-0.5">Endpoint</div>
            <div class="text-sm font-mono flex items-center gap-2">
              <span class="px-1.5 py-0.5 rounded text-[10px] font-semibold {methodTone[(nodeDraft.method as string) ?? 'GET'] ?? 'bg-surface-200-800'}">
                {nodeDraft.method ?? 'GET'}
              </span>
              <span class="text-surface-900-100">{nodeDraft.path ?? ''}</span>
            </div>
          </div>

          {#if parsePathParams((nodeDraft.path as string) ?? '').length > 0}
            <div class="border-t border-surface-200-800 pt-3 space-y-2">
              <div class="text-xs font-semibold text-surface-700-300">Path parameters</div>
              {#each parsePathParams((nodeDraft.path as string) ?? '') as param}
                <Input
                  label={param}
                  help="node.query_param"
                  value={(nodeDraft.params as Record<string, string> | undefined)?.[param] ?? ''}
                  oninput={(e: Event) => updateIntegrationParam(param, (e.target as HTMLInputElement).value)}
                />
              {/each}
            </div>
          {/if}

          {#if integrationAction?.query_params && Array.isArray(integrationAction.query_params) && integrationAction.query_params.length > 0}
            <div class="border-t border-surface-200-800 pt-3 space-y-2">
              <div class="text-xs font-semibold text-surface-700-300">Query parameters</div>
              {#each integrationAction.query_params as qp}
                <Input
                  label={`${(qp as { name: string }).name}${(qp as { required?: boolean }).required ? ' *' : ''}`}
                  help="node.query_param"
                  value={(nodeDraft.query as Record<string, string> | undefined)?.[(qp as { name: string }).name] ?? ''}
                  oninput={(e: Event) => updateIntegrationQuery((qp as { name: string }).name, (e.target as HTMLInputElement).value)}
                />
              {/each}
            </div>
          {/if}

          {#if ['POST', 'PUT', 'PATCH'].includes((nodeDraft.method as string) ?? '')}
            <div class="border-t border-surface-200-800 pt-3 space-y-2">
              <div class="text-xs font-semibold text-surface-700-300 inline-flex items-center gap-1">Request body <FieldHint id="action_test.body" /></div>
              <textarea
                value={JSON.stringify(nodeDraft.body ?? {}, null, 2)}
                oninput={(e) => updateIntegrationBody((e.target as HTMLTextAreaElement).value)}
                rows={8}
                class="w-full px-3 py-2 bg-surface-50-950 border border-surface-300-700 rounded-md text-xs text-surface-900-100 font-mono focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 resize-y"
              ></textarea>
            </div>
          {/if}

          {#if templateIssues.length > 0}
            <div class="border-t border-surface-200-800 pt-3">
              <div class="text-xs font-semibold text-surface-700-300 mb-1.5 flex items-center gap-1.5">
                <AlertTriangle size="14" class="text-warning-500" />
                Template references
              </div>
              <ul class="space-y-1">
                {#each templateIssues as { location, issue }}
                  <li class="text-[11px] flex items-start gap-1.5 {issue.severity === 'error' ? 'text-error-300' : 'text-warning-300'}">
                    <span class="font-mono text-surface-500 shrink-0">{location}</span>
                    <span class="font-mono text-surface-500 shrink-0">{issue.template.raw}</span>
                    <span>{issue.message}</span>
                  </li>
                {/each}
              </ul>
            </div>
          {/if}

          <details class="border-t border-surface-200-800 pt-3 text-[11px] text-surface-500">
            <summary class="cursor-pointer text-surface-700-300 font-semibold">Variable reference syntax</summary>
            <div class="mt-2 space-y-1.5">
              <div><code class="text-surface-700-300">&#123;&#123; steps.&lt;node-id&gt;.output.&lt;path&gt; &#125;&#125;</code> — upstream output (e.g. <code>steps.ssh.output.results[0].output</code>)</div>
              <div><code class="text-surface-700-300">&#123;&#123; steps.X.output.devices_by_name['router-1'].output.field &#125;&#125;</code> — per-device map by hostname</div>
              <div><code class="text-surface-700-300">&#123;&#123; steps.X.output.first_success.field &#125;&#125;</code> — first device where status == Completed</div>
              <div><code class="text-surface-700-300">&#123;&#123; device.&lt;path&gt; &#125;&#125;</code> — current device (per_device only)</div>
              <div><code class="text-surface-700-300">&#123;&#123; input.&lt;path&gt; &#125;&#125;</code> — workflow trigger payload</div>
              <div><code class="text-surface-700-300">&#123;&#123; run.&lt;path&gt; &#125;&#125;</code> — run metadata: <code>id</code>, <code>workflow_id</code>, <code>workflow_name</code>, <code>environment</code>, <code>trigger</code>, <code>started_at</code></div>
              <div class="pt-1"><span class="text-surface-700-300">Filters:</span> <code>| trim | upper | lower | truncate(80) | json | strip_ansi | strip | default('-')</code></div>
              <div class="text-surface-600-400">SSH stdout is ANSI-stripped automatically (raw bytes available under <code>output.stdout_raw</code>).</div>
            </div>
          </details>

        {:else if isMcpCall}
          {@const currentServerId = (nodeDraft.mcp_server_id as string | undefined) ?? ''}
          {@const currentToolName = (nodeDraft.tool_name as string | undefined) ?? ''}
          {@const toolsForServer = currentServerId ? mcpTools.filter((t) => t.mcp_server_id === currentServerId) : []}
          {@const selectedTool = toolsForServer.find((t) => t.name === currentToolName) ?? null}
          {@const argsBinding = makeMcpArgsBinding()}

          <Select label="MCP server" help="node.mcp_server" value={currentServerId} onchange={(e: Event) => selectMcpServer((e.target as HTMLSelectElement).value)}>
            <option value="">— pick a server —</option>
            {#each mcpServerList as s (s.mcp_server_id)}
              <option value={s.mcp_server_id}>{s.name}</option>
            {/each}
          </Select>

          {#if currentServerId}
            <div>
              {#if toolsForServer.length > 0}
                <Select label="Tool" help="node.mcp_tool" value={currentToolName} onchange={(e: Event) => selectMcpTool((e.target as HTMLSelectElement).value)}>
                  <option value="">— pick a tool —</option>
                  {#each toolsForServer as t (t.mcp_tool_id)}
                    <option value={t.name}>{t.name}</option>
                  {/each}
                </Select>
              {:else}
                <div class="text-xs font-medium text-surface-600-400 mb-1">Tool</div>
                <Alert tone="warning">No tools cached for this server yet — run “Test &amp; sync” on it in Govern → MCP servers.</Alert>
              {/if}
              {#if selectedTool?.description}
                <p class="text-[11px] text-surface-500 mt-1">{selectedTool.description}</p>
              {/if}
            </div>

            {#if currentToolName}
              <div>
                <div class="text-xs font-medium text-surface-600-400 mb-1">Arguments</div>
                <ParamsEditor schema={selectedTool?.input_schema ?? {}} bind:value={argsBinding.current} />
                <p class="text-[11px] text-surface-500 mt-1">
                  Use <code>&#123;&#123; steps.&lt;node-id&gt;.output.&lt;field&gt; &#125;&#125;</code> to pass an upstream output as an argument.
                </p>
              </div>
            {/if}
          {/if}

          {#if templateIssues.length > 0}
            <div class="border-t border-surface-200-800 pt-3">
              <div class="text-xs font-semibold text-surface-700-300 mb-1.5 flex items-center gap-1.5">
                <AlertTriangle size="14" class="text-warning-500" />
                Template references
              </div>
              <ul class="space-y-1">
                {#each templateIssues as { location, issue }}
                  <li class="text-[11px] flex items-start gap-1.5 {issue.severity === 'error' ? 'text-error-300' : 'text-warning-300'}">
                    <span class="font-mono text-surface-500 shrink-0">{location}</span>
                    <span class="font-mono text-surface-500 shrink-0">{issue.template.raw}</span>
                    <span>{issue.message}</span>
                  </li>
                {/each}
              </ul>
            </div>
          {/if}

        {:else if service}
          <div class="flex items-start justify-between gap-3">
            <div>
              <div class="text-[10px] uppercase tracking-wider text-surface-500 mb-0.5">Service</div>
              <div class="text-sm text-surface-900-100">{service.name}</div>
              <div class="text-[11px] text-surface-500">{service.type}</div>
            </div>
            <Button size="xs" variant="ghost" icon={ExternalLink} href="/snippets/{service.id}">
              Open in snippets
            </Button>
          </div>

          {#if service.description}
            <div class="text-xs text-surface-700-300">{service.description}</div>
          {/if}

          {#if service.logic_diagram_mermaid && service.logic_diagram_mermaid.trim()}
            <div class="border-t border-surface-200-800 pt-3">
              <div class="flex items-center justify-between mb-2">
                <div class="text-xs font-semibold text-surface-700-300">Logic diagram</div>
                <span class="text-[10px] text-surface-500">rendered from snippet.logic_diagram_mermaid</span>
              </div>
              <div class="rounded-md border border-surface-200-800 bg-surface-100-900/40 p-3">
                <MermaidDiagram source={service.logic_diagram_mermaid} />
              </div>
            </div>
          {/if}

          {#if isTransform}
            <div class="border-t border-surface-200-800 pt-3">
              <div class="text-xs font-semibold text-surface-700-300 mb-2">Transform playground</div>
              <div class="h-[400px]">
                <TransformPlayground
                  bind:inputData={transformInput}
                  bind:expression={transformExpression}
                  onSave={saveTransform}
                />
              </div>
            </div>
          {:else if service.input_schema && Object.keys(service.input_schema).length > 0}
            <div class="border-t border-surface-200-800 pt-3 space-y-3">
              {#if hasIntegrationFields(service.input_schema)}
                <div>
                  <div class="text-xs font-semibold text-surface-700-300 mb-1 inline-flex items-center gap-1">Integration connection <FieldHint id="integrations.name" /></div>
                  <select
                    value={(nodeDraft._integration_id as string) ?? ''}
                    onchange={(e) => {
                      const sel = e.currentTarget.value;
                      const integration = integrationList.find((a) => a.id === sel);
                      if (integration) {
                        nodeDraft = {
                          ...nodeDraft,
                          _integration_id: sel,
                          _integration_name: integration.name,
                          netbox_url: integration.base_url,
                          netbox_token: (integration.auth_config as Record<string, string> | undefined)?.token ?? '',
                        };
                      }
                    }}
                    class="w-full h-8 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm px-3 focus:outline-none focus:border-primary-500"
                  >
                    <option value="">Select integration…</option>
                    {#each integrationList as integration (integration.id)}
                      <option value={integration.id}>{integration.name} ({integration.type})</option>
                    {/each}
                  </select>
                </div>
              {/if}
              <div class="text-xs font-semibold text-surface-700-300">Parameters</div>
              <!-- Form ↔ JSON toggle. Form is the schema-driven UI (with
                   labels, required markers, enums, etc.); JSON is the
                   escape hatch for power users who want to paste / diff
                   the whole object. Both modes edit the same nodeDraft. -->
              <ParamsEditor
                schema={filterIntegrationFields(service.input_schema)}
                bind:value={nodeDraft}
              />
            </div>
          {:else}
            <div class="border-t border-surface-200-800 pt-3">
              <p class="text-xs text-surface-500 italic">No configurable parameters</p>
            </div>
          {/if}
        {:else}
          <p class="text-xs text-surface-500 italic">Snippet not found for this node.</p>
        {/if}
      </div>
    {/if}

    <!-- Tab 2: Service -->
    {#if tab === 'service' && serviceDraft}
      <div class="space-y-3">
        <Alert tone="warning">
          <div class="flex items-start gap-2 text-xs">
            <AlertTriangle size={14} class="mt-0.5 shrink-0" />
            <div>
              These fields live on the <strong>snippet</strong>.
              Changes affect every workflow that uses "{service?.name}" on
              its next run. You'll be asked to confirm before saving.
            </div>
          </div>
        </Alert>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
          <Input label="Name" help="node.name" bind:value={serviceDraft.name} />
          <Input label="Type" help="node.type" value={serviceDraft.type} disabled />
        </div>
        <!-- The Snippet stores description/code/script_language
             as `string | null`. Input/Textarea require string, so we
             coerce via an intermediate reactive handle per field. -->
        <Textarea
          label="Description"
          help="node.description"
          value={serviceDraft.description ?? ''}
          rows={2}
          oninput={(e) => serviceDraft!.description = (e.target as HTMLTextAreaElement).value}
        />
        {#if ['python_snippet', 'ansible_playbook', 'transform', 'jmespath'].includes(serviceDraft.type)}
          <Textarea
            label="Code"
            help="node.code"
            value={serviceDraft.code ?? ''}
            rows={10}
            oninput={(e) => serviceDraft!.code = (e.target as HTMLTextAreaElement).value}
          />
        {/if}
        <div class="grid grid-cols-1 md:grid-cols-3 gap-3">
          <Select label="Target mode" help="node.target_mode" bind:value={serviceDraft.target_mode}>
            <option value="once">once</option>
            <option value="per_device">per_device</option>
          </Select>
          <Input label="Timeout (seconds)" help="node.timeout" type="number" min={1} max={86400} bind:value={serviceDraft.timeout_seconds} />
          <Input label="Max parallel" help="node.max_parallel" type="number" min={1} max={1000} bind:value={serviceDraft.max_parallel} />
        </div>
        {#if serviceDraft.type === 'python_snippet'}
          <Select
            label="Script language"
            help="node.script_language"
            value={serviceDraft.script_language ?? 'python'}
            onchange={(e) => serviceDraft!.script_language = (e.target as HTMLSelectElement).value}
          >
            <option value="python">python</option>
            <option value="bash">bash</option>
          </Select>

          <!-- python_snippet only: lifts the sandbox network isolation for
               interactive SSH (netmiko/paramiko). Admin-gated by the backend;
               mirrors the toggle on /snippets/[id]. -->
          {#if isAdmin}
            <!-- `for` keeps the checkbox as the labeled control: <button> is
                 labelable, so FieldHint's info button would otherwise claim it
                 and clicking the row would toggle the tooltip, not the flag. -->
            <label class="flex items-start gap-2 text-sm text-surface-900-100" for="node-network-enabled">
              <FieldHint id="snippets.network_enabled" />
              <input
                id="node-network-enabled"
                type="checkbox"
                checked={serviceDraft.network_enabled ?? false}
                onchange={(e) => serviceDraft!.network_enabled = (e.target as HTMLInputElement).checked}
                class="mt-0.5"
              />
              <span>
                <span class="font-medium">Network-enabled · interactive SSH</span>
                <span class="block text-[11px] text-surface-500">
                  Lifts the sandbox network isolation so this snippet can use
                  netmiko/paramiko for interactive SSH (e.g. a password change that
                  prompts for confirmation). Only enable for trusted code — admin only.
                </span>
              </span>
            </label>
          {:else if serviceDraft.network_enabled}
            <p class="text-[11px] text-warning-300">
              This snippet is network-enabled (interactive SSH); only an admin can change it.
            </p>
          {/if}
        {/if}

        <!-- S13.6 follow-up: rollback policy override per snippet. The
             handler's DefaultIdempotency is the floor — picking a
             weaker value here is ignored by the analyzer when the
             handler ships NonReversible. See docs/handlers.md and
             docs/permissions / rollback policy. -->
        <div class="border-t border-surface-200-800 pt-3 space-y-1.5">
          <Select
            label="Rollback policy override"
            help="node.rollback_policy"
            value={serviceDraft.idempotency ?? ''}
            onchange={(e) => {
              const v = (e.target as HTMLSelectElement).value;
              serviceDraft!.idempotency = v === ''
                ? null
                : v as 'idempotent' | 'requires_compensation' | 'non_reversible';
            }}
          >
            <option value="">— inherit handler default —</option>
            <option value="idempotent">Idempotent (safe to retry; rollback always passes)</option>
            <option value="requires_compensation">Requires compensation (needs a failure edge)</option>
            <option value="non_reversible">Non-reversible (blocks rollback)</option>
          </Select>
          <p class="text-[11px] text-surface-500">
            The handler floor wins when stricter. Setting <code>idempotent</code> on a snippet
            of a non-reversible handler (e.g. <code>ssh</code>) has no effect.
          </p>
        </div>
      </div>
    {/if}

    <!-- Tab 3: Schemas -->
    {#if tab === 'schemas' && service}
      <div class="space-y-4">
        <Alert tone="warning">
          <div class="flex items-start gap-2 text-xs">
            <AlertTriangle size={14} class="mt-0.5 shrink-0" />
            <div>
              Schemas live on the <strong>service definition</strong>.
              Changes affect every workflow that uses "{service.name}".
            </div>
          </div>
        </Alert>

        <SchemaEditor title="Input schema" bind:value={schemasDraft.input_schema} />
        <SchemaEditor title="Output schema" bind:value={schemasDraft.output_schema} />

        <div>
          <Textarea
            label="Retry policy (raw JSON, leave empty for none)"
            help="node.retry_policy"
            bind:value={schemasDraft.retry_policy}
            rows={5}
          />
          {#if retryPolicyError}
            <div class="text-[11px] text-error-300 mt-1">{retryPolicyError}</div>
          {/if}
        </div>
      </div>
    {/if}
  {/if}

  {#snippet footer()}
    <Button variant="ghost" onclick={handleClose}>Close</Button>
    {#if isSentinel}
      <!-- Sentinel: close is the only action. -->
    {:else if tab === 'node'}
      <Button
        variant="primary"
        icon={Check}
        disabled={!nodeDirty || templateBlocking}
        onclick={applyNode}
      >
        {nodeDirty ? (templateBlocking ? 'Fix errors first' : 'Apply changes') : 'No changes'}
      </Button>
    {:else if tab === 'service'}
      <Button
        variant="primary"
        icon={Save}
        loading={savingService}
        disabled={!serviceDirty}
        onclick={saveService}
      >
        {serviceDirty ? 'Save service' : 'No changes'}
      </Button>
    {:else if tab === 'schemas'}
      <Button
        variant="primary"
        icon={Save}
        loading={savingSchemas}
        disabled={!schemasDirty}
        onclick={saveSchemas}
      >
        {schemasDirty ? 'Save schemas' : 'No changes'}
      </Button>
    {/if}
  {/snippet}
</Dialog>
