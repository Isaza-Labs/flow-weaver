<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { page } from '$app/state';
  import {
    integrations,
    integrationActionTest,
    errorMessage,
    type Integration,
    type IntegrationAction,
    type PromptSkillSummary,
    type ApiSpecSummary,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, IconButton, Badge, EmptyState, Alert, Spinner,
    Input, Select, Textarea, Dialog, StatusBadge, DataTable, confirm, toast, FieldHint,
  } from '$lib/components/ui';
  import { statusTone, toneDotClass } from '$lib/components/ui/status';
  import {
    Cable, Plus, ChevronDown, Trash2, HeartPulse, Plug, Play, Shield, Pencil,
    FileCode2, BookOpen, Upload,
  } from 'lucide-svelte';
  import NewIntegrationModal from './NewIntegrationModal.svelte';

  type AuthMethod = 'none' | 'token' | 'bearer' | 'basic' | 'api_key' | 'oauth2_client_credentials';

  let integrationList = $state<Integration[]>([]);
  let loading = $state(true);
  let error = $state('');

  let expanded = $state<string | null>(null);
  let actionsMap = $state<Record<string, IntegrationAction[]>>({});
  let actionsLoading = $state<Record<string, boolean>>({});
  let healthLoading = $state<Record<string, boolean>>({});
  let saveLoading = $state<Record<string, boolean>>({});

  // Per-integration scoped skills + specs, loaded lazily on expand.
  let bundleMap = $state<Record<string, { skills: PromptSkillSummary[]; specs: ApiSpecSummary[] }>>({});
  let bundleLoading = $state<Record<string, boolean>>({});

  type EditForm = {
    name: string;
    base_url: string;
    auth_method: AuthMethod;
    auth_token: string;
    auth_username: string;
    auth_password: string;
    auth_api_key: string;
    // oauth2_client_credentials: token endpoint + client credentials. The
    // access token itself is obtained and cached server-side at call time.
    auth_token_url: string;
    auth_client_id: string;
    auth_client_secret: string;
    auth_scope: string;
    // Strict health check (optional). Empty path = lenient mode (reachability
    // + credential verification). A path switches the probe to exactly
    // path+status — point it at a real, authenticated API endpoint.
    health_path: string;
    health_expected: string;
    tls_skip_verify: boolean;
    allow_private_network: boolean;
    // S13.3: justification text submitted with the audit row when the
    // SSRF guard opt-out is toggled. Empty when the form first loads;
    // the user fills it in when they flip the checkbox.
    allow_private_network_initial: boolean;
    allow_private_network_reason: string;
  };
  let editForms = $state<Record<string, EditForm>>({});

  let showNewModal = $state(false);


  const integrationTypes = ['netbox', 'servicenow', 'infoblox', 'paloalto', 'generic_rest'] as const;
  const authMethods: AuthMethod[] = ['none', 'token', 'bearer', 'basic', 'api_key', 'oauth2_client_credentials'];
  const integrationTypeTone: Record<string, 'primary' | 'success' | 'warning' | 'error' | 'neutral'> = {
    netbox: 'primary',
    servicenow: 'success',
    infoblox: 'primary',
    paloalto: 'error',
    generic_rest: 'neutral',
  };

  const methodTone: Record<string, 'primary' | 'success' | 'warning' | 'error' | 'neutral'> = {
    GET: 'success',
    POST: 'primary',
    PUT: 'warning',
    PATCH: 'warning',
    DELETE: 'error',
  };

  onMount(loadIntegrations);

  function guessAuthMethod(integration: Integration): AuthMethod {
    const cfg = integration.auth_config || {};
    // Empty auth_config → no credentials were set, treat as "none".
    // Matches how IntegrationAuthBuilder skips header injection.
    if (!cfg || Object.keys(cfg).length === 0) return 'none';
    const method = typeof cfg.method === 'string' ? cfg.method : '';
    if (method === 'none') return 'none';
    if (method === 'oauth2_client_credentials') return 'oauth2_client_credentials';
    // The auth builder treats oauth2 (static token) like bearer; surface both as "bearer".
    if (method === 'bearer' || method === 'oauth2') return 'bearer';
    if ('username' in cfg) return 'basic';
    if ('api_key' in cfg) return 'api_key';
    return 'token';
  }

  function initEditForm(i: Integration) {
    const method = guessAuthMethod(i);
    const cfg = i.auth_config || {};
    const hc = (i.health_check || {}) as Record<string, unknown>;
    editForms = {
      ...editForms,
      [i.id]: {
        name: i.name,
        base_url: i.base_url,
        auth_method: method,
        auth_token: (cfg.token as string) || '',
        auth_username: (cfg.username as string) || '',
        auth_password: (cfg.password as string) || '',
        auth_api_key: (cfg.api_key as string) || '',
        auth_token_url: (cfg.token_url as string) || '',
        auth_client_id: (cfg.client_id as string) || '',
        auth_client_secret: (cfg.client_secret as string) || '',
        auth_scope: (cfg.scope as string) || '',
        health_path: (hc.path as string) || '',
        health_expected: hc.expected_status ? String(hc.expected_status) : '',
        tls_skip_verify: i.tls_skip_verify,
        allow_private_network: i.allow_private_network,
        allow_private_network_initial: i.allow_private_network,
        allow_private_network_reason: '',
      },
    };
  }

  async function loadIntegrations() {
    loading = true;
    error = '';
    try {
      const res = await integrations.list();
      integrationList = res.data;
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load integrations');
    } finally {
      loading = false;
    }
    // Honour deep-link `?expand=<id>` (set by the /integrations/[id]
    // redirect). After the rows render, auto-expand the requested row
    // and scroll it into view — only once per navigation so a manual
    // collapse afterwards doesn't get fought.
    const targetId = page.url.searchParams.get('expand');
    if (targetId && integrationList.some((i) => i.id === targetId)) {
      const target = integrationList.find((i) => i.id === targetId)!;
      if (!editForms[targetId]) initEditForm(target);
      if (!actionsMap[targetId]) void loadActions(targetId);
      if (!bundleMap[targetId]) void loadBundle(targetId);
      expanded = targetId;
      await tick();
      document
        .querySelector<HTMLElement>(`[data-integration-row="${targetId}"]`)
        ?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  }

  async function loadActions(id: string) {
    actionsLoading = { ...actionsLoading, [id]: true };
    try {
      const a = await integrations.listActions(id);
      actionsMap = { ...actionsMap, [id]: a };
    } catch {
      actionsMap = { ...actionsMap, [id]: [] };
    } finally {
      actionsLoading = { ...actionsLoading, [id]: false };
    }
  }

  async function loadBundle(id: string) {
    bundleLoading = { ...bundleLoading, [id]: true };
    try {
      const b = await integrations.getBundle(id);
      bundleMap = { ...bundleMap, [id]: { skills: b.skills ?? [], specs: b.specs ?? [] } };
    } catch {
      bundleMap = { ...bundleMap, [id]: { skills: [], specs: [] } };
    } finally {
      bundleLoading = { ...bundleLoading, [id]: false };
    }
  }

  function toggleExpand(id: string) {
    if (expanded === id) { expanded = null; return; }
    expanded = id;
    const i = integrationList.find((x) => x.id === id);
    if (i && !editForms[id]) initEditForm(i);
    if (!actionsMap[id]) loadActions(id);
    if (!bundleMap[id]) loadBundle(id);
  }

  function authConfigFor(
    method: AuthMethod,
    fields: Pick<
      EditForm,
      | 'auth_token'
      | 'auth_username'
      | 'auth_password'
      | 'auth_api_key'
      | 'auth_token_url'
      | 'auth_client_id'
      | 'auth_client_secret'
      | 'auth_scope'
    >,
  ) {
    if (method === 'none') return {};
    if (method === 'token') return { method: 'token', token: fields.auth_token };
    if (method === 'bearer') return { method: 'bearer', token: fields.auth_token };
    if (method === 'basic')
      return { method: 'basic', username: fields.auth_username, password: fields.auth_password };
    if (method === 'oauth2_client_credentials')
      return {
        method: 'oauth2_client_credentials',
        token_url: fields.auth_token_url.trim(),
        client_id: fields.auth_client_id.trim(),
        client_secret: fields.auth_client_secret,
        scope: fields.auth_scope.trim(),
      };
    return { method: 'api_key', token: fields.auth_api_key };
  }

  async function handleSave(id: string) {
    const f = editForms[id];
    if (!f) return;
    // The health path is RELATIVE to the base URL — a pasted full URL would
    // concatenate into "<base>/https:/…" and probe garbage.
    if (f.health_path.includes('://')) {
      toast.error('Health check path must be relative', {
        description:
          'Use a path like /organizations — the base URL is prepended automatically. Remove the scheme and host.',
      });
      return;
    }
    // S13.3: when AllowPrivateNetwork is being TOGGLED ON, demand a
    // reason. When toggled OFF or unchanged, no reason is required —
    // the audit row still lands but with reason=null.
    const togglingOn = f.allow_private_network && !f.allow_private_network_initial;
    if (togglingOn && !f.allow_private_network_reason.trim()) {
      toast.error('Reason required', {
        description: 'Document why you are relaxing the SSRF guard for this integration.',
      });
      return;
    }
    saveLoading = { ...saveLoading, [id]: true };
    try {
      await integrations.update(id, {
        name: f.name,
        base_url: f.base_url,
        auth_config: authConfigFor(f.auth_method, f),
        // Empty path → `{}` = lenient probe; a path → strict mode with the
        // exact expected status (default 200).
        health_check: f.health_path.trim()
          ? {
              path: f.health_path.trim(),
              expected_status: parseInt(f.health_expected, 10) || 200,
            }
          : {},
        tls_skip_verify: f.tls_skip_verify,
        allow_private_network: f.allow_private_network,
        allow_private_network_reason: f.allow_private_network !== f.allow_private_network_initial
          ? f.allow_private_network_reason.trim() || null
          : null,
      });
      await loadIntegrations();
      toast.success('Integration saved');
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t save');
    } finally {
      saveLoading = { ...saveLoading, [id]: false };
    }
  }

  async function handleDelete(id: string) {
    if (!(await confirm({
      title: 'Delete integration?',
      message: 'This deletes the integration and all its actions. Scoped skills and specs stay in the catalog but lose their link.',
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await integrations.delete(id);
      if (expanded === id) expanded = null;
      await loadIntegrations();
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t delete');
    }
  }

  async function handleHealthCheck(id: string) {
    healthLoading = { ...healthLoading, [id]: true };
    try {
      const result = await integrations.healthCheck(id);
      integrationList = integrationList.map((i) =>
        i.id === id ? { ...i, status: result.status, last_checked_at: new Date().toISOString() } : i,
      );
      // The badge alone can't say WHY — surface the checker's explanation
      // (rejected credentials, or "reachable but token never validated").
      if (result.status === 'unhealthy') {
        toast.error('Integration unhealthy', { description: result.error });
      } else if (result.status === 'degraded') {
        toast.warning('Reachable, credentials not verified', { description: result.error });
      }
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Health check failed');
    } finally {
      healthLoading = { ...healthLoading, [id]: false };
    }
  }

  async function handleDeleteAction(integrationId: string, actionId: string) {
    if (!(await confirm({ title: 'Delete action?', tone: 'danger', confirmLabel: 'Delete' }))) return;
    try {
      await integrations.deleteAction(integrationId, actionId);
      await loadActions(integrationId);
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t delete action');
    }
  }

  // ─── Attach spec / skill dialogs ───────────────────────────────────
  // Loading a spec onto a live integration re-materializes its Actions
  // (the backend upserts actions by name); loading a skill scopes a prompt
  // fragment to it. Both re-fetch the integration's bundle on success, and
  // a spec attach also refreshes the Actions table.
  let specDialogOpen = $state(false);
  let specIntegrationId = $state('');
  let specApi = $state('');
  let specContent = $state('');
  let specLockApi = $state(false); // editing an existing spec keeps its api
  let specSaving = $state(false);
  let specError = $state<string | null>(null);

  function openSpecDialog(integrationId: string, existing?: ApiSpecSummary) {
    specIntegrationId = integrationId;
    specApi = existing?.api ?? '';
    specContent = existing?.content ?? '';
    specLockApi = !!existing;
    specError = null;
    specDialogOpen = true;
  }
  async function onSpecFile(ev: Event) {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    specContent = await file.text();
    if (!specApi) specApi = file.name.replace(/\.(ya?ml)$/i, '').toLowerCase();
    input.value = '';
  }
  async function saveSpec() {
    if (specSaving) return;
    const api = specApi.trim().toLowerCase();
    if (!/^[a-z0-9_-]+$/.test(api)) { specError = 'API id must be lowercase letters, digits, - or _.'; return; }
    if (!specContent.trim()) { specError = 'Content cannot be empty.'; return; }
    specSaving = true;
    specError = null;
    try {
      const res = await integrations.attachSpec(specIntegrationId, { api, content: specContent });
      specDialogOpen = false;
      toast.success('Spec attached', {
        description: res.spec_unparsed
          ? 'Saved, but no operations parsed — no actions materialized.'
          : `${res.actions_upserted} action(s) added/refreshed.`,
      });
      await Promise.all([loadActions(specIntegrationId), loadBundle(specIntegrationId)]);
    } catch (e) {
      specError = errorMessage(e);
    } finally {
      specSaving = false;
    }
  }

  let skillDialogOpen = $state(false);
  let skillIntegrationId = $state('');
  let skillName = $state('');
  let skillContent = $state('');
  let skillSaving = $state(false);
  let skillError = $state<string | null>(null);

  function openSkillDialog(integrationId: string, existing?: PromptSkillSummary) {
    skillIntegrationId = integrationId;
    skillName = existing?.name ?? '';
    skillContent = existing?.content ?? '';
    skillError = null;
    skillDialogOpen = true;
  }
  async function onSkillFile(ev: Event) {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    skillContent = await file.text();
    if (!skillName) skillName = file.name;
    input.value = '';
  }
  async function saveSkill() {
    if (skillSaving) return;
    const name = skillName.trim();
    if (!/^[a-zA-Z0-9_-]+\.md$/.test(name)) { skillError = 'Name must be filename-safe and end in .md (e.g. netbox.md).'; return; }
    if (!skillContent.trim()) { skillError = 'Content cannot be empty.'; return; }
    skillSaving = true;
    skillError = null;
    try {
      await integrations.attachSkill(skillIntegrationId, { name, content: skillContent });
      skillDialogOpen = false;
      toast.success('Skill attached');
      await loadBundle(skillIntegrationId);
    } catch (e) {
      skillError = errorMessage(e);
    } finally {
      skillSaving = false;
    }
  }

  function kb(n: number): string {
    return n < 1024 ? `${n} B` : `${(n / 1024).toFixed(1)} KB`;
  }

  // ─── Edit-action dialog ────────────────────────────────────────────
  // Imports leave stub actions with empty Path / Method=GET (or a
  // best-effort verb guess). The user has to backfill these before the
  // workflow can run, hence the inline edit dialog. JSON fields
  // (path_params, query_params, request_body, response_schema) are
  // edited as raw JSON textareas — parsed on save so a malformed
  // document never reaches the backend.
  const HTTP_METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'] as const;
  let editActionDialogOpen = $state(false);
  let editingAction = $state<IntegrationAction | null>(null);
  let editingIntegrationId = $state<string>('');
  let editActionForm = $state({
    name: '',
    description: '',
    method: 'GET',
    path: '',
    category: '',
    enabled: true,
    path_params_json: '[]',
    query_params_json: '[]',
    request_body_json: '{}',
    response_schema_json: '{}',
  });
  let editActionSaving = $state(false);
  let editActionError = $state<string | null>(null);

  function openEditActionDialog(integrationId: string, action: IntegrationAction) {
    editingAction = action;
    editingIntegrationId = integrationId;
    editActionForm = {
      name: action.name ?? '',
      description: action.description ?? '',
      method: (action.method ?? 'GET').toUpperCase(),
      path: action.path ?? '',
      category: action.category ?? '',
      enabled: action.enabled !== false,
      path_params_json: JSON.stringify(action.path_params ?? [], null, 2),
      query_params_json: JSON.stringify(action.query_params ?? [], null, 2),
      request_body_json: JSON.stringify(action.request_body ?? {}, null, 2),
      response_schema_json: JSON.stringify(action.response_schema ?? {}, null, 2),
    };
    editActionError = null;
    editActionDialogOpen = true;
  }

  // Each JSON textarea is parsed independently so the error message
  // can point at the specific field rather than a generic "bad JSON".
  function parseJsonField(label: string, raw: string): unknown {
    const trimmed = raw.trim();
    if (!trimmed) return label.endsWith('params') ? [] : {};
    try {
      return JSON.parse(trimmed);
    } catch (e) {
      throw new Error(`${label}: ${(e as Error).message}`);
    }
  }

  async function saveEditAction() {
    if (!editingAction) return;
    editActionSaving = true;
    editActionError = null;
    try {
      const path_params = parseJsonField('path_params', editActionForm.path_params_json);
      const query_params = parseJsonField('query_params', editActionForm.query_params_json);
      const request_body = parseJsonField('request_body', editActionForm.request_body_json);
      const response_schema = parseJsonField('response_schema', editActionForm.response_schema_json);

      if (!Array.isArray(path_params)) throw new Error('path_params must be a JSON array.');
      if (!Array.isArray(query_params)) throw new Error('query_params must be a JSON array.');

      await integrations.updateAction(editingAction.id, {
        name: editActionForm.name.trim() || editingAction.name,
        description: editActionForm.description,
        method: editActionForm.method,
        path: editActionForm.path,
        category: editActionForm.category,
        enabled: editActionForm.enabled,
        path_params: path_params as unknown[],
        query_params: query_params as unknown[],
        request_body: request_body as Record<string, unknown>,
        response_schema: response_schema as Record<string, unknown>,
      });
      await loadActions(editingIntegrationId);
      editActionDialogOpen = false;
      toast.success('Action updated');
    } catch (e) {
      editActionError = e instanceof Error ? e.message : errorMessage(e);
    } finally {
      editActionSaving = false;
    }
  }

  // FR-015/FR-016 — Test-action workspace. Clicking Play on an action
  // row opens a dialog with a JSON textarea for synthetic input and
  // fires the /api/integration-test/action/{id} endpoint (no step_run
  // persisted). Operator role gates the endpoint server-side, so
  // viewers see the button but get 403 if they try.
  let testDialogOpen = $state(false);
  let testingAction = $state<IntegrationAction | null>(null);
  let testBodyJson = $state('{}');
  let testParamsJson = $state('{}');
  let testQueryJson = $state('{}');
  let testRunning = $state(false);
  let testError = $state<string | null>(null);
  let testResult = $state<Awaited<ReturnType<typeof integrationActionTest.run>> | null>(null);

  function openTestDialog(action: IntegrationAction) {
    testingAction = action;
    testBodyJson = '{}';
    testParamsJson = '{}';
    testQueryJson = '{}';
    testError = null;
    testResult = null;
    testDialogOpen = true;
  }

  async function runTestAction() {
    if (!testingAction || testRunning) return;
    testRunning = true;
    testError = null;
    try {
      const body = JSON.parse(testBodyJson || '{}');
      const params = JSON.parse(testParamsJson || '{}');
      const query = JSON.parse(testQueryJson || '{}');
      testResult = await integrationActionTest.run(testingAction.id, { body, params, query });
    } catch (e) {
      testError = errorMessage(e);
    } finally {
      testRunning = false;
    }
  }
</script>

<svelte:head><title>Integrations · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Integrations"
    description="External REST systems your workflows and the AI agent can call. Each integration bundles a base URL, credentials, and (optionally) scoped skills and specs."
  >
    {#snippet actions()}
      <Button variant="primary" icon={Plus} onclick={() => (showNewModal = true)}>
        New integration
      </Button>
    {/snippet}
  </PageHeader>

  {#if error}<Alert tone="error" dismissible onDismiss={() => (error = '')}>{error}</Alert>{/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if integrationList.length === 0}
    <Card padding="none">
      <EmptyState icon={Cable} title="No integrations yet" description='Click "New integration" to add one.'>
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={() => (showNewModal = true)}>New integration</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <div class="space-y-2">
      {#each integrationList as integration (integration.id)}
        {@const isOpen = expanded === integration.id}
        <Card padding="none">
          <div data-integration-row={integration.id} class="scroll-mt-20">
          <button
            type="button"
            class="w-full flex items-center justify-between gap-3 px-4 h-12 text-left hover:bg-surface-200-800/30 transition-colors rounded-t-lg {isOpen ? '' : 'rounded-b-lg'}"
            onclick={() => toggleExpand(integration.id)}
          >
            <div class="flex items-center gap-3 min-w-0">
              <span class="w-2 h-2 rounded-full {toneDotClass(statusTone(integration.status))}" title={integration.status}></span>
              <span class="font-medium text-surface-900-100 truncate">{integration.name}</span>
              <Badge tone={integrationTypeTone[integration.type] ?? 'neutral'}>{integration.type}</Badge>
              <span class="text-xs text-surface-500 truncate hidden sm:inline font-mono">{integration.base_url}</span>
            </div>
            <ChevronDown size={14} class="text-surface-500 transition-transform {isOpen ? 'rotate-180' : ''}" />
          </button>

          {#if isOpen}
            <div class="border-t border-surface-200-800 px-4 py-4 space-y-4">
              <div class="flex flex-wrap gap-2">
                <Button size="sm" variant="success" icon={HeartPulse} onclick={() => handleHealthCheck(integration.id)} loading={healthLoading[integration.id]}>
                  Health check
                </Button>
                <Button size="sm" variant="ghost" icon={Shield} href={`/integrations/${integration.id}/permissions`}>Permissions</Button>
                <Button size="sm" variant="danger" icon={Trash2} onclick={() => handleDelete(integration.id)}>Delete</Button>
              </div>

              {#if editForms[integration.id]}
                {@const ef = editForms[integration.id]}
                <Card>
                  <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 mb-3 inline-flex items-center gap-1">Edit integration <FieldHint id="integrations.edit_section" /></h3>
                  <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
                    <Input label="Name" help="integrations.name" bind:value={ef.name} />
                    <Input label="Base URL" help="integrations.base_url" bind:value={ef.base_url} />
                    <Select label="Auth method" help="integrations.auth_method" bind:value={ef.auth_method}>
                      {#each authMethods as m}<option value={m}>{m}</option>{/each}
                    </Select>
                    <div>
                      {#if ef.auth_method === 'none'}
                        <div class="text-xs text-surface-500 pt-6">
                          No credentials sent on requests.
                        </div>
                      {:else if ef.auth_method === 'token' || ef.auth_method === 'bearer'}
                        <Input
                          label="Token"
                          help="integrations.auth_token"
                          type="password"
                          revealable
                          bind:value={ef.auth_token}
                          placeholder={ef.auth_method === 'bearer'
                            ? 'Authorization: Bearer <token>'
                            : 'Authorization: Token <token>'}
                        />
                      {:else if ef.auth_method === 'basic'}
                        <div class="space-y-2">
                          <Input label="Username" help="integrations.auth_username" bind:value={ef.auth_username} />
                          <Input label="Password" help="integrations.auth_password" type="password" revealable bind:value={ef.auth_password} />
                        </div>
                      {:else if ef.auth_method === 'oauth2_client_credentials'}
                        <div class="space-y-2">
                          <Input
                            label="Token URL"
                            help="integrations.auth_token_url"
                            bind:value={ef.auth_token_url}
                            placeholder="https://idp.example.com/oauth/token"
                          />
                          <Input label="Client ID" help="integrations.auth_client_id" bind:value={ef.auth_client_id} />
                          <Input label="Client secret" help="integrations.auth_client_secret" type="password" revealable bind:value={ef.auth_client_secret} />
                          <Input label="Scope (optional)" help="integrations.auth_scope" bind:value={ef.auth_scope} placeholder="read write" />
                          <div class="text-[11px] text-surface-500">
                            The access token is requested and cached server-side on each call — it is never stored in the integration.
                          </div>
                        </div>
                      {:else}
                        <Input label="API key" help="integrations.auth_api_key" type="password" revealable bind:value={ef.auth_api_key} />
                      {/if}
                    </div>
                    <div class="md:col-span-2 grid grid-cols-1 md:grid-cols-2 gap-3">
                      <Input
                        label="Health check path (optional)"
                        help="integrations.health_path"
                        bind:value={ef.health_path}
                        placeholder="/organizations — empty = lenient reachability probe"
                      />
                      <Input
                        label="Expected status"
                        help="integrations.health_expected"
                        type="number"
                        bind:value={ef.health_expected}
                        placeholder="200"
                      />
                      <div class="md:col-span-2 -mt-2 text-[11px] text-surface-500">
                        Point it at a real, authenticated API endpoint (relative to the base URL) so an
                        invalid token shows as unhealthy. Empty = generic probe, which can only prove
                        reachability on APIs whose root answers everyone.
                      </div>
                    </div>
                    <label class="flex items-center gap-2 md:col-span-2 cursor-pointer">
                      <input type="checkbox" bind:checked={ef.tls_skip_verify} class="rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-primary-500" />
                      <span class="text-sm text-surface-700-300 inline-flex items-center gap-1">Skip TLS verification <FieldHint id="integrations.tls_skip_verify" /></span>
                    </label>
                    <label class="flex items-start gap-2 md:col-span-2 cursor-pointer">
                      <input type="checkbox" bind:checked={ef.allow_private_network} class="mt-0.5 rounded border-surface-300-700 bg-surface-50-950 text-warning-500 focus:ring-warning-500" />
                      <span class="text-sm text-surface-700-300">
                        Allow private-network targets (10/8, 172.16/12, 192.168/16)
                        <FieldHint id="integrations.allow_private_network" />
                        <span class="block text-[11px] text-surface-500 mt-0.5">
                          Required for self-hosted NetBox, internal AWX, etc. Loopback and cloud-metadata IPs stay blocked.
                        </span>
                      </span>
                    </label>
                    {#if ef.allow_private_network !== ef.allow_private_network_initial}
                      <div class="md:col-span-2">
                        <Textarea
                          label={ef.allow_private_network ? 'Reason for enabling (required)' : 'Reason for disabling (optional)'}
                          help="integrations.private_network_reason"
                          bind:value={ef.allow_private_network_reason}
                          rows={2}
                          placeholder="e.g. NetBox runs at 10.0.5.42 — internal-only, ticket #SEC-1273"
                        />
                        <p class="text-[11px] text-surface-500 mt-1">
                          Stored in the audit row alongside the toggle. Visible in <a href="/admin/audit" class="text-primary-400 hover:underline">/admin/audit</a> under the AllowPrivateNetwork chip.
                        </p>
                      </div>
                    {/if}
                  </div>
                  <div class="mt-4 flex justify-end">
                    <Button variant="primary" onclick={() => handleSave(integration.id)} loading={saveLoading[integration.id]}>Save</Button>
                  </div>
                </Card>
              {/if}

              <!-- Skills & specs scoped to this integration -->
              <div>
                <div class="flex items-center justify-between mb-2">
                  <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 flex items-center gap-2">
                    <FileCode2 size={12} /> Skills &amp; specs <FieldHint id="integrations.bundle" />
                  </h3>
                  <div class="flex items-center gap-1">
                    <Button size="xs" variant="secondary" icon={BookOpen} onclick={() => openSkillDialog(integration.id)}>Load skill</Button>
                    <Button size="xs" variant="primary" icon={FileCode2} onclick={() => openSpecDialog(integration.id)}>Load spec</Button>
                  </div>
                </div>
                {#if bundleLoading[integration.id]}
                  <p class="text-sm text-surface-500">Loading…</p>
                {:else}
                  {@const bundle = bundleMap[integration.id] ?? { skills: [], specs: [] }}
                  {#if bundle.specs.length === 0 && bundle.skills.length === 0}
                    <p class="text-sm text-surface-500">
                      No scoped skills or specs yet. <em>Load spec</em> attaches an OpenAPI YAML and materializes its operations as the Actions below.
                    </p>
                  {:else}
                    <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
                      <div class="space-y-1">
                        <div class="text-[11px] uppercase tracking-wide text-surface-500">Specs</div>
                        {#if bundle.specs.length === 0}
                          <p class="text-xs text-surface-500">None.</p>
                        {:else}
                          {#each bundle.specs as s (s.ai_api_spec_id)}
                            <div class="flex items-center gap-2 rounded-md border border-surface-200-800 px-2 py-1.5">
                              <FileCode2 size={12} class="text-surface-500 shrink-0" />
                              <span class="font-mono text-xs text-surface-900-100 truncate">{s.api}</span>
                              <Badge tone="neutral">{s.operation_count} ops</Badge>
                              <span class="text-[11px] text-surface-500 ml-auto">{kb(s.size_bytes)}</span>
                              <IconButton icon={Pencil} label="Edit spec {s.api}" variant="secondary" onclick={() => openSpecDialog(integration.id, s)} />
                            </div>
                          {/each}
                        {/if}
                      </div>
                      <div class="space-y-1">
                        <div class="text-[11px] uppercase tracking-wide text-surface-500">Skills</div>
                        {#if bundle.skills.length === 0}
                          <p class="text-xs text-surface-500">None.</p>
                        {:else}
                          {#each bundle.skills as sk (sk.ai_prompt_skill_id)}
                            <div class="flex items-center gap-2 rounded-md border border-surface-200-800 px-2 py-1.5">
                              <BookOpen size={12} class="text-surface-500 shrink-0" />
                              <span class="font-mono text-xs text-surface-900-100 truncate">{sk.name}</span>
                              <span class="text-[11px] text-surface-500 ml-auto">{kb(sk.size_bytes)}</span>
                              <IconButton icon={Pencil} label="Edit skill {sk.name}" variant="secondary" onclick={() => openSkillDialog(integration.id, sk)} />
                            </div>
                          {/each}
                        {/if}
                      </div>
                    </div>
                  {/if}
                {/if}
              </div>

              <div>
                <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 mb-2 flex items-center gap-2">
                  <Plug size={12} /> Actions <FieldHint id="integrations.actions_section" />
                </h3>
                {#if actionsLoading[integration.id]}
                  <p class="text-sm text-surface-500">Loading actions…</p>
                {:else if !actionsMap[integration.id] || actionsMap[integration.id].length === 0}
                  <p class="text-sm text-surface-500">No actions yet. Click <em>Load spec</em> above to attach an OpenAPI spec and materialize its operations as actions.</p>
                {:else}
                  <DataTable>
                    <thead>
                      <tr>
                        <th>Method</th>
                        <th>Path</th>
                        <th>Name</th>
                        <th>Category</th>
                        <th class="!text-right">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {#each actionsMap[integration.id] as action (action.id)}
                        <tr>
                          <td><Badge tone={methodTone[action.method] ?? 'neutral'} mono>{action.method}</Badge></td>
                          <td class="font-mono text-xs text-surface-700-300">{action.path}</td>
                          <td class="text-surface-700-300">{action.name}</td>
                          <td class="text-xs text-surface-500">{action.category || '—'}</td>
                          <td class="text-right">
                            <div class="inline-flex items-center gap-1">
                              <IconButton icon={Pencil} label="Edit action" variant="secondary" onclick={() => openEditActionDialog(integration.id, action)} />
                              <IconButton icon={Play} label="Test action" variant="secondary" onclick={() => openTestDialog(action)} />
                              <IconButton icon={Trash2} label="Delete" variant="danger" onclick={() => handleDeleteAction(integration.id, action.id)} />
                            </div>
                          </td>
                        </tr>
                      {/each}
                    </tbody>
                  </DataTable>
                {/if}
              </div>
            </div>
          {/if}
          </div>
        </Card>
      {/each}
    </div>
  {/if}
</div>

<NewIntegrationModal bind:open={showNewModal} onCreated={loadIntegrations} />

<Dialog bind:open={editActionDialogOpen} title={editingAction ? `Edit action: ${editingAction.name}` : 'Edit action'} size="lg">
  {#if editingAction}
    <div class="space-y-3">
      <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Input label="Name" help="integrations.action_name" bind:value={editActionForm.name} />
        <div class="flex flex-col gap-1">
          <label for="edit-action-method" class="text-xs font-medium text-surface-600-400 inline-flex items-center gap-1">Method <FieldHint id="integrations.action_method" /></label>
          <select
            id="edit-action-method"
            bind:value={editActionForm.method}
            class="h-8 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm px-3 focus:outline-none focus:border-primary-500"
          >
            {#each HTTP_METHODS as m}
              <option value={m}>{m}</option>
            {/each}
          </select>
        </div>
      </div>
      <Input label="Path" help="integrations.action_path" placeholder="/v1/notify" bind:value={editActionForm.path} />
      <Textarea label="Description" help="integrations.action_description" rows={2} bind:value={editActionForm.description} />
      <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Input label="Category" help="integrations.action_category" bind:value={editActionForm.category} placeholder="imported, general, …" />
        <label class="flex items-center gap-2 text-xs font-medium text-surface-700-300 mt-5">
          <input type="checkbox" bind:checked={editActionForm.enabled} />
          <span class="inline-flex items-center gap-1">Enabled (visible in the workflow action picker) <FieldHint id="integrations.action_enabled" /></span>
        </label>
      </div>

      <details class="border border-surface-300-700/50 rounded-md p-2 text-xs">
        <summary class="cursor-pointer text-surface-700-300 font-semibold">Path parameters (JSON array)</summary>
        <Textarea bind:value={editActionForm.path_params_json} rows={4} mono />
        <p class="text-[10px] text-surface-500 mt-1">Each entry should be <code>{'{ "name": "id", "required": true }'}</code>.</p>
      </details>

      <details class="border border-surface-300-700/50 rounded-md p-2 text-xs">
        <summary class="cursor-pointer text-surface-700-300 font-semibold">Query parameters (JSON array)</summary>
        <Textarea bind:value={editActionForm.query_params_json} rows={4} mono />
      </details>

      <details class="border border-surface-300-700/50 rounded-md p-2 text-xs">
        <summary class="cursor-pointer text-surface-700-300 font-semibold">Request body (JSON Schema)</summary>
        <Textarea bind:value={editActionForm.request_body_json} rows={6} mono />
      </details>

      <details class="border border-surface-300-700/50 rounded-md p-2 text-xs">
        <summary class="cursor-pointer text-surface-700-300 font-semibold">Response schema (JSON Schema)</summary>
        <Textarea bind:value={editActionForm.response_schema_json} rows={6} mono />
      </details>

      {#if editActionError}
        <Alert tone="error">{editActionError}</Alert>
      {/if}
    </div>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (editActionDialogOpen = false)} disabled={editActionSaving}>Cancel</Button>
    <Button variant="primary" onclick={saveEditAction} loading={editActionSaving}>Save action</Button>
  {/snippet}
</Dialog>

<Dialog bind:open={testDialogOpen} title={testingAction ? `Test: ${testingAction.method} ${testingAction.path}` : 'Test action'} size="xl">
  {#if testingAction}
    <div class="space-y-3">
      <p class="text-xs text-surface-500">
        Fires the action once with the inputs below and returns the resolved URL,
        applied headers (secrets redacted), and the upstream response. Nothing is
        persisted — no step_run, no audit row.
      </p>
      <div class="grid grid-cols-1 md:grid-cols-3 gap-3">
        <div><Textarea label="body (JSON)" help="action_test.body" bind:value={testBodyJson} rows={8} mono /></div>
        <div><Textarea label="path params (JSON)" help="action_test.path_params" bind:value={testParamsJson} rows={8} mono /></div>
        <div><Textarea label="query (JSON)" help="action_test.query" bind:value={testQueryJson} rows={8} mono /></div>
      </div>

      {#if testError}<Alert tone="error">{testError}</Alert>{/if}

      {#if testRunning}
        <div class="py-6 flex justify-center"><Spinner label="Calling upstream…" /></div>
      {:else if testResult}
        <div class="space-y-3">
          <div class="text-xs text-surface-500">
            Resolved URL:
            <code class="text-primary-400 break-all">{testResult.resolved_url}</code>
          </div>
          {#if testResult.transport_error}
            <Alert tone="error">Transport: {testResult.transport_error}</Alert>
          {:else if testResult.response}
            <div class="flex items-center gap-2">
              <StatusBadge
                status={testResult.response.status_code < 400 ? 'success' : 'failure'}
                label={`HTTP ${testResult.response.status_code}`}
              />
            </div>
            <div>
              <h4 class="text-xs font-semibold uppercase tracking-wide text-surface-500 mb-1">Response body</h4>
              <pre class="text-xs p-2 rounded bg-surface-100-900 border border-surface-300-700 max-h-64 overflow-auto whitespace-pre-wrap">{testResult.response.body}</pre>
            </div>
          {/if}
          <details>
            <summary class="text-xs text-surface-500 cursor-pointer">Applied request headers (redacted)</summary>
            <pre class="mt-1 text-xs p-2 rounded bg-surface-100-900 border border-surface-300-700">{JSON.stringify(testResult.applied_headers, null, 2)}</pre>
          </details>
        </div>
      {/if}
    </div>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (testDialogOpen = false)}>Close</Button>
    <Button variant="primary" icon={Play} loading={testRunning} onclick={runTestAction}>Run test</Button>
  {/snippet}
</Dialog>

<!-- Attach / edit spec -->
<Dialog bind:open={specDialogOpen} title={specLockApi ? `Edit spec: ${specApi}` : 'Load spec'} size="xl">
  <div class="space-y-3 p-1">
    {#if specError}<Alert tone="error">{specError}</Alert>{/if}
    <p class="text-xs text-surface-500">
      Attach an OpenAPI 3.x YAML to this integration. Its operations become Actions —
      upsert by name, so existing actions refresh and new ones are added.
    </p>
    <div class="grid grid-cols-1 md:grid-cols-3 gap-3 items-end">
      <div class="md:col-span-2">
        <Input label="API identifier" help="specs.api" bind:value={specApi} disabled={specSaving || specLockApi} placeholder="netbox" />
      </div>
      <!-- `for` is load-bearing: without it the label binds to its first
           LABELABLE descendant, and <button> is labelable — so FieldHint's
           info button (which precedes the input) became the labeled control
           and clicking "Upload" only toggled the tooltip. -->
      <label for="integration-spec-file" class="inline-flex items-center justify-center gap-2 rounded-md border border-dashed border-surface-300-700 px-3 py-2 text-xs cursor-pointer hover:bg-surface-200-800/40">
        <Upload size={12} /> Upload .yaml <FieldHint id="integrations.upload_spec" />
        <input id="integration-spec-file" type="file" accept=".yaml,.yml" onchange={onSpecFile} disabled={specSaving} class="sr-only" />
      </label>
    </div>
    <Textarea
      label="Content (OpenAPI 3.x YAML)" help="specs.content"
      bind:value={specContent}
      disabled={specSaving}
      rows={18}
      placeholder={'openapi: 3.0.0\ninfo:\n  title: netbox\n  version: "1.0"\nservers:\n  - url: https://netbox.example.com/api\npaths:\n  /dcim/devices/:\n    get:\n      operationId: netbox:list_devices\n      summary: List devices\n      tags: [dcim]'}
    />
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (specDialogOpen = false)} disabled={specSaving}>Cancel</Button>
    <Button variant="primary" onclick={saveSpec} loading={specSaving}>{specLockApi ? 'Save & re-materialize' : 'Attach spec'}</Button>
  {/snippet}
</Dialog>

<!-- Attach / edit skill -->
<Dialog bind:open={skillDialogOpen} title={skillName ? `Skill: ${skillName}` : 'Load skill'} size="xl">
  <div class="space-y-3 p-1">
    {#if skillError}<Alert tone="error">{skillError}</Alert>{/if}
    <p class="text-xs text-surface-500">
      Attach a markdown prompt-skill scoped to this integration. It joins the agent's
      system prompt so it knows how to drive this integration.
    </p>
    <div class="grid grid-cols-1 md:grid-cols-3 gap-3 items-end">
      <div class="md:col-span-2">
        <Input label="Name (must end in .md)" help="skills.name" bind:value={skillName} disabled={skillSaving} placeholder="netbox.md" />
      </div>
      <!-- See the note on the spec upload above: `for` is what keeps the
           file input, not FieldHint's button, as the labeled control. -->
      <label for="integration-skill-file" class="inline-flex items-center justify-center gap-2 rounded-md border border-dashed border-surface-300-700 px-3 py-2 text-xs cursor-pointer hover:bg-surface-200-800/40">
        <Upload size={12} /> Upload .md <FieldHint id="integrations.upload_skill" />
        <input id="integration-skill-file" type="file" accept=".md,text/markdown" onchange={onSkillFile} disabled={skillSaving} class="sr-only" />
      </label>
    </div>
    <Textarea
      label="Content (markdown)" help="skills.content"
      bind:value={skillContent}
      disabled={skillSaving}
      rows={16}
      placeholder={'## NetBox\n\nUse the netbox_* actions to read devices, sites and IPs. Base URL and token come from this integration.'}
    />
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (skillDialogOpen = false)} disabled={skillSaving}>Cancel</Button>
    <Button variant="primary" onclick={saveSkill} loading={skillSaving}>Attach skill</Button>
  {/snippet}
</Dialog>
