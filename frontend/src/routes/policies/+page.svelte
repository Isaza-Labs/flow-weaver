<script lang="ts">
  import { onMount } from 'svelte';
  import { policies, errorMessage, type Policy } from '$lib/api/client';
  import {
    PageHeader, Card, Button, IconButton, Badge, EmptyState, Alert, Spinner,
    Input, Textarea, Tabs, confirm, toast, FieldHint,
  } from '$lib/components/ui';
  import { ShieldCheck, Plus, Trash2, Power, Save, Activity, RefreshCw, ChevronDown } from 'lucide-svelte';
  import RuleBuilder from './RuleBuilder.svelte';

  // State
  let list = $state<Policy[]>([]);
  let loading = $state(true);
  let error = $state('');

  let showNewForm = $state(false);
  let newName = $state('');
  let newDescription = $state('');
  let newRuleJson = $state(defaultRuleTemplate());
  let newEnabled = $state(false);
  let newError = $state('');
  let creating = $state(false);
  // Mode toggle per form. Visual = chip-based form backed by the
  // same JSON string so users can switch freely without losing state.
  let newMode = $state<'visual' | 'json'>('visual');
  let newBuilderError = $state<string | null>(null);

  let expanded = $state<string | null>(null);
  let editBuffer = $state<Record<string, { name: string; description: string; ruleJson: string; enabled: boolean; mode: 'visual' | 'json' }>>({});
  let editBuilderError = $state<Record<string, string | null>>({});
  let saveLoading = $state<Record<string, boolean>>({});

  function defaultRuleTemplate(): string {
    return JSON.stringify(
      {
        action: 'deny',
        reason: '',
        when: {
          env: ['production'],
          snippet_type: ['ssh'],
        },
      },
      null,
      2,
    );
  }

  onMount(load);

  async function load() {
    loading = true;
    error = '';
    try {
      const res = await policies.list();
      list = res.data;
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load policies');
    } finally {
      loading = false;
    }
  }

  async function create() {
    newError = '';
    creating = true;
    try {
      let rule: Record<string, unknown>;
      try {
        rule = JSON.parse(newRuleJson);
        if (!rule || typeof rule !== 'object' || Array.isArray(rule)) {
          throw new Error('rule must be a JSON object');
        }
      } catch (e) {
        newError = `Rule JSON invalid: ${(e as Error).message}`;
        return;
      }

      await policies.create({
        name: newName.trim(),
        description: newDescription.trim() || null,
        rule,
        enabled: newEnabled,
      });
      toast.success(`Policy "${newName}" created`);
      newName = '';
      newDescription = '';
      newRuleJson = defaultRuleTemplate();
      newEnabled = false;
      showNewForm = false;
      await load();
    } catch (e) {
      newError = errorMessage(e);
    } finally {
      creating = false;
    }
  }

  function initEdit(p: Policy) {
    editBuffer = {
      ...editBuffer,
      [p.policy_id]: {
        name: p.name,
        description: p.description ?? '',
        ruleJson: JSON.stringify(p.rule ?? {}, null, 2),
        enabled: p.enabled,
        mode: 'visual',
      },
    };
  }

  function toggleExpand(p: Policy) {
    if (expanded === p.policy_id) {
      expanded = null;
      return;
    }
    expanded = p.policy_id;
    if (!editBuffer[p.policy_id]) initEdit(p);
  }

  async function save(id: string) {
    const buf = editBuffer[id];
    if (!buf) return;
    saveLoading = { ...saveLoading, [id]: true };
    try {
      let rule: Record<string, unknown>;
      try {
        rule = JSON.parse(buf.ruleJson);
      } catch (e) {
        toast.error('Rule JSON invalid', { description: (e as Error).message });
        return;
      }
      await policies.update(id, {
        name: buf.name.trim(),
        // Send "" (not null) when cleared: the backend update skips null
        // fields so the enable/disable toggle can PATCH a single field, so
        // null here would leave the old description in place. "" clears it.
        description: buf.description.trim(),
        rule,
        enabled: buf.enabled,
      });
      toast.success('Policy saved');
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t save policy');
    } finally {
      saveLoading = { ...saveLoading, [id]: false };
    }
  }

  async function toggleEnabled(p: Policy) {
    // Disabling an active guardrail lets blocked operations through, so confirm first.
    if (
      p.enabled &&
      !(await confirm({
        title: 'Disable policy?',
        message: 'Disabling lets blocked operations through immediately.',
        tone: 'danger',
        confirmLabel: 'Disable',
      }))
    )
      return;
    const next = !p.enabled;
    saveLoading = { ...saveLoading, [p.policy_id]: true };
    try {
      await policies.update(p.policy_id, { enabled: next });
      toast.success(next ? `Policy "${p.name}" enabled` : `Policy "${p.name}" disabled`);
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t toggle policy');
    } finally {
      saveLoading = { ...saveLoading, [p.policy_id]: false };
    }
  }

  async function remove(p: Policy) {
    if (
      !(await confirm({
        title: 'Delete policy?',
        message: `Remove "${p.name}". Existing workflows are unaffected; new creates/runs will stop being blocked by this rule.`,
        tone: 'danger',
        confirmLabel: 'Delete',
      }))
    )
      return;
    try {
      await policies.delete(p.policy_id);
      if (expanded === p.policy_id) expanded = null;
      await load();
    } catch (e) {
      toast.fromError(e, 'Couldn’t delete policy');
    }
  }
</script>

<svelte:head><title>Policies · Flow Weaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Policies"
    description="Corporate guardrails evaluated on every workflow create, update, and promotion. Deny rules block matching operations; gates require historical conditions before a promotion proceeds."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      <Button variant="ghost" icon={Activity} href="/policies/audit">Audit</Button>
      <Button variant="primary" icon={Plus} onclick={() => (showNewForm = !showNewForm)}>New policy</Button>
    {/snippet}
  </PageHeader>

  {#if error}
    <Alert tone="error">{error}</Alert>
  {/if}

  {#if showNewForm}
    <Card padding="none">
      <header class="flex items-center justify-between gap-3 px-4 h-12 border-b border-surface-200-800">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500">New policy</h3>
        <Button size="xs" variant="ghost" onclick={() => (showNewForm = false)}>Cancel</Button>
      </header>
      <div class="px-4 py-4 space-y-3">
        {#if newError}<Alert tone="error">{newError}</Alert>{/if}
        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
          <Input label="Name" help="policies.name" bind:value={newName} placeholder="no-ssh-in-production" />
          <label class="flex items-center gap-2 cursor-pointer self-end pb-2">
            <input type="checkbox" bind:checked={newEnabled} class="rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-primary-500" />
            <span class="text-sm text-surface-700-300 inline-flex items-center gap-1">Enabled immediately <FieldHint id="policies.enabled" /></span>
          </label>
          <div class="md:col-span-2">
            <Input label="Description (optional)" help="policies.description" bind:value={newDescription} />
          </div>
          <div class="md:col-span-2">
            <Tabs
              bind:value={newMode}
              tabs={[
                { value: 'visual', label: 'Visual' },
                { value: 'json', label: 'JSON' },
              ]}
            />
            <div class="mt-3">
              {#if newMode === 'visual'}
                <RuleBuilder bind:ruleJson={newRuleJson} onError={(msg) => (newBuilderError = msg)} />
                {#if newBuilderError}<Alert tone="warning">{newBuilderError}</Alert>{/if}
              {:else}
                <Textarea label="Rule (JSON)" help="policies.rule" bind:value={newRuleJson} rows={10} mono />
                <p class="mt-1 text-xs text-surface-500">
                  Shape: <code>{'{ action: "deny" | "gate", … }'}</code>.
                  Switch to the Visual tab for a guided builder of either rule type.
                </p>
              {/if}
            </div>
          </div>
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
        icon={ShieldCheck}
        title="No policies yet"
        description="Policies block workflow operations that match a rule — typical use is to require a change window before SSHing into production routers, or a successful qa run before promoting."
      >
        {#snippet actions()}
          <Button variant="primary" icon={Plus} onclick={() => (showNewForm = true)}>Create first policy</Button>
        {/snippet}
      </EmptyState>
    </Card>
  {:else}
    <div class="space-y-2">
      {#each list as p (p.policy_id)}
        {@const isExpanded = expanded === p.policy_id}
        {@const buf = editBuffer[p.policy_id]}
        {@const ruleAction = (p.rule as { action?: string } | undefined)?.action}
        <Card padding="none">
          <div class="flex items-stretch">
            <button
              type="button"
              class="flex-1 flex items-center justify-between gap-3 px-4 h-12 text-left hover:bg-surface-200-800/30 transition-colors {isExpanded ? 'rounded-t-lg' : 'rounded-l-lg rounded-tr-none rounded-br-none lg:rounded-l-lg'}"
              onclick={() => toggleExpand(p)}
            >
              <div class="flex items-center gap-2 min-w-0">
                <ChevronDown size={14} class="text-surface-500 transition-transform shrink-0 {isExpanded ? 'rotate-0' : '-rotate-90'}" />
                <span class="font-medium text-surface-900-100 truncate">{p.name}</span>
                {#if ruleAction === 'gate'}
                  <Badge tone="primary">gate</Badge>
                {:else}
                  <Badge tone="neutral">deny</Badge>
                {/if}
                <Badge tone={p.enabled ? 'success' : 'neutral'}>{p.enabled ? 'enabled' : 'disabled'}</Badge>
                {#if p.description}
                  <span class="text-xs text-surface-500 truncate hidden sm:inline">{p.description}</span>
                {/if}
              </div>
            </button>
            <div class="flex items-center gap-1 px-2 border-l border-surface-200-800/60">
              <IconButton
                icon={Power}
                label={p.enabled ? 'Disable' : 'Enable'}
                variant={p.enabled ? 'secondary' : 'ghost'}
                loading={!!saveLoading[p.policy_id]}
                onclick={() => toggleEnabled(p)}
              />
              <IconButton icon={Trash2} label="Delete" variant="danger" onclick={() => remove(p)} />
            </div>
          </div>

          {#if isExpanded && buf}
            <div class="border-t border-surface-200-800 px-4 py-4 space-y-3">
              <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
                <Input label="Name" help="policies.name" bind:value={buf.name} />
                <label class="flex items-center gap-2 cursor-pointer self-end pb-2">
                  <input type="checkbox" bind:checked={buf.enabled} class="rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-primary-500" />
                  <span class="text-sm text-surface-700-300 inline-flex items-center gap-1">Enabled <FieldHint id="policies.enabled" /></span>
                </label>
                <div class="md:col-span-2">
                  <Input label="Description" help="policies.description" bind:value={buf.description} />
                </div>
                <div class="md:col-span-2">
                  <Tabs
                    bind:value={buf.mode}
                    tabs={[
                      { value: 'visual', label: 'Visual' },
                      { value: 'json', label: 'JSON' },
                    ]}
                  />
                  <div class="mt-3">
                    {#if buf.mode === 'visual'}
                      <RuleBuilder
                        bind:ruleJson={buf.ruleJson}
                        onError={(msg) => (editBuilderError = { ...editBuilderError, [p.policy_id]: msg })}
                      />
                      {#if editBuilderError[p.policy_id]}<Alert tone="warning">{editBuilderError[p.policy_id]}</Alert>{/if}
                    {:else}
                      <Textarea label="Rule (JSON)" help="policies.rule" bind:value={buf.ruleJson} rows={10} mono />
                    {/if}
                  </div>
                </div>
              </div>
              <div class="flex justify-end">
                <Button variant="primary" icon={Save} loading={saveLoading[p.policy_id]} onclick={() => save(p.policy_id)}>
                  Save changes
                </Button>
              </div>
            </div>
          {/if}
        </Card>
      {/each}
    </div>
    <p class="text-xs text-surface-500">{list.length} polic{list.length === 1 ? 'y' : 'ies'}</p>
  {/if}
</div>
