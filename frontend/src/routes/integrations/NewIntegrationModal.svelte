<script lang="ts">
  import { integrations, errorMessage } from '$lib/api/client';
  import {
    Dialog, Tabs, Input, Select, Textarea, Button, Alert, IconButton, toast, FieldHint,
  } from '$lib/components/ui';
  import { Trash2, Upload } from 'lucide-svelte';

  type AuthMethod = 'none' | 'token' | 'bearer' | 'basic' | 'api_key' | 'oauth2_client_credentials';
  type ModalTab = 'integration' | 'skills' | 'specs';

  // StagedSkill/Spec: one row per file the user has queued up. They're
  // persisted only when the bundle is submitted — until then everything
  // lives in client-side state.
  type StagedSkill = { name: string; content: string; sort_order: number };
  type StagedSpec = { api: string; content: string };

  let {
    open = $bindable(false),
    onCreated,
  }: {
    open?: boolean;
    onCreated?: () => void;
  } = $props();

  const integrationTypes = ['netbox', 'servicenow', 'infoblox', 'paloalto', 'generic_rest'] as const;
  const authMethods: AuthMethod[] = ['none', 'token', 'bearer', 'basic', 'api_key', 'oauth2_client_credentials'];

  // Matches the regex the old /skills FileUploadManager enforced — kept the
  // same so migrated prompt skills don't collide with new ones.
  const SKILL_NAME_RE = /^[a-zA-Z0-9_\-]+\.md$/;
  const SPEC_API_RE = /^[a-zA-Z0-9_\-]+$/;

  // ── Form state ──────────────────────────────────────────────────────
  let activeTab = $state<ModalTab>('integration');

  let form = $state({
    name: '',
    type: 'generic_rest' as string,
    base_url: '',
    description: '',
    auth_method: 'token' as AuthMethod,
    auth_token: '',
    auth_username: '',
    auth_password: '',
    auth_api_key: '',
    // oauth2_client_credentials: token endpoint + client credentials. The
    // access token is obtained and cached server-side at call time.
    auth_token_url: '',
    auth_client_id: '',
    auth_client_secret: '',
    auth_scope: '',
    tls_skip_verify: false,
    allow_private_network: false,
  });

  let stagedSkills = $state<StagedSkill[]>([]);
  let stagedSpecs = $state<StagedSpec[]>([]);

  let tabErrors = $state<Record<ModalTab, string>>({
    integration: '',
    skills: '',
    specs: '',
  });
  let submitting = $state(false);
  let submitError = $state('');

  // Refs to the native inputs so we can hide them and drive their
  // .click() from our own button. Browsers render "Examinar" /
  // "Ningún archivo seleccionado" from the OS locale on the native
  // control — the only robust fix is to keep the input invisible
  // and build the trigger ourselves.
  let skillFileInput = $state<HTMLInputElement | null>(null);
  let specFileInput = $state<HTMLInputElement | null>(null);

  // ── File staging ────────────────────────────────────────────────────

  async function onSkillFiles(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = input.files;
    if (!files || files.length === 0) return;
    for (const file of Array.from(files)) {
      const content = await file.text();
      stagedSkills = [
        ...stagedSkills,
        {
          // The uploaded filename becomes the skill name verbatim — user can
          // edit before submit if the regex doesn't match.
          name: file.name,
          content,
          sort_order: 100,
        },
      ];
    }
    // Reset so picking the same file again re-fires change.
    input.value = '';
  }

  async function onSpecFiles(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = input.files;
    if (!files || files.length === 0) return;
    for (const file of Array.from(files)) {
      const content = await file.text();
      const stem = file.name.replace(/\.(ya?ml)$/i, '').toLowerCase();
      stagedSpecs = [...stagedSpecs, { api: stem, content }];
    }
    input.value = '';
  }

  function removeSkill(index: number) {
    stagedSkills = stagedSkills.filter((_, i) => i !== index);
  }

  function removeSpec(index: number) {
    stagedSpecs = stagedSpecs.filter((_, i) => i !== index);
  }

  // ── Validation ──────────────────────────────────────────────────────

  function validate(): ModalTab | null {
    tabErrors = { integration: '', skills: '', specs: '' };

    if (!form.name.trim()) {
      tabErrors.integration = 'Name is required.';
      return 'integration';
    }
    if (!form.type.trim()) {
      tabErrors.integration = 'Type is required.';
      return 'integration';
    }
    if (!form.base_url.trim()) {
      tabErrors.integration = 'Base URL is required.';
      return 'integration';
    }
    if ((form.auth_method === 'token' || form.auth_method === 'bearer') && !form.auth_token) {
      tabErrors.integration = `Token is required for the ${form.auth_method} auth method.`;
      return 'integration';
    }
    if (form.auth_method === 'basic' && (!form.auth_username || !form.auth_password)) {
      tabErrors.integration = 'Username and password are required for basic auth.';
      return 'integration';
    }
    if (form.auth_method === 'api_key' && !form.auth_api_key) {
      tabErrors.integration = 'API key is required for the api_key auth method.';
      return 'integration';
    }
    if (form.auth_method === 'oauth2_client_credentials' && (!form.auth_token_url.trim() || !form.auth_client_id.trim())) {
      tabErrors.integration = 'Token URL and Client ID are required for OAuth2 client credentials.';
      return 'integration';
    }

    for (const s of stagedSkills) {
      if (!SKILL_NAME_RE.test(s.name)) {
        tabErrors.skills = `"${s.name}" is not a valid skill name (letters, digits, _, - and ending in .md).`;
        return 'skills';
      }
      if (!s.content.trim()) {
        tabErrors.skills = `"${s.name}" has empty content.`;
        return 'skills';
      }
    }
    const skillNames = stagedSkills.map((s) => s.name);
    const dupSkill = skillNames.find((n, i) => skillNames.indexOf(n) !== i);
    if (dupSkill) {
      tabErrors.skills = `Duplicate skill name: ${dupSkill}`;
      return 'skills';
    }

    for (const s of stagedSpecs) {
      if (!SPEC_API_RE.test(s.api)) {
        tabErrors.specs = `"${s.api}" is not a valid spec id (letters, digits, _, -, no extension).`;
        return 'specs';
      }
      if (!s.content.trim()) {
        tabErrors.specs = `"${s.api}" has empty content.`;
        return 'specs';
      }
      if (!/(^|\n)paths\s*:/.test(s.content)) {
        tabErrors.specs = `"${s.api}" does not look like an OpenAPI spec (no top-level "paths:" key).`;
        return 'specs';
      }
    }
    const specApis = stagedSpecs.map((s) => s.api);
    const dupSpec = specApis.find((n, i) => specApis.indexOf(n) !== i);
    if (dupSpec) {
      tabErrors.specs = `Duplicate spec id: ${dupSpec}`;
      return 'specs';
    }

    return null;
  }

  function authConfigFor(): Record<string, unknown> {
    // IntegrationAuthBuilder treats an empty/missing method as "no auth" and
    // silently skips header injection — matches services like the internal
    // mail microservice that are reachable without credentials.
    if (form.auth_method === 'none') return {};
    if (form.auth_method === 'token') return { method: 'token', token: form.auth_token };
    // bearer → Authorization: Bearer <token>. Required by Slack, GitHub, and
    // most modern APIs; the plain `token` method sends the "Token" prefix.
    if (form.auth_method === 'bearer') return { method: 'bearer', token: form.auth_token };
    if (form.auth_method === 'basic')
      return { method: 'basic', username: form.auth_username, password: form.auth_password };
    // oauth2_client_credentials → the backend obtains the Bearer token from
    // token_url at call time (IntegrationOAuthTokenService); only the client
    // credentials are stored.
    if (form.auth_method === 'oauth2_client_credentials')
      return {
        method: 'oauth2_client_credentials',
        token_url: form.auth_token_url.trim(),
        client_id: form.auth_client_id.trim(),
        client_secret: form.auth_client_secret,
        scope: form.auth_scope.trim(),
      };
    return { method: 'api_key', token: form.auth_api_key };
  }

  async function submit() {
    if (submitting) return;
    submitError = '';
    const firstFailedTab = validate();
    if (firstFailedTab) {
      activeTab = firstFailedTab;
      return;
    }

    submitting = true;
    try {
      await integrations.createBundle({
        integration: {
          name: form.name.trim(),
          type: form.type.trim(),
          description: form.description.trim() || null,
          base_url: form.base_url.trim(),
          auth_config: authConfigFor(),
          tls_skip_verify: form.tls_skip_verify,
          allow_private_network: form.allow_private_network,
        },
        skills: stagedSkills.map((s) => ({
          name: s.name,
          content: s.content,
          sort_order: s.sort_order,
        })),
        specs: stagedSpecs.map((s) => ({ api: s.api, content: s.content })),
      });

      toast.success(`Integration "${form.name}" created`, {
        description:
          stagedSkills.length + stagedSpecs.length > 0
            ? `${stagedSkills.length} skill(s), ${stagedSpecs.length} spec(s) linked`
            : undefined,
      });
      reset();
      open = false;
      onCreated?.();
    } catch (e) {
      submitError = errorMessage(e);
      toast.fromError(e, 'Couldn’t create integration');
    } finally {
      submitting = false;
    }
  }

  function reset() {
    activeTab = 'integration';
    form = {
      name: '',
      type: 'generic_rest',
      base_url: '',
      description: '',
      auth_method: 'token',
      auth_token: '',
      auth_username: '',
      auth_password: '',
      auth_api_key: '',
      auth_token_url: '',
      auth_client_id: '',
      auth_client_secret: '',
      auth_scope: '',
      tls_skip_verify: false,
      allow_private_network: false,
    };
    stagedSkills = [];
    stagedSpecs = [];
    tabErrors = { integration: '', skills: '', specs: '' };
    submitError = '';
  }

  function cancel() {
    if (submitting) return;
    reset();
    open = false;
  }

  const tabDefs = $derived<{ value: ModalTab; label: string; count?: number }[]>([
    { value: 'integration', label: 'Integration' },
    { value: 'skills', label: 'Skills', count: stagedSkills.length },
    { value: 'specs', label: 'Specs', count: stagedSpecs.length },
  ]);
</script>

<Dialog bind:open title="New integration" size="xl">
  <div class="space-y-4">
    <Tabs bind:value={activeTab} tabs={tabDefs} />

    {#if submitError}
      <Alert tone="error">{submitError}</Alert>
    {/if}

    <!-- ─────────────── Integration tab ─────────────── -->
    {#if activeTab === 'integration'}
      {#if tabErrors.integration}
        <Alert tone="error">{tabErrors.integration}</Alert>
      {/if}
      <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
        <Input label="Name" help="integrations.name" bind:value={form.name} placeholder="My integration" />
        <Select label="Type" help="integrations.auth_method" bind:value={form.type}>
          {#each integrationTypes as t}<option value={t}>{t}</option>{/each}
        </Select>
        <div class="md:col-span-2">
          <Input label="Base URL" help="integrations.base_url" bind:value={form.base_url} placeholder="https://example.com/api" />
        </div>
        <div class="md:col-span-2">
          <Input label="Description (optional)" help="integrations.action_description" bind:value={form.description} />
        </div>
        <Select label="Auth method" help="integrations.auth_method" bind:value={form.auth_method}>
          {#each authMethods as m}<option value={m}>{m}</option>{/each}
        </Select>
        <div>
          {#if form.auth_method === 'none'}
            <div class="text-xs text-surface-500 pt-6">
              No credentials sent. Pick this for internal services that are
              open on a trusted network.
            </div>
          {:else if form.auth_method === 'token' || form.auth_method === 'bearer'}
            <Input
              label="Token" help="integrations.auth_token"
              type="password"
              revealable
              bind:value={form.auth_token}
              placeholder={form.auth_method === 'bearer'
                ? 'Sent as: Authorization: Bearer <token>'
                : 'Sent as: Authorization: Token <token>'}
            />
          {:else if form.auth_method === 'basic'}
            <div class="space-y-2">
              <Input label="Username" help="integrations.auth_username" bind:value={form.auth_username} />
              <Input label="Password" help="integrations.auth_password" type="password" revealable bind:value={form.auth_password} />
            </div>
          {:else if form.auth_method === 'oauth2_client_credentials'}
            <div class="space-y-2">
              <Input
                label="Token URL"
                help="integrations.auth_token_url"
                bind:value={form.auth_token_url}
                placeholder="https://idp.example.com/oauth/token"
              />
              <Input label="Client ID" help="integrations.auth_client_id" bind:value={form.auth_client_id} />
              <Input label="Client secret" help="integrations.auth_client_secret" type="password" revealable bind:value={form.auth_client_secret} />
              <Input label="Scope (optional)" help="integrations.auth_scope" bind:value={form.auth_scope} placeholder="read write" />
              <div class="text-[11px] text-surface-500">
                The access token is requested and cached server-side on each call — it is never stored in the integration.
              </div>
            </div>
          {:else}
            <Input label="API key" help="integrations.auth_api_key" type="password" revealable bind:value={form.auth_api_key} />
          {/if}
        </div>
        <label class="md:col-span-2 flex items-center gap-2 cursor-pointer">
          <input
            type="checkbox"
            bind:checked={form.tls_skip_verify}
            class="rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-primary-500"
          />
          <span class="text-sm text-surface-700-300 inline-flex items-center gap-1">Skip TLS verification (lab only) <FieldHint id="integrations.tls_skip_verify" /></span>
        </label>
        <label class="md:col-span-2 flex items-start gap-2 cursor-pointer">
          <input
            type="checkbox"
            bind:checked={form.allow_private_network}
            class="mt-0.5 rounded border-surface-300-700 bg-surface-50-950 text-warning-500 focus:ring-warning-500"
          />
          <span class="text-sm text-surface-700-300">
            Allow private-network targets (10/8, 172.16/12, 192.168/16)
            <FieldHint id="integrations.allow_private_network" />
            <span class="block text-[11px] text-surface-500 mt-0.5">
              Required when the integration's base URL points at an internal
              host (self-hosted NetBox, internal AWX, etc.). Loopback and
              cloud-metadata IPs stay blocked regardless.
            </span>
          </span>
        </label>
      </div>
    {/if}

    <!-- ─────────────── Skills tab ─────────────── -->
    {#if activeTab === 'skills'}
      {#if tabErrors.skills}
        <Alert tone="error">{tabErrors.skills}</Alert>
      {/if}
      <p class="text-xs text-surface-500">
        Prompt skills scoped to this integration. The agent concatenates them
        into the system prompt whenever the integration appears in scope.
      </p>

      <div class="flex flex-col gap-2">
        <span class="text-xs font-medium text-surface-600-400 inline-flex items-center gap-1">Markdown skills <FieldHint id="integrations.skill_files" /></span>
        <input
          bind:this={skillFileInput}
          type="file"
          accept=".md,text/markdown"
          multiple
          onchange={onSkillFiles}
          disabled={submitting}
          class="sr-only"
          tabindex="-1"
          aria-hidden="true"
        />
        <Button
          variant="secondary"
          icon={Upload}
          disabled={submitting}
          onclick={() => skillFileInput?.click()}
        >
          Choose .md files
        </Button>
      </div>

      {#if stagedSkills.length === 0}
        <div class="rounded-md border border-dashed border-surface-300-700 p-6 text-center text-xs text-surface-500">
          No skills staged yet. Pick one or more `.md` files to attach.
        </div>
      {:else}
        <div class="space-y-2">
          {#each stagedSkills as skill, i (i)}
            <div class="border border-surface-200-800 rounded-md p-3 space-y-2">
              <div class="flex items-start gap-2">
                <div class="flex-1 grid grid-cols-1 md:grid-cols-3 gap-2">
                  <div class="md:col-span-2">
                    <Input label="Name" help="skills.name" bind:value={skill.name} placeholder="base.md" />
                  </div>
                  <Input label="Sort order" help="skills.sort_order" type="number" min={0} bind:value={skill.sort_order} />
                </div>
                <IconButton
                  icon={Trash2}
                  label="Remove skill"
                  variant="danger"
                  onclick={() => removeSkill(i)}
                />
              </div>
              <Textarea
                label="Content preview" help="upload.preview"
                bind:value={skill.content}
                rows={4}
                mono
              />
            </div>
          {/each}
        </div>
      {/if}
    {/if}

    <!-- ─────────────── Specs tab ─────────────── -->
    {#if activeTab === 'specs'}
      {#if tabErrors.specs}
        <Alert tone="error">{tabErrors.specs}</Alert>
      {/if}
      <p class="text-xs text-surface-500">
        OpenAPI 3.x specs scoped to this integration. Operations discovered
        here inherit the integration's base URL and credentials.
      </p>

      <div class="flex flex-col gap-2">
        <span class="text-xs font-medium text-surface-600-400 inline-flex items-center gap-1">OpenAPI specs <FieldHint id="integrations.spec_files" /></span>
        <input
          bind:this={specFileInput}
          type="file"
          accept=".yaml,.yml,text/yaml,application/yaml,application/x-yaml"
          multiple
          onchange={onSpecFiles}
          disabled={submitting}
          class="sr-only"
          tabindex="-1"
          aria-hidden="true"
        />
        <Button
          variant="secondary"
          icon={Upload}
          disabled={submitting}
          onclick={() => specFileInput?.click()}
        >
          Choose .yaml files
        </Button>
      </div>

      {#if stagedSpecs.length === 0}
        <div class="rounded-md border border-dashed border-surface-300-700 p-6 text-center text-xs text-surface-500">
          No specs staged yet. Pick one or more `.yaml` files to attach.
        </div>
      {:else}
        <div class="space-y-2">
          {#each stagedSpecs as spec, i (i)}
            <div class="border border-surface-200-800 rounded-md p-3 space-y-2">
              <div class="flex items-start gap-2">
                <div class="flex-1">
                  <Input label="API identifier (no extension)" help="specs.api" bind:value={spec.api} placeholder="netbox" />
                </div>
                <IconButton
                  icon={Trash2}
                  label="Remove spec"
                  variant="danger"
                  onclick={() => removeSpec(i)}
                />
              </div>
              <Textarea
                label="YAML preview" help="upload.preview"
                bind:value={spec.content}
                rows={6}
                mono
              />
            </div>
          {/each}
        </div>
      {/if}
    {/if}
  </div>

  {#snippet footer()}
    <Button variant="ghost" onclick={cancel} disabled={submitting}>Cancel</Button>
    <Button
      variant="primary"
      icon={Upload}
      loading={submitting}
      onclick={submit}
    >
      Create integration
    </Button>
  {/snippet}
</Dialog>
