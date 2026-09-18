<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import {
    mcpServers,
    errorMessage,
    type McpServer,
    type McpTool,
    type McpAuthInput,
    type CreateMcpServerBody,
  } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Badge, Button, IconButton, Dialog,
    Input, Textarea, Select, Alert, Spinner, EmptyState, ErrorState, Checkbox,
    toast, confirm, formatDateTime,
  } from '$lib/components/ui';
  import { Plus, RefreshCw, Trash2, Pencil, RotateCw, ShieldCheck, Boxes, Plug } from 'lucide-svelte';

  const AUTH_TYPES = [
    { value: 'none', label: 'None' },
    { value: 'api_key', label: 'API key (header)' },
    { value: 'bearer', label: 'Bearer token' },
    { value: 'basic', label: 'Basic (username / password)' },
    { value: 'headers', label: 'Custom secret headers' },
    { value: 'oauth_client_credentials', label: 'OAuth — client credentials' },
    { value: 'oauth_authorization_code', label: 'OAuth — authorization code' },
  ];

  type ServerForm = {
    name: string;
    url: string;
    auth_type: string;
    api_key_header: string;
    api_key: string;
    token: string;
    username: string;
    password: string;
    secret_headers: string;
    client_id: string;
    client_secret: string;
    authorization_endpoint: string;
    token_endpoint: string;
    scopes: string;
    redirect_uri: string;
    tls_skip_verify: boolean;
    allow_private_network: boolean;
    enabled: boolean;
  };

  function emptyForm(): ServerForm {
    return {
      name: '', url: '', auth_type: 'none',
      api_key_header: '', api_key: '', token: '', username: '', password: '', secret_headers: '',
      client_id: '', client_secret: '', authorization_endpoint: '', token_endpoint: '',
      scopes: '', redirect_uri: '',
      tls_skip_verify: false, allow_private_network: false, enabled: true,
    };
  }

  let list = $state<McpServer[]>([]);
  let loading = $state(true);
  let loadError = $state<unknown>(null);

  let showForm = $state(false);
  let editing = $state<McpServer | null>(null);
  let form = $state<ServerForm>(emptyForm());
  let saving = $state(false);
  let formError = $state<string | null>(null);

  let busy = $state<Record<string, boolean>>({});
  let authorizing = $state<Record<string, boolean>>({});

  // Tools viewer dialog.
  let showTools = $state(false);
  let toolsFor = $state<McpServer | null>(null);
  let tools = $state<McpTool[]>([]);
  let toolsLoading = $state(false);

  const isOauth = $derived(form.auth_type.startsWith('oauth'));

  onMount(() => {
    // Complete the OAuth round-trip: the callback bounces back with a query flag.
    const authorized = page.url.searchParams.get('mcp_authorized');
    const err = page.url.searchParams.get('mcp_error');
    if (authorized) toast.success('MCP server authorized');
    if (err) toast.error('Authorization failed', { description: err });
    if (authorized || err) void goto('/admin/mcp-servers', { replaceState: true });
    void load();
  });

  async function load() {
    loading = true;
    loadError = null;
    try {
      const res = await mcpServers.list(200, 0);
      list = res.data;
    } catch (e) {
      loadError = e;
    } finally {
      loading = false;
    }
  }

  function openCreate() {
    editing = null;
    form = emptyForm();
    formError = null;
    showForm = true;
  }

  function openEdit(s: McpServer) {
    editing = s;
    form = {
      ...emptyForm(),
      name: s.name,
      url: s.url,
      auth_type: s.auth_type,
      // Non-secret fields are echoed, so pre-fill them (basic username, OAuth cfg).
      username: s.username ?? '',
      client_id: s.client_id ?? '',
      authorization_endpoint: s.authorization_endpoint ?? '',
      token_endpoint: s.token_endpoint ?? '',
      scopes: (s.scopes ?? []).join(' '),
      redirect_uri: s.redirect_uri ?? '',
      tls_skip_verify: s.tls_skip_verify,
      allow_private_network: s.allow_private_network,
      enabled: s.enabled,
    };
    formError = null;
    showForm = true;
  }

  function parseJsonObject(raw: string): Record<string, string> | null {
    if (!raw.trim()) return null;
    try {
      const v = JSON.parse(raw);
      return v && typeof v === 'object' && !Array.isArray(v) ? v : null;
    } catch {
      return null;
    }
  }

  function buildBody(): CreateMcpServerBody | null {
    const auth: McpAuthInput = {};
    if (form.auth_type === 'api_key') {
      if (form.api_key_header.trim()) auth.api_key_header = form.api_key_header.trim();
      if (form.api_key) auth.api_key = form.api_key;
    } else if (form.auth_type === 'bearer') {
      if (form.token) auth.token = form.token;
    } else if (form.auth_type === 'basic') {
      if (form.username.trim()) auth.username = form.username.trim();
      if (form.password) auth.password = form.password;
    } else if (form.auth_type === 'headers') {
      if (form.secret_headers.trim()) {
        const parsed = parseJsonObject(form.secret_headers);
        if (!parsed) { formError = 'Custom headers must be a JSON object of string values.'; return null; }
        auth.secret_headers = parsed;
      }
    } else if (isOauth) {
      if (form.client_id.trim()) auth.client_id = form.client_id.trim();
      if (form.client_secret) auth.client_secret = form.client_secret;
      if (form.authorization_endpoint.trim()) auth.authorization_endpoint = form.authorization_endpoint.trim();
      if (form.token_endpoint.trim()) auth.token_endpoint = form.token_endpoint.trim();
      if (form.redirect_uri.trim()) auth.redirect_uri = form.redirect_uri.trim();
      const scopes = form.scopes.split(/[\s,]+/).filter(Boolean);
      if (scopes.length) auth.scopes = scopes;
    }

    const body: CreateMcpServerBody = {
      name: form.name.trim(),
      url: form.url.trim(),
      auth_type: form.auth_type,
      tls_skip_verify: form.tls_skip_verify,
      allow_private_network: form.allow_private_network,
      enabled: form.enabled,
    };
    if (Object.keys(auth).length > 0) body.auth = auth;
    return body;
  }

  async function save(event?: SubmitEvent) {
    event?.preventDefault();
    if (saving) return;
    formError = null;
    if (!form.name.trim()) { formError = 'Name is required'; return; }
    if (!form.url.trim()) { formError = 'URL is required'; return; }
    const body = buildBody();
    if (!body) return;

    saving = true;
    try {
      if (editing) await mcpServers.update(editing.mcp_server_id, body);
      else await mcpServers.create(body);
      showForm = false;
      toast.success(editing ? 'MCP server updated' : 'MCP server created');
      await load();
    } catch (e) {
      formError = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function sync(s: McpServer) {
    busy = { ...busy, [s.mcp_server_id]: true };
    try {
      const res = await mcpServers.sync(s.mcp_server_id);
      if (res.status === 'ok') toast.success(`Synced ${res.tools_synced} tool${res.tools_synced === 1 ? '' : 's'}`);
      else toast.warning(`Server is ${res.status.replace(/_/g, ' ')}`, { description: res.error ?? undefined });
      await load();
    } catch (e) {
      toast.fromError(e, 'Test & sync failed');
    } finally {
      busy = { ...busy, [s.mcp_server_id]: false };
    }
  }

  async function authorize(s: McpServer) {
    authorizing = { ...authorizing, [s.mcp_server_id]: true };
    try {
      const { authorization_url } = await mcpServers.oauthStart(s.mcp_server_id);
      window.location.href = authorization_url;
    } catch (e) {
      toast.fromError(e, 'Could not start authorization');
      authorizing = { ...authorizing, [s.mcp_server_id]: false };
    }
  }

  async function viewTools(s: McpServer) {
    toolsFor = s;
    tools = [];
    toolsLoading = true;
    showTools = true;
    try {
      tools = await mcpServers.tools(s.mcp_server_id);
    } catch (e) {
      toast.fromError(e, 'Could not load tools');
    } finally {
      toolsLoading = false;
    }
  }

  async function remove(s: McpServer) {
    const ok = await confirm({
      title: `Delete ${s.name}?`,
      message: 'The server and its cached tools are removed. Workflows referencing it will fail.',
      confirmLabel: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await mcpServers.remove(s.mcp_server_id);
      list = list.filter((x) => x.mcp_server_id !== s.mcp_server_id);
      toast.success('Deleted');
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  function statusTone(s: string): 'success' | 'warning' | 'error' | 'neutral' {
    if (s === 'ok') return 'success';
    if (s === 'needs_config' || s === 'needs_authorization') return 'warning';
    if (s === 'unreachable') return 'error';
    return 'neutral';
  }

  function authLabel(s: McpServer): string {
    return AUTH_TYPES.find((a) => a.value === s.auth_type)?.label ?? s.auth_type;
  }

  // ── Tool parameter rendering ──
  // Flatten a tool's JSON-Schema input into a readable parameter list for the
  // tools dialog. The schema comes verbatim from the external MCP server, so we
  // defensively narrow every field instead of trusting its shape.
  type ToolParam = { name: string; type: string; required: boolean; description: string | null };

  function schemaType(p: Record<string, unknown>): string {
    if (Array.isArray(p.enum)) return p.enum.map((v) => JSON.stringify(v)).join(' | ');
    const t = p.type;
    let base =
      typeof t === 'string'
        ? t
        : Array.isArray(t)
          ? t.filter((x): x is string => typeof x === 'string').join(' | ')
          : '';
    if (base === 'array' && p.items && typeof p.items === 'object') {
      const inner = schemaType(p.items as Record<string, unknown>);
      if (inner) base = `array<${inner}>`;
    }
    if (!base) {
      const union = p.anyOf ?? p.oneOf;
      if (Array.isArray(union)) {
        base = union
          .map((m) => (m && typeof m === 'object' ? schemaType(m as Record<string, unknown>) : ''))
          .filter(Boolean)
          .join(' | ');
      }
    }
    if (base && typeof p.format === 'string') base = `${base} (${p.format})`;
    return base || 'any';
  }

  function toolParams(schema: Record<string, unknown> | null | undefined): ToolParam[] {
    const props = schema?.properties;
    if (!props || typeof props !== 'object') return [];
    const req = schema?.required;
    const required = new Set<string>(
      Array.isArray(req) ? req.filter((x): x is string => typeof x === 'string') : [],
    );
    return Object.entries(props as Record<string, unknown>).map(([name, raw]) => {
      const p = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
      return {
        name,
        type: schemaType(p),
        required: required.has(name),
        description: typeof p.description === 'string' ? p.description : null,
      };
    });
  }
</script>

<svelte:head><title>MCP servers · FlowWeaver</title></svelte:head>

<div class="p-6 space-y-6">
  <PageHeader
    title="MCP servers"
    description="Register external Model Context Protocol servers. The agent and the mcp_call workflow node can call their tools."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={openCreate}>Add server</Button>
    {/snippet}
  </PageHeader>

  {#if loading}
    <Spinner size="lg" label="Loading MCP servers…" />
  {:else if loadError}
    <Card padding="none"><ErrorState error={loadError} onRetry={load} /></Card>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={Plug}
        title="No MCP servers yet"
        description="Add a remote MCP server, then Test & sync to discover its tools."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={openCreate}>Add server</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <Card padding="none">
      <DataTable caption="MCP servers">
        <thead>
          <tr>
            <th>Name</th>
            <th>Auth</th>
            <th>Status</th>
            <th>Tools</th>
            <th>Synced</th>
            <th class="!text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each list as s (s.mcp_server_id)}
            <tr>
              <td>
                <div class="font-medium text-surface-900-100">{s.name}</div>
                <div class="text-xs text-surface-500 font-mono truncate max-w-xs" title={s.url}>{s.url}</div>
                {#if !s.enabled}<Badge tone="neutral" size="xs">disabled</Badge>{/if}
              </td>
              <td class="text-sm text-surface-600-400">{authLabel(s)}</td>
              <td><Badge tone={statusTone(s.status)}>{s.status.replace(/_/g, ' ')}</Badge></td>
              <td>
                <button
                  type="button"
                  class="text-sm text-primary-400 hover:underline disabled:text-surface-500 disabled:no-underline"
                  disabled={s.tool_count === 0}
                  onclick={() => viewTools(s)}
                >{s.tool_count}</button>
              </td>
              <td class="text-xs text-surface-500">{s.last_tools_synced_at ? formatDateTime(s.last_tools_synced_at) : '—'}</td>
              <td class="text-right">
                <div class="inline-flex items-center gap-1">
                  {#if s.auth_type === 'oauth_authorization_code'}
                    <IconButton
                      icon={ShieldCheck}
                      label={`Authorize ${s.name}`}
                      variant={s.status === 'needs_authorization' ? 'success' : 'ghost'}
                      loading={authorizing[s.mcp_server_id]}
                      onclick={() => authorize(s)}
                    />
                  {/if}
                  <IconButton icon={Boxes} label={`View ${s.name} tools`} onclick={() => viewTools(s)} />
                  <IconButton icon={RotateCw} label={`Test & sync ${s.name}`} loading={busy[s.mcp_server_id]} onclick={() => sync(s)} />
                  <IconButton icon={Pencil} label={`Edit ${s.name}`} onclick={() => openEdit(s)} />
                  <IconButton icon={Trash2} label={`Delete ${s.name}`} variant="danger" onclick={() => remove(s)} />
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
    </Card>
  {/if}
</div>

<!-- Create / edit -->
<Dialog bind:open={showForm} title={editing ? `Edit ${editing.name}` : 'Add MCP server'} size="lg">
  <form class="space-y-4 p-1" onsubmit={save}>
    {#if formError}<Alert tone="error">{formError}</Alert>{/if}

    <Input label="Name" help="mcp.name" placeholder="e.g. Acme search" bind:value={form.name} disabled={saving} />
    <Input label="Server URL" help="mcp.url" placeholder="https://mcp.example.com/mcp" bind:value={form.url} disabled={saving} />

    <Select label="Authentication" help="mcp.auth" bind:value={form.auth_type} disabled={saving}>
      {#each AUTH_TYPES as a (a.value)}<option value={a.value}>{a.label}</option>{/each}
    </Select>

    {#if form.auth_type === 'api_key'}
      <Input label="Header name" help="mcp.header_name" placeholder="X-API-Key" bind:value={form.api_key_header} disabled={saving} />
      <Input
        type="password" revealable
        label={editing && editing.has_api_key ? 'API key (leave blank to keep)' : 'API key'}
        help="mcp.header_name"
        bind:value={form.api_key} disabled={saving}
      />
    {:else if form.auth_type === 'bearer'}
      <Input
        type="password" revealable
        label={editing && editing.has_token ? 'Bearer token (leave blank to keep)' : 'Bearer token'}
        help="mcp.auth"
        bind:value={form.token} disabled={saving}
      />
    {:else if form.auth_type === 'basic'}
      <Input label="Username" help="mcp.username" placeholder="e.g. svc-flowweaver" bind:value={form.username} disabled={saving} />
      <Input
        type="password" revealable
        label={editing && editing.has_password ? 'Password (leave blank to keep)' : 'Password'}
        help="mcp.username"
        bind:value={form.password} disabled={saving}
      />
      <p class="text-[11px] text-surface-500">Sent as an <code class="font-mono">Authorization: Basic</code> header (base64 of user:password) on every request to the server.</p>
    {:else if form.auth_type === 'headers'}
      <Textarea
        label="Secret headers (JSON object)"
        help="mcp.secret_headers"
        hint={editing ? 'Leave blank to keep the stored headers.' : 'e.g. {"X-Api-Token": "…"}'}
        mono rows={4} bind:value={form.secret_headers} disabled={saving}
      />
    {:else if isOauth}
      <Input label="Client ID" help="mcp.client_id" bind:value={form.client_id} disabled={saving} />
      <Input
        type="password" revealable
        label={editing && editing.has_client_secret ? 'Client secret (leave blank to keep)' : 'Client secret'}
        help="mcp.client_id"
        hint={form.auth_type === 'oauth_authorization_code' ? 'Optional if the server supports dynamic client registration.' : undefined}
        bind:value={form.client_secret} disabled={saving}
      />
      <Input label="Token endpoint" help="mcp.token_endpoint" hint="Leave blank to auto-discover from the server." bind:value={form.token_endpoint} disabled={saving} />
      {#if form.auth_type === 'oauth_authorization_code'}
        <Input label="Authorization endpoint" help="mcp.authorization_endpoint" hint="Leave blank to auto-discover." bind:value={form.authorization_endpoint} disabled={saving} />
        <Input label="Redirect URI" help="mcp.redirect_uri" hint="Leave blank to use this backend's callback." bind:value={form.redirect_uri} disabled={saving} />
      {/if}
      <Input label="Scopes" help="mcp.scopes" placeholder="space-separated" bind:value={form.scopes} disabled={saving} />
      {#if form.auth_type === 'oauth_authorization_code'}
        <Alert tone="info">Save first, then use the <strong>Authorize</strong> button on the server row to complete consent.</Alert>
      {/if}
    {/if}

    <div class="flex flex-col items-start gap-2 pt-1">
      <Checkbox label="Enabled" help="mcp.enabled" bind:checked={form.enabled} disabled={saving} />
      <Checkbox label="Skip TLS verification (lab / dev only)" help="mcp.tls_skip_verify" bind:checked={form.tls_skip_verify} disabled={saving} />
      <Checkbox label="Allow private / loopback network (SSRF opt-out)" help="mcp.allow_private_network" bind:checked={form.allow_private_network} disabled={saving} />
    </div>
  </form>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showForm = false)} disabled={saving}>Cancel</Button>
    <Button variant="primary" onclick={() => save()} loading={saving}>{editing ? 'Save changes' : 'Create'}</Button>
  {/snippet}
</Dialog>

<!-- Tools viewer -->
<Dialog bind:open={showTools} title={toolsFor ? `${toolsFor.name} — tools` : 'Tools'} size="lg">
  {#if toolsLoading}
    <Spinner label="Loading tools…" />
  {:else if tools.length === 0}
    <EmptyState icon={Boxes} title="No tools cached" description="Run Test & sync on the server to discover its tools." />
  {:else}
    <div class="space-y-2 p-1">
      {#each tools as t (t.mcp_tool_id)}
        {@const params = toolParams(t.input_schema)}
        <div class="rounded-md border border-surface-200-800 p-3">
          <div class="flex flex-wrap items-center gap-2">
            <span class="font-mono font-medium text-surface-900-100">{t.name}</span>
            {#if t.title}<span class="text-sm text-surface-500">— {t.title}</span>{/if}
            {#if !t.enabled}<Badge tone="neutral" size="xs">disabled</Badge>{/if}
          </div>
          {#if t.description}<p class="text-sm text-surface-600-400 mt-1">{t.description}</p>{/if}

          <div class="mt-2 border-t border-surface-200-800 pt-2">
            <div class="text-[11px] font-semibold uppercase tracking-wide text-surface-500">Parameters</div>
            {#if params.length === 0}
              <p class="text-sm text-surface-500 italic mt-1">No parameters</p>
            {:else}
              <ul class="mt-1 space-y-1.5">
                {#each params as p (p.name)}
                  <li class="text-sm">
                    <div class="flex flex-wrap items-center gap-2">
                      <code class="font-mono font-medium text-surface-800-200">{p.name}</code>
                      <span class="rounded border border-surface-200-800 px-1.5 py-0.5 font-mono text-[11px] text-surface-600-400">{p.type}</span>
                      {#if p.required}<Badge tone="warning" size="xs">required</Badge>{/if}
                    </div>
                    {#if p.description}<p class="text-surface-500 mt-0.5">{p.description}</p>{/if}
                  </li>
                {/each}
              </ul>
            {/if}
          </div>
        </div>
      {/each}
    </div>
  {/if}
</Dialog>
