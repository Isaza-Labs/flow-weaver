<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { workflows, workflowTriggers, errorMessage, type WorkflowTrigger, type Workflow } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, Badge, Tabs, EmptyState,
    Alert, Spinner, Input, Dialog, formatDateTime, confirm,
  } from '$lib/components/ui';
  import { Plus, Zap, Trash2, Power, Edit, Webhook, Hand, CalendarClock, Radio, Copy, KeyRound } from 'lucide-svelte';
  import { copyText } from '$lib/utils/clipboard';

  type TriggerType = 'manual' | 'webhook' | 'schedule' | 'event';

  // Legacy rows created before the webhook feature carry type 'api'; treat them
  // as webhooks so they don't fall out of the tab.
  const isWebhook = (t: WorkflowTrigger) => t.type === 'webhook' || t.type === 'api';

  let workflow = $state<Workflow | null>(null);
  let triggers = $state<WorkflowTrigger[]>([]);
  let loading = $state(true);
  let listError = $state('');
  let activeTab = $state<'all' | TriggerType>('all');

  let addingType = $state<TriggerType | null>(null);
  let showAddDialog = $state(false);
  let formName = $state('');
  let formDescription = $state('');
  let formError = $state('');
  let saving = $state(false);

  // Webhook detail / secret-reveal dialog.
  let showWebhookDialog = $state(false);
  let webhookTrigger = $state<WorkflowTrigger | null>(null);
  let revealedSecret = $state<string | null>(null);
  let rotating = $state(false);

  onMount(loadAll);

  async function loadAll() {
    loading = true;
    listError = '';
    try {
      const wfId = page.params.id;
      if (!wfId) { listError = 'No workflow id'; loading = false; return; }
      const [wf, trigs] = await Promise.all([workflows.get(wfId), workflows.triggers(wfId)]);
      workflow = wf;
      triggers = trigs ?? [];
    } catch (e) {
      listError = errorMessage(e);
          toast.fromError(e, 'Action failed');
    } finally {
      loading = false;
    }
  }

  const counts = $derived({
    all: triggers.length,
    manual: triggers.filter((t) => t.type === 'manual').length,
    webhook: triggers.filter(isWebhook).length,
    schedule: triggers.filter((t) => t.type === 'schedule').length,
    event: triggers.filter((t) => t.type === 'event').length,
  });

  const visibleTriggers = $derived(
    activeTab === 'all'
      ? triggers
      : activeTab === 'webhook'
        ? triggers.filter(isWebhook)
        : triggers.filter((t) => t.type === activeTab));
  const typeOrder: TriggerType[] = ['schedule', 'webhook', 'manual', 'event'];
  const sortedVisibleTriggers = $derived.by(() => {
    const rank = (t: WorkflowTrigger) => typeOrder.indexOf((isWebhook(t) ? 'webhook' : t.type) as TriggerType);
    return [...visibleTriggers].sort((a, b) => {
      const ai = rank(a), bi = rank(b);
      if (ai !== bi) return ai - bi;
      return a.created_at.localeCompare(b.created_at);
    });
  });

  function webhookUrl(t: WorkflowTrigger): string {
    return t.webhook_path ? `${page.url.origin}${t.webhook_path}` : '';
  }

  async function copy(text: string, label = 'Copied') {
    if (await copyText(text)) toast.success(label);
    else toast.error('Copy failed — select and copy manually');
  }

  function openAdd(type: TriggerType) {
    if (type === 'schedule') {
      goto(`/workflows/${page.params.id}/schedules/new`);
      return;
    }
    addingType = type;
    showAddDialog = true;
    formName = '';
    formDescription = '';
    formError = '';
  }

  function closeAdd() { addingType = null; showAddDialog = false; formError = ''; }

  async function createTrigger() {
    formError = '';
    if (!formName.trim()) { formError = 'Name is required'; return; }
    const wfId = page.params.id;
    if (!wfId || !addingType) return;
    saving = true;
    try {
      const created = await workflows.createTrigger(wfId, {
        name: formName.trim(),
        type: addingType,
        description: formDescription.trim() || null,
      });
      await loadAll();
      closeAdd();
      // A freshly-created webhook returns its signing secret exactly once —
      // surface it immediately so the user can copy it before it's gone.
      if (created && isWebhook(created)) {
        webhookTrigger = created;
        revealedSecret = created.webhook_secret ?? null;
        showWebhookDialog = true;
      }
    } catch (e) {
      formError = errorMessage(e);toast.fromError(e, 'Couldn’t create trigger');
    } finally {
      saving = false;
    }
  }

  function openWebhook(t: WorkflowTrigger) {
    webhookTrigger = t;
    revealedSecret = null;   // secret is only shown right after create/rotate
    showWebhookDialog = true;
  }

  async function rotateSecret() {
    if (!webhookTrigger) return;
    if (!(await confirm({
      title: 'Rotate signing secret?',
      message: 'The current secret stops working immediately. Update your webhook sender with the new one.',
      confirmLabel: 'Rotate',
      tone: 'danger',
    }))) return;
    rotating = true;
    try {
      const res = await workflowTriggers.rotateSecret(webhookTrigger.id);
      revealedSecret = res.webhook_secret ?? null;
      webhookTrigger = res;
      await loadAll();
    } catch (e) {
      toast.fromError(e, 'Couldn’t rotate secret');
    } finally {
      rotating = false;
    }
  }

  async function toggleTrigger(t: WorkflowTrigger) {
    try { await workflowTriggers.update(t.id, { enabled: !t.enabled }); await loadAll(); }
    catch (e) { listError = errorMessage(e);       toast.fromError(e, 'Couldn’t toggle trigger');
    }
  }

  async function deleteTrigger(t: WorkflowTrigger) {
    if (!(await confirm({ title: `Delete "${t.name}"?`, message: 'This cannot be undone.', tone: 'danger', confirmLabel: 'Delete' }))) return;
    try { await workflowTriggers.delete(t.id); await loadAll(); }
    catch (e) { listError = errorMessage(e);       toast.fromError(e, 'Couldn’t delete trigger');
    }
  }

  function describe(t: WorkflowTrigger): string {
    if (isWebhook(t)) return t.webhook_path ? `POST ${t.webhook_path}` : '(webhook)';
    switch (t.type) {
      case 'schedule': return t.cron_expression ?? '(no cron)';
      case 'manual': return 'Run from UI / API';
      case 'event': return t.description ?? '(no event type)';
      default: return '';
    }
  }

  const typeTone: Record<string, 'primary' | 'success' | 'warning' | 'neutral'> = {
    schedule: 'primary', webhook: 'success', api: 'success', manual: 'neutral', event: 'warning',
  };
</script>

<svelte:head><title>Triggers · {workflow?.name ?? 'Workflow'} · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-6xl mx-auto space-y-5">
  <PageHeader
    title="Triggers"
    description="Every way this workflow can be started."
    breadcrumbs={[
      { label: 'Workflows', href: '/workflows' },
      ...(workflow ? [{ label: workflow.name, href: `/workflows/${page.params.id}` }] : []),
      { label: 'Triggers' },
    ]}
  >
    {#snippet actions()}
      <Button size="sm" variant="secondary" icon={Hand} onclick={() => openAdd('manual')}>Manual</Button>
      <Button size="sm" variant="success" icon={Webhook} onclick={() => openAdd('webhook')}>Webhook</Button>
      <Button size="sm" variant="primary" icon={CalendarClock} href="/workflows/{page.params.id}/schedules/new">Schedule</Button>
      <Button size="sm" variant="secondary" icon={Radio} onclick={() => openAdd('event')}>Event</Button>
    {/snippet}
  </PageHeader>

  {#if listError}<Alert tone="error" dismissible onDismiss={() => (listError = '')}>{listError}</Alert>{/if}

  <Tabs
    bind:value={activeTab}
    tabs={[
      { value: 'all', label: 'All', count: counts.all },
      { value: 'schedule', label: 'Schedule', count: counts.schedule },
      { value: 'webhook', label: 'Webhook', count: counts.webhook },
      { value: 'manual', label: 'Manual', count: counts.manual },
      { value: 'event', label: 'Event', count: counts.event },
    ]}
  />

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if sortedVisibleTriggers.length === 0}
    <Card padding="none">
      <EmptyState icon={Zap} title={activeTab === 'all' ? 'No triggers yet' : `No ${activeTab} triggers yet`} description="Use the buttons above to add one." />
    </Card>
  {:else}
    <DataTable>
      <thead>
        <tr>
          <th>Name</th>
          <th>Type</th>
          <th>Detail</th>
          <th>Status</th>
          <th>Created</th>
          <th class="!text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each sortedVisibleTriggers as trigger (trigger.id)}
          <tr>
            <td>
              <div class="font-medium text-surface-900-100">{trigger.name}</div>
              {#if trigger.description}<div class="text-xs text-surface-500 mt-0.5">{trigger.description}</div>{/if}
            </td>
            <td><Badge tone={typeTone[trigger.type] ?? 'neutral'}>{isWebhook(trigger) ? 'webhook' : trigger.type}</Badge></td>
            <td class="text-sm font-mono text-surface-700-300">{describe(trigger)}</td>
            <td>
              {#if !trigger.enabled}
                <Badge tone="warning">disabled</Badge>
              {:else if trigger.last_run_status}
                <Badge tone="success">{trigger.last_run_status}</Badge>
              {:else}
                <Badge tone="primary">enabled</Badge>
              {/if}
            </td>
            <td class="text-xs text-surface-600-400">{formatDateTime(trigger.created_at)}</td>
            <td class="text-right whitespace-nowrap space-x-1">
              {#if trigger.type === 'schedule'}
                <Button size="xs" variant="ghost" icon={Edit} href="/workflows/{page.params.id}/schedules/{trigger.id}/edit">Edit</Button>
              {/if}
              {#if isWebhook(trigger)}
                <Button size="xs" variant="ghost" icon={Webhook} onclick={() => openWebhook(trigger)}>Webhook</Button>
              {/if}
              <Button size="xs" variant="ghost" icon={Power} onclick={() => toggleTrigger(trigger)}>{trigger.enabled ? 'Disable' : 'Enable'}</Button>
              <Button size="xs" variant="danger" icon={Trash2} onclick={() => deleteTrigger(trigger)}>Delete</Button>
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
  {/if}
</div>

<Dialog bind:open={showAddDialog} title={addingType ? `New ${addingType} trigger` : ''} size="md" onClose={closeAdd}>
  <div class="space-y-3">
    <Input label="Name" help="schedule.name" bind:value={formName} placeholder="e.g. Deploy on push from CI" />
    <Input label="Description (optional)" help="workflows.description" bind:value={formDescription} placeholder="Short note about when to use this trigger" />

    {#if addingType === 'webhook'}
      <Alert tone="info">
        A public POST endpoint and an HMAC signing secret are generated on create. The
        request body becomes the workflow input. You'll get the URL and secret next — the
        secret is shown only once.
      </Alert>
    {/if}

    {#if addingType === 'event'}
      <Alert tone="warning">
        Event triggers are defined here but not currently dispatched — the event subsystem is planned for a later phase.
      </Alert>
    {/if}

    {#if formError}<Alert tone="error">{formError}</Alert>{/if}
  </div>
  {#snippet footer()}
    <Button variant="ghost" onclick={closeAdd}>Cancel</Button>
    <Button variant="primary" onclick={createTrigger} loading={saving}>Create trigger</Button>
  {/snippet}
</Dialog>

<Dialog bind:open={showWebhookDialog} title="Webhook endpoint" size="md" onClose={() => (showWebhookDialog = false)}>
  {#if webhookTrigger}
    <div class="space-y-4">
      <div>
        <div class="text-xs font-medium text-surface-600-400 mb-1">Endpoint URL</div>
        <div class="flex items-center gap-2">
          <code class="flex-1 min-w-0 truncate rounded-lg border border-surface-200-800 bg-surface-100-900/40 px-3 py-1.5 text-xs font-mono">{webhookUrl(webhookTrigger)}</code>
          <Button size="xs" variant="ghost" icon={Copy} onclick={() => copy(webhookUrl(webhookTrigger!), 'URL copied')}>Copy</Button>
        </div>
        <p class="text-[11px] text-surface-500 mt-1">
          POST your payload here. Authenticate with an <strong>HTTP header</strong> (not in the body):
          <code class="font-mono">X-FlowWeaver-Token: &lt;secret&gt;</code>, or
          <code class="font-mono">X-FlowWeaver-Signature: sha256=&lt;HMAC of the body&gt;</code>.
          The body becomes the run input.
        </p>
      </div>

      <div>
        <div class="text-xs font-medium text-surface-600-400 mb-1">Targeting</div>
        <p class="text-[11px] text-surface-500">
          {#if webhookTrigger.allow_target_override}
            <span class="text-warning-500">The body may choose targets.</span>
            Include <code class="font-mono">target_devices</code> /
            <code class="font-mono">target_pools</code> (arrays of ids) in the payload.
            {#if (webhookTrigger.target_devices?.length ?? 0) > 0}
              This trigger is scoped to {webhookTrigger.target_devices?.length} device(s), so the
              body can only narrow that list — ids outside it are dropped.
            {:else}
              This trigger has no configured devices, so the body picks freely.
            {/if}
          {:else}
            The run always fires against this trigger's configured devices.
            <code class="font-mono">target_devices</code> /
            <code class="font-mono">target_pools</code> in the body are ignored — set
            <code class="font-mono">allow_target_override</code> on the trigger to honour them.
            Leaving it off means a leaked secret can only re-fire the workflow at the devices
            you already chose.
          {/if}
        </p>
      </div>

      {#if revealedSecret}
        <div>
          <div class="text-xs font-medium text-surface-600-400 mb-1">Signing secret</div>
          <div class="flex items-center gap-2">
            <code class="flex-1 min-w-0 truncate rounded-lg border border-warning-500/40 bg-warning-500/5 px-3 py-1.5 text-xs font-mono">{revealedSecret}</code>
            <Button size="xs" variant="ghost" icon={Copy} onclick={() => copy(revealedSecret!, 'Secret copied')}>Copy</Button>
          </div>
          <div class="mt-2">
            <Alert tone="warning">
              Copy this now — it won't be shown again. Store it in your webhook sender's secret field.
            </Alert>
          </div>
        </div>
      {:else}
        <div class="text-xs text-surface-500">
          {#if webhookTrigger.has_webhook_secret}
            A signing secret is set (hidden). Rotate it if you need a new one.
          {:else if webhookTrigger.allow_unsigned}
            <span class="text-warning-500">Unsigned deliveries are allowed</span> — anyone with the URL can fire this. Rotate in a secret to require signatures.
          {:else}
            No signing secret configured.
          {/if}
        </div>
      {/if}
    </div>
  {/if}
  {#snippet footer()}
    <Button variant="ghost" onclick={() => (showWebhookDialog = false)}>Close</Button>
    <Button variant="secondary" icon={KeyRound} onclick={rotateSecret} loading={rotating}>Rotate secret</Button>
  {/snippet}
</Dialog>
