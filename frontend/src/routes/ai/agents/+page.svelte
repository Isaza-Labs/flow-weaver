<script lang="ts">
  // AI agent CRUD. An agent is a named role bound to a provider with a
  // tool allowlist, temperature, iteration cap, and an optional trailing
  // system prompt (the skill catalog is prepended automatically).
  //
  // `Role = "assistant"` is the one the chat controller falls back to
  // when no explicit agent_id is supplied. We prefill the seed-equivalent
  // values on "New" so the default experience stays close to the seeded
  // "Flow Weaver Assistant".

  import { onMount } from 'svelte';
  import {
    aiAgents, aiProviders,
    type AIAgent, type AIProvider, type AiToolInfo,
    errorMessage,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Input, Textarea, Select, Dialog, Alert,
    Spinner, EmptyState, StatusBadge, formatDateTime, toast, confirm, FieldHint,
  } from '$lib/components/ui';
  import {
    Bot, Plus, Pencil, Trash2, RefreshCw,
  } from 'lucide-svelte';

  // Same list the DefaultAgentSeedService ships with. Names that don't
  // exist in the running registry are filtered out server-side.
  const DEFAULT_TOOLS = [
    'list_workflows', 'list_services', 'query_devices', 'create_workflow_plan',
    'list_apis', 'discover_operations', 'operation_detail', 'execute_operation', 'load_skill',
    'get_run_details', 'get_step_logs', 'get_workflow_details', 'update_workflow_node_config',
  ];

  let rows = $state<AIAgent[]>([]);
  let providers = $state<AIProvider[]>([]);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  // Runtime tool catalog for the allowlist picker. When it can't be loaded
  // (endpoint failure) the form falls back to the original free-text textarea,
  // so agent editing never becomes impossible.
  let allTools = $state<AiToolInfo[]>([]);
  let toolsCatalogError = $state(false);
  let selectedTools = $state<string[]>([]);
  let toolFilter = $state('');

  const pickerAvailable = $derived(allTools.length > 0);
  const knownToolNames = $derived(new Set(allTools.map((t) => t.name)));
  // Entries on an existing agent that this server's registry doesn't know
  // (forward-looking or from another deploy). Kept visible + selected so an
  // edit doesn't silently drop them — unchecking is the explicit way out.
  const unknownSelected = $derived(selectedTools.filter((t) => !knownToolNames.has(t)));
  const filteredTools = $derived.by(() => {
    const q = toolFilter.trim().toLowerCase();
    if (!q) return allTools;
    return allTools.filter((t) =>
      t.name.toLowerCase().includes(q)
      || (t.description ?? '').toLowerCase().includes(q)
      || (t.domain ?? '').toLowerCase().includes(q));
  });

  let showDialog = $state(false);
  let editing = $state<AIAgent | null>(null);
  let name = $state('');
  let role = $state('assistant');
  let description = $state('');
  let providerId = $state('');
  let modelOverride = $state('gpt-5.4');
  let systemPrompt = $state('');
  let toolsText = $state(DEFAULT_TOOLS.join('\n'));
  let maxIterations = $state(10);
  let temperature = $state(0.2);
  let enabled = $state(true);
  let saving = $state(false);
  let formError = $state<string | null>(null);

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      const [agentsRes, providersRes, toolsRes] = await Promise.all([
        aiAgents.list(100, 0),
        aiProviders.list(100, 0),
        // Tolerated failure: the picker degrades to the free-text textarea.
        aiAgents.tools().catch(() => null),
      ]);
      rows = agentsRes.data;
      providers = providersRes.data;
      allTools = toolsRes ?? [];
      toolsCatalogError = toolsRes === null;
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load agents");
    } finally {
      loading = false;
    }
  }

  function openCreate() {
    editing = null;
    name = 'Flow Weaver Assistant';
    role = 'assistant';
    description = '';
    providerId = providers.find((p) => p.enabled)?.id ?? '';
    modelOverride = 'gpt-5.4';
    systemPrompt = '';
    toolsText = DEFAULT_TOOLS.join('\n');
    selectedTools = [...DEFAULT_TOOLS];
    toolFilter = '';
    maxIterations = 10;
    temperature = 0.2;
    enabled = true;
    formError = null;
    showDialog = true;
  }

  function openEdit(a: AIAgent) {
    editing = a;
    name = a.name;
    role = a.role || 'assistant';
    description = a.description ?? '';
    providerId = a.provider_id ?? '';
    modelOverride = a.model_override ?? '';
    systemPrompt = a.system_prompt ?? '';
    toolsText = (a.tools ?? []).join('\n');
    selectedTools = [...(a.tools ?? [])];
    toolFilter = '';
    maxIterations = a.max_iterations ?? 10;
    temperature = a.temperature ?? 0.2;
    enabled = a.enabled;
    formError = null;
    showDialog = true;
  }

  function closeDialog() {
    if (saving) return;
    showDialog = false;
    editing = null;
  }

  function parseTools(text: string): string[] {
    return text.split(/\r?\n|,/).map((s) => s.trim()).filter((s) => s.length > 0);
  }

  function toggleTool(toolName: string) {
    selectedTools = selectedTools.includes(toolName)
      ? selectedTools.filter((t) => t !== toolName)
      : [...selectedTools, toolName];
  }

  // Selects every tool currently visible (i.e. respecting the filter); with no
  // filter that's the whole catalog. Additive — already-selected stay selected.
  function selectAllFiltered() {
    selectedTools = [...new Set([...selectedTools, ...filteredTools.map((t) => t.name)])];
  }

  function clearSelection() {
    selectedTools = [];
  }

  function validate(): string | null {
    if (!name.trim()) return 'Name is required.';
    if (!role.trim()) return 'Role is required.';
    if (!providerId) return 'Provider is required.';
    if (maxIterations < 1 || maxIterations > 50) return 'Max iterations must be between 1 and 50.';
    if (temperature < 0 || temperature > 2) return 'Temperature must be between 0 and 2.';
    return null;
  }

  async function save() {
    if (saving) return;
    formError = validate();
    if (formError) return;

    saving = true;
    try {
      const body: Partial<AIAgent> = {
        name: name.trim(),
        role: role.trim(),
        description: description.trim() || null,
        provider_id: providerId,
        model_override: modelOverride.trim() || null,
        system_prompt: systemPrompt,
        // Picker mode sends the checked set; textarea fallback keeps the old
        // free-text parsing so the form works even without the tool catalog.
        tools: pickerAvailable ? [...selectedTools] : parseTools(toolsText),
        max_iterations: maxIterations,
        temperature,
        enabled,
      };
      if (editing) {
        await aiAgents.update(editing.id, body);
        toast.success('Agent updated');
      } else {
        await aiAgents.create(body);
        toast.success('Agent created');
      }
      showDialog = false;
      await load();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function del(a: AIAgent) {
    if (!(await confirm({
      title: `Delete agent "${a.name}"?`,
      message: 'Conversations it created will stay, but new chats will fall back to another agent.',
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await aiAgents.delete(a.id);
      toast.success('Agent deleted');
      await load();
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  function providerName(id: string | null): string {
    if (!id) return '—';
    return providers.find((p) => p.id === id)?.name ?? id.slice(0, 8);
  }
</script>

<svelte:head><title>AI Agents · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="AI Agents"
    description="Named roles the chat controller binds to. Each agent picks a provider, a model, a tool allowlist, and optional extra system prompt."
    breadcrumbs={[{ label: 'AI', href: '/ai' }, { label: 'Agents' }]}
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate} disabled={providers.length === 0}>New agent</Button>
    {/snippet}
  </PageHeader>

  {#if providers.length === 0 && !loading}
    <Alert tone="warning">
      No providers yet. <a href="/ai/providers" class="underline">Add one</a> before creating an agent.
    </Alert>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if rows.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Bot}
        title="No agents configured"
        description='The default "Flow Weaver Assistant" auto-seeds after a provider is added and the backend restarts. You can also create one manually here.'
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate} disabled={providers.length === 0}>New agent</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <table class="w-full text-sm">
        <thead class="border-b border-surface-200-800">
          <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
            <th class="px-4 py-2">Name</th>
            <th class="px-4 py-2">Role</th>
            <th class="px-4 py-2">Provider</th>
            <th class="px-4 py-2">Model</th>
            <th class="px-4 py-2">Tools</th>
            <th class="px-4 py-2">State</th>
            <th class="px-4 py-2">Updated</th>
            <th class="px-4 py-2 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each rows as a (a.id)}
            <tr class="border-b border-surface-200-800/50 hover:bg-surface-100-900/40">
              <td class="px-4 py-2 text-surface-900-100">{a.name}</td>
              <td class="px-4 py-2"><StatusBadge status="info" label={a.role || 'agent'} showDot={false} /></td>
              <td class="px-4 py-2 text-surface-700-300">{providerName(a.provider_id)}</td>
              <td class="px-4 py-2 font-mono text-xs text-surface-700-300">{a.model_override ?? '—'}</td>
              <td class="px-4 py-2 text-[11px] text-surface-500 tabular-nums">{(a.tools ?? []).length}</td>
              <td class="px-4 py-2">
                {#if a.enabled}
                  <StatusBadge status="success" label="enabled" showDot={false} />
                {:else}
                  <StatusBadge status="pending" label="disabled" showDot={false} />
                {/if}
              </td>
              <td class="px-4 py-2 text-surface-500 text-[11px] tabular-nums whitespace-nowrap">{formatDateTime(a.created_at)}</td>
              <td class="px-4 py-2">
                <div class="flex items-center justify-end gap-1">
                  <Button size="xs" variant="ghost" icon={Pencil} onclick={() => openEdit(a)}>Edit</Button>
                  <Button size="xs" variant="ghost" icon={Trash2} onclick={() => del(a)}>Delete</Button>
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </Card>
  {/if}
</div>

<Dialog bind:open={showDialog} title={editing ? 'Edit agent' : 'New agent'} size="lg">
  <div class="space-y-4 p-1">
    {#if formError}
      <Alert tone="error">{formError}</Alert>
    {/if}

    <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
      <Input label="Name" help="agents.name" bind:value={name} disabled={saving} placeholder="Flow Weaver Assistant" />
      <Input label="Role" help="agents.role" bind:value={role} disabled={saving} placeholder="assistant" />
    </div>

    <Textarea label="Description (optional)" help="agents.description" bind:value={description} disabled={saving} rows={2} />

    <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
      <Select label="Provider" help="agents.provider" bind:value={providerId} disabled={saving}>
        <option value="">— Select —</option>
        {#each providers as p (p.id)}
          <option value={p.id}>{p.name} ({p.type})</option>
        {/each}
      </Select>
      <Input label="Model override" help="agents.model_override" bind:value={modelOverride} disabled={saving} placeholder="gpt-5.4" />
    </div>

    <div>
      <Textarea
        label="Extra system prompt (appended after skills)"
        help="agents.extra_prompt"
        bind:value={systemPrompt}
        disabled={saving}
        rows={3}
        placeholder="Leave empty unless this agent needs per-role guidance beyond the skill catalog."
      />
      <p class="text-[11px] text-surface-500 mt-1">
        Skills + this prompt form the final system message. Keep it short.
      </p>
    </div>

    <div>
      {#if pickerAvailable}
        <div class="flex items-center justify-between mb-1">
          <span class="text-xs font-medium text-surface-600-400">Tool allowlist</span>
          <span class="text-[11px] text-surface-500 tabular-nums">
            {selectedTools.length} of {allTools.length} selected
          </span>
        </div>
        <div class="flex items-center gap-2 mb-2">
          <div class="flex-1">
            <Input bind:value={toolFilter} disabled={saving} placeholder="Filter tools…" />
          </div>
          <FieldHint id="agents.tool_checkbox" />
          <Button size="xs" variant="ghost" onclick={selectAllFiltered} disabled={saving}>Select all</Button>
          <Button size="xs" variant="ghost" onclick={clearSelection} disabled={saving || selectedTools.length === 0}>Clear</Button>
        </div>
        <div class="max-h-56 overflow-y-auto rounded-lg border border-surface-200-800 divide-y divide-surface-200-800/50">
          {#each filteredTools as t (t.name)}
            <label class="flex items-start gap-2 px-3 py-1.5 cursor-pointer hover:bg-surface-100-900/40">
              <input
                type="checkbox"
                class="accent-primary-500 mt-0.5"
                checked={selectedTools.includes(t.name)}
                onchange={() => toggleTool(t.name)}
                disabled={saving}
              />
              <span class="min-w-0 flex-1">
                <span class="font-mono text-xs text-surface-900-100">{t.name}</span>
                {#if t.tier && t.tier !== 'autonomous'}
                  <span class="ml-1.5 text-[10px] uppercase tracking-wide text-surface-500">{t.tier}</span>
                {/if}
                {#if t.description}
                  <span class="block text-[11px] text-surface-500 truncate" title={t.description}>{t.description}</span>
                {/if}
              </span>
            </label>
          {:else}
            <p class="px-3 py-2 text-[11px] text-surface-500">No tools match “{toolFilter}”.</p>
          {/each}
          {#each unknownSelected as toolName (toolName)}
            <!-- Inside {#each}, so the id carries the tool name: a literal one
                 would repeat per row and every label would bind to the first
                 checkbox. `for` is needed at all because <button> is labelable
                 and FieldHint renders one before the input. -->
            <label class="flex items-start gap-2 px-3 py-1.5 cursor-pointer hover:bg-surface-100-900/40" for="agent-tool-{toolName}">
              <FieldHint id="agents.tool_checkbox" />
              <input
                id="agent-tool-{toolName}"
                type="checkbox"
                class="accent-primary-500 mt-0.5"
                checked
                onchange={() => toggleTool(toolName)}
                disabled={saving}
              />
              <span class="min-w-0 flex-1">
                <span class="font-mono text-xs text-surface-900-100">{toolName}</span>
                <span class="ml-1.5 text-[10px] uppercase tracking-wide text-warning-500">not in registry</span>
              </span>
            </label>
          {/each}
        </div>
        <p class="text-[11px] text-surface-500 mt-1">
          Loaded from this server's tool registry — pick instead of typing. Entries kept from
          another deploy stay listed as “not in registry” and are filtered out at chat time.
        </p>
      {:else}
        <Textarea
          label="Tool allowlist (one per line, or comma-separated)"
          help="agents.tool_allowlist"
          bind:value={toolsText}
          disabled={saving}
          rows={6}
        />
        <p class="text-[11px] text-surface-500 mt-1">
          {#if toolsCatalogError}Couldn't load the tool catalog — enter names manually.
          {/if}Unknown names are filtered out at chat time — forward-looking entries are safe.
        </p>
      {/if}
    </div>

    <div class="grid grid-cols-1 md:grid-cols-3 gap-3 items-end">
      <Input label="Max iterations" help="agents.max_iterations" type="number" min={1} max={50} bind:value={maxIterations} disabled={saving} />
      <Input label="Temperature" help="agents.temperature" type="number" min={0} max={2} step={0.1} bind:value={temperature} disabled={saving} />
      <label class="inline-flex items-center gap-2 text-sm pb-2">
        <input type="checkbox" bind:checked={enabled} disabled={saving} class="accent-primary-500" />
        Enabled <FieldHint id="agents.enabled" />
      </label>
    </div>
  </div>

  {#snippet footer()}
    <Button variant="ghost" onclick={closeDialog} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={save} loading={saving}>{editing ? 'Save changes' : 'Create'}</Button>
  {/snippet}
</Dialog>
