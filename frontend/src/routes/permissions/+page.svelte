<script lang="ts">
  import { onMount } from 'svelte';
  import {
    permissionGrants, users, errorMessage,
    type PermissionGrant, type CapabilityInfo, type User,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, IconButton, Badge, EmptyState, Alert, Spinner,
    Input, Textarea, Tabs, Select, confirm, toast, FieldHint,
  } from '$lib/components/ui';
  import { KeyRound, Plus, Trash2, Power, Save, RefreshCw, ChevronDown, UserPlus, X, Lock } from 'lucide-svelte';
  import CapabilityPicker from './CapabilityPicker.svelte';
  import ConditionBuilder from './ConditionBuilder.svelte';

  type EditBuf = {
    name: string;
    description: string;
    enabled: boolean;
    capabilities: string[];
    conditionsJson: string;
    mode: 'visual' | 'json';
  };

  // Data
  let list = $state<PermissionGrant[]>([]);
  let caps = $state<CapabilityInfo[]>([]);
  let userList = $state<User[]>([]);
  let loading = $state(true);
  let error = $state('');

  // Create form
  let showNewForm = $state(false);
  let newName = $state('');
  let newDescription = $state('');
  let newEnabled = $state(true);
  let newCapabilities = $state<string[]>([]);
  let newConditionsJson = $state('{}');
  let newMode = $state<'visual' | 'json'>('visual');
  let newBuilderError = $state<string | null>(null);
  let newError = $state('');
  let creating = $state(false);

  // Edit (expand-to-edit, keyed by grant id like the policies page)
  let expanded = $state<string | null>(null);
  let editBuffer = $state<Record<string, EditBuf>>({});
  let editBuilderError = $state<Record<string, string | null>>({});
  let saveLoading = $state<Record<string, boolean>>({});

  // Subject assignment (per grant)
  let subjectDraft = $state<Record<string, string>>({});
  let subjectLoading = $state<Record<string, boolean>>({});

  // Resolve subject uuids → usernames for display.
  const userById = $derived(new Map(userList.map((u) => [u.user_id, u])));
  function usernameFor(id: string): string {
    return userById.get(id)?.username ?? id;
  }

  // Union of the condition dimensions the selected capabilities actually
  // consult — surfaced as a hint so admins know which condition fields
  // matter for their selection.
  function conditionableFor(capKeys: string[]): string[] {
    const set = new Set<string>();
    for (const c of caps) {
      if (capKeys.includes(c.key)) for (const d of c.conditionable) set.add(d);
    }
    return [...set].sort();
  }

  onMount(async () => {
    await Promise.all([load(), loadCapabilities(), loadUsers()]);
  });

  async function load() {
    loading = true;
    error = '';
    try {
      const res = await permissionGrants.list();
      list = res.data;
      // Seed the per-grant "Add user" draft for every grant up-front so the
      // Select never binds to an undefined record key — Svelte 5 rejects
      // bind:value={record[unseededKey]} with props_invalid_value.
      const drafts = { ...subjectDraft };
      for (const g of list) if (drafts[g.permission_grant_id] === undefined) drafts[g.permission_grant_id] = '';
      subjectDraft = drafts;
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load permission grants');
    } finally {
      loading = false;
    }
  }

  async function loadCapabilities() {
    try {
      caps = await permissionGrants.capabilities();
    } catch {
      // Non-fatal: the picker just renders empty and the JSON tab still works.
    }
  }

  async function loadUsers() {
    try {
      userList = await users.list();
    } catch {
      // Non-fatal: subjects render as raw ids and the picker stays empty.
    }
  }

  async function create() {
    newError = '';
    creating = true;
    try {
      let conditions: Record<string, unknown>;
      try {
        conditions = JSON.parse(newConditionsJson || '{}');
        if (!conditions || typeof conditions !== 'object' || Array.isArray(conditions)) {
          throw new Error('conditions must be a JSON object');
        }
      } catch (e) {
        newError = `Conditions JSON invalid: ${(e as Error).message}`;
        return;
      }
      if (newCapabilities.length === 0) {
        newError = 'Select at least one capability.';
        return;
      }

      await permissionGrants.create({
        name: newName.trim(),
        description: newDescription.trim() || null,
        enabled: newEnabled,
        capabilities: newCapabilities,
        // Subjects are assigned after creation via the dedicated endpoints.
        subject_ids: [],
        conditions,
      });
      toast.success(`Permission "${newName}" created`);
      newName = '';
      newDescription = '';
      newEnabled = true;
      newCapabilities = [];
      newConditionsJson = '{}';
      newMode = 'visual';
      newBuilderError = null;
      showNewForm = false;
      await load();
    } catch (e) {
      newError = errorMessage(e);
    } finally {
      creating = false;
    }
  }

  function initEdit(g: PermissionGrant) {
    editBuffer = {
      ...editBuffer,
      [g.permission_grant_id]: {
        name: g.name,
        description: g.description ?? '',
        enabled: g.enabled,
        capabilities: [...(g.capabilities ?? [])],
        conditionsJson: JSON.stringify(g.conditions ?? {}, null, 2),
        mode: 'visual',
      },
    };
  }

  function toggleExpand(g: PermissionGrant) {
    // Built-in grants are immutable — never open the editor for them.
    if (g.is_builtin) return;
    if (expanded === g.permission_grant_id) {
      expanded = null;
      return;
    }
    expanded = g.permission_grant_id;
    if (!editBuffer[g.permission_grant_id]) initEdit(g);
  }

  async function save(id: string) {
    const buf = editBuffer[id];
    if (!buf) return;
    saveLoading = { ...saveLoading, [id]: true };
    try {
      let conditions: Record<string, unknown>;
      try {
        conditions = JSON.parse(buf.conditionsJson || '{}');
      } catch (e) {
        toast.error('Conditions JSON invalid', { description: (e as Error).message });
        return;
      }
      await permissionGrants.update(id, {
        name: buf.name.trim(),
        // Send "" (not null) when cleared: the backend skips null fields on
        // a partial PUT so the enable/disable toggle can PATCH one field,
        // which would leave a stale description. "" clears it explicitly.
        description: buf.description.trim(),
        enabled: buf.enabled,
        capabilities: buf.capabilities,
        conditions,
      });
      toast.success('Permission saved');
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t save permission');
    } finally {
      saveLoading = { ...saveLoading, [id]: false };
    }
  }

  async function toggleEnabled(g: PermissionGrant) {
    if (
      g.enabled &&
      !(await confirm({
        title: 'Disable permission?',
        message: 'Subjects lose the capabilities this grant confers immediately.',
        tone: 'danger',
        confirmLabel: 'Disable',
      }))
    )
      return;
    const next = !g.enabled;
    saveLoading = { ...saveLoading, [g.permission_grant_id]: true };
    try {
      await permissionGrants.update(g.permission_grant_id, { enabled: next });
      toast.success(next ? `Permission "${g.name}" enabled` : `Permission "${g.name}" disabled`);
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t toggle permission');
    } finally {
      saveLoading = { ...saveLoading, [g.permission_grant_id]: false };
    }
  }

  async function remove(g: PermissionGrant) {
    if (
      !(await confirm({
        title: 'Delete permission?',
        message: `Remove "${g.name}". Every assigned subject loses the capabilities it granted.`,
        tone: 'danger',
        confirmLabel: 'Delete',
      }))
    )
      return;
    try {
      await permissionGrants.remove(g.permission_grant_id);
      if (expanded === g.permission_grant_id) expanded = null;
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t delete permission');
    }
  }

  async function addSubject(g: PermissionGrant) {
    const userId = subjectDraft[g.permission_grant_id];
    if (!userId) return;
    subjectLoading = { ...subjectLoading, [g.permission_grant_id]: true };
    try {
      await permissionGrants.addSubject(g.permission_grant_id, userId);
      subjectDraft = { ...subjectDraft, [g.permission_grant_id]: '' };
      await load();
      toast.success('Subject added');
    } catch (e) {
      toast.fromError(e, 'Couldn’t add subject');
    } finally {
      subjectLoading = { ...subjectLoading, [g.permission_grant_id]: false };
    }
  }

  async function removeSubject(g: PermissionGrant, userId: string) {
    try {
      await permissionGrants.removeSubject(g.permission_grant_id, userId);
      await load();
      toast.success('Subject removed');
    } catch (e) {
      toast.fromError(e, 'Couldn’t remove subject');
    }
  }
</script>

<svelte:head><title>Permissions · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Permissions"
    description="Granular capability grants. Each grant confers a set of capabilities on the assigned users, optionally scoped to an environment, device, or resource context. Built-in grants are seeded and read-only."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="primary" icon={Plus} onclick={() => (showNewForm = !showNewForm)}>New permission</Button>
    {/snippet}
  </PageHeader>

  {#if error}
    <Alert tone="error">{error}</Alert>
  {/if}

  {#if showNewForm}
    <Card padding="none">
      <header class="flex items-center justify-between gap-3 px-4 h-12 border-b border-surface-200-800">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500">New permission</h3>
        <Button size="xs" variant="ghost" onclick={() => (showNewForm = false)}>Cancel</Button>
      </header>
      <div class="px-4 py-4 space-y-4">
        {#if newError}<Alert tone="error">{newError}</Alert>{/if}
        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
          <Input label="Name" help="permissions.name" bind:value={newName} placeholder="prod-ssh-operators" />
          <label class="flex items-center gap-2 cursor-pointer self-end pb-2">
            <input type="checkbox" bind:checked={newEnabled} class="rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-primary-500" />
            <span class="text-sm text-surface-700-300 inline-flex items-center gap-1">Enabled immediately <FieldHint id="permissions.enabled" /></span>
          </label>
          <div class="md:col-span-2">
            <Input label="Description (optional)" help="permissions.description" bind:value={newDescription} />
          </div>
        </div>

        <div>
          <span class="block text-xs uppercase tracking-wide text-surface-500 mb-2">Capabilities</span>
          <CapabilityPicker capabilities={caps} bind:selected={newCapabilities} />
        </div>

        <div>
          <div class="flex items-center justify-between mb-2">
            <span class="block text-xs uppercase tracking-wide text-surface-500">Conditions</span>
            <Tabs
              bind:value={newMode}
              tabs={[
                { value: 'visual', label: 'Visual' },
                { value: 'json', label: 'JSON' },
              ]}
            />
          </div>
          <p class="text-[11px] text-surface-500 mb-2">
            {#if newCapabilities.length === 0}
              Pick capabilities above; conditions scope where they apply. Leave empty for any context.
            {:else if conditionableFor(newCapabilities).length === 0}
              The selected capabilities take no conditions — they apply globally.
            {:else}
              Selected capabilities are scoped by: <span class="font-medium">{conditionableFor(newCapabilities).join(', ')}</span>.
            {/if}
          </p>
          {#if newMode === 'visual'}
            <ConditionBuilder bind:conditionsJson={newConditionsJson} onError={(msg) => (newBuilderError = msg)} />
            {#if newBuilderError}<Alert tone="warning">{newBuilderError}</Alert>{/if}
          {:else}
            <Textarea label="Conditions (JSON)" help="permissions.conditions" bind:value={newConditionsJson} rows={10} mono />
            <p class="mt-1 text-xs text-surface-500">
              An empty object <code>{'{}'}</code> means the grant applies in any context.
            </p>
          {/if}
        </div>
      </div>
      <footer class="flex items-center justify-end gap-2 px-4 py-3 border-t border-surface-200-800 bg-surface-50-950/30">
        <Button variant="ghost" onclick={() => (showNewForm = false)}>Cancel</Button>
        <Button variant="primary" icon={Save} loading={creating} onclick={create}>Create</Button>
      </footer>
    </Card>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if list.length === 0}
    <Card padding="none">
      <EmptyState
        icon={KeyRound}
        title="No permission grants yet"
        description="A grant binds a set of capabilities to specific users, optionally scoped to an environment, device, or resource. Create one to give operators fine-grained access without changing their global role."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={() => (showNewForm = true)}>Create first permission</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <div class="space-y-2">
      {#each list as g (g.permission_grant_id)}
        {@const isExpanded = expanded === g.permission_grant_id}
        {@const buf = editBuffer[g.permission_grant_id]}
        <Card padding="none">
          {#if g.is_builtin}
            <!-- Built-in: read-only. No expand / edit / delete / assign. -->
            <div class="flex items-center gap-2 px-4 h-12">
              <Lock size={14} class="text-surface-500 shrink-0" />
              <span class="font-medium text-surface-900-100 truncate">{g.name}</span>
              <Badge tone="primary">built-in</Badge>
              <Badge tone={g.enabled ? 'success' : 'neutral'}>{g.enabled ? 'enabled' : 'disabled'}</Badge>
              {#if g.description}
                <span class="text-xs text-surface-500 truncate hidden sm:inline">{g.description}</span>
              {/if}
            </div>
            <div class="border-t border-surface-200-800 px-4 py-3 space-y-2">
              <div class="flex flex-wrap items-center gap-1">
                <span class="text-[11px] uppercase tracking-wide text-surface-500 mr-1">Capabilities</span>
                {#each g.capabilities as cap}
                  <Badge tone="neutral" size="xs" mono>{cap}</Badge>
                {/each}
                {#if g.capabilities.length === 0}<span class="text-xs text-surface-500">none</span>{/if}
              </div>
              <div class="text-xs text-surface-500">
                {#if g.subject_ids.length > 0}
                  Subjects: {g.subject_ids.map(usernameFor).join(', ')}
                {:else}
                  No subjects assigned
                {/if}
              </div>
            </div>
          {:else}
            <div class="flex items-stretch">
              <button
                type="button"
                class="flex-1 flex items-center justify-between gap-3 px-4 h-12 text-left hover:bg-surface-200-800/30 transition-colors {isExpanded ? 'rounded-t-lg' : 'rounded-l-lg'}"
                onclick={() => toggleExpand(g)}
              >
                <div class="flex items-center gap-2 min-w-0">
                  <ChevronDown size={14} class="text-surface-500 transition-transform shrink-0 {isExpanded ? 'rotate-0' : '-rotate-90'}" />
                  <span class="font-medium text-surface-900-100 truncate">{g.name}</span>
                  <Badge tone={g.enabled ? 'success' : 'neutral'}>{g.enabled ? 'enabled' : 'disabled'}</Badge>
                  <Badge tone="neutral">{g.capabilities.length} cap{g.capabilities.length === 1 ? '' : 's'}</Badge>
                  {#if g.subject_ids.length > 0}
                    <Badge tone="primary">{g.subject_ids.length} subject{g.subject_ids.length === 1 ? '' : 's'}</Badge>
                  {/if}
                  {#if g.description}
                    <span class="text-xs text-surface-500 truncate hidden sm:inline">{g.description}</span>
                  {/if}
                </div>
              </button>
              <div class="flex items-center gap-1 px-2 border-l border-surface-200-800/60">
                <IconButton
                  icon={Power}
                  label={g.enabled ? 'Disable' : 'Enable'}
                  variant={g.enabled ? 'success' : 'ghost'}
                  loading={!!saveLoading[g.permission_grant_id]}
                  onclick={() => toggleEnabled(g)}
                />
                <IconButton icon={Trash2} label="Delete" variant="danger" onclick={() => remove(g)} />
              </div>
            </div>

            {#if isExpanded && buf}
              <div class="border-t border-surface-200-800 px-4 py-4 space-y-4">
                <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
                  <Input label="Name" help="permissions.name" bind:value={buf.name} />
                  <label class="flex items-center gap-2 cursor-pointer self-end pb-2">
                    <input type="checkbox" bind:checked={buf.enabled} class="rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-primary-500" />
                    <span class="text-sm text-surface-700-300 inline-flex items-center gap-1">Enabled <FieldHint id="permissions.enabled" /></span>
                  </label>
                  <div class="md:col-span-2">
                    <Input label="Description" help="permissions.description" bind:value={buf.description} />
                  </div>
                </div>

                <div>
                  <span class="block text-xs uppercase tracking-wide text-surface-500 mb-2">Capabilities</span>
                  <CapabilityPicker capabilities={caps} bind:selected={buf.capabilities} />
                </div>

                <div>
                  <div class="flex items-center justify-between mb-2">
                    <span class="block text-xs uppercase tracking-wide text-surface-500">Conditions</span>
                    <Tabs
                      bind:value={buf.mode}
                      tabs={[
                        { value: 'visual', label: 'Visual' },
                        { value: 'json', label: 'JSON' },
                      ]}
                    />
                  </div>
                  <p class="text-[11px] text-surface-500 mb-2">
                    {#if buf.capabilities.length === 0}
                      Pick capabilities above; conditions scope where they apply. Leave empty for any context.
                    {:else if conditionableFor(buf.capabilities).length === 0}
                      The selected capabilities take no conditions — they apply globally.
                    {:else}
                      Selected capabilities are scoped by: <span class="font-medium">{conditionableFor(buf.capabilities).join(', ')}</span>.
                    {/if}
                  </p>
                  {#if buf.mode === 'visual'}
                    <ConditionBuilder
                      bind:conditionsJson={buf.conditionsJson}
                      onError={(msg) => (editBuilderError = { ...editBuilderError, [g.permission_grant_id]: msg })}
                    />
                    {#if editBuilderError[g.permission_grant_id]}<Alert tone="warning">{editBuilderError[g.permission_grant_id]}</Alert>{/if}
                  {:else}
                    <Textarea label="Conditions (JSON)" help="permissions.conditions" bind:value={buf.conditionsJson} rows={10} mono />
                  {/if}
                </div>

                <!-- Subject assignment. Add/remove commit immediately via the
                     dedicated subject endpoints (not part of Save changes). -->
                <div>
                  <span class="block text-xs uppercase tracking-wide text-surface-500 mb-2">Subjects</span>
                  <div class="flex flex-wrap items-center gap-1.5 mb-2">
                    {#each g.subject_ids as sid (sid)}
                      <span class="inline-flex items-center gap-1 bg-surface-100-900 border border-surface-300-700 rounded-full px-2 py-0.5 text-xs">
                        {usernameFor(sid)}
                        <button type="button" class="hover:text-error-400" onclick={() => removeSubject(g, sid)} aria-label="Remove subject">
                          <X size={10} />
                        </button>
                      </span>
                    {/each}
                    {#if g.subject_ids.length === 0}
                      <span class="text-xs text-surface-500">No subjects assigned yet.</span>
                    {/if}
                  </div>
                  <div class="flex items-end gap-2">
                    <Select label="Add user" help="permissions.add_user" bind:value={subjectDraft[g.permission_grant_id]}>
                      <option value="">—</option>
                      {#each userList.filter((u) => !g.subject_ids.includes(u.user_id)) as u (u.user_id)}
                        <option value={u.user_id}>{u.username}</option>
                      {/each}
                    </Select>
                    <Button
                      icon={UserPlus}
                      loading={!!subjectLoading[g.permission_grant_id]}
                      disabled={!subjectDraft[g.permission_grant_id]}
                      onclick={() => addSubject(g)}
                    >Add</Button>
                  </div>
                </div>

                <div class="flex justify-end">
                  <Button variant="primary" icon={Save} loading={saveLoading[g.permission_grant_id]} onclick={() => save(g.permission_grant_id)}>
                    Save changes
                  </Button>
                </div>
              </div>
            {/if}
          {/if}
        </Card>
      {/each}
    </div>
    <p class="text-xs text-surface-500">{list.length} permission{list.length === 1 ? '' : 's'}</p>
  {/if}
</div>
