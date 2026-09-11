<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { workflows, workflowTriggers, errorMessage, type WorkflowTrigger, type Workflow } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, Badge, EmptyState,
    Alert, Spinner, formatDateTime, confirm,
  } from '$lib/components/ui';
  import { Plus, CalendarClock, Trash2, Power, Edit } from 'lucide-svelte';

  let workflow = $state<Workflow | null>(null);
  let triggers = $state<WorkflowTrigger[]>([]);
  let loading = $state(true);
  let listError = $state('');
  // Per-trigger in-flight flag so the toggle can't be double-clicked mid-request.
  let togglingId = $state<string | null>(null);

  const scheduleTriggers = $derived(triggers.filter((t) => t.type === 'schedule'));

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

  async function toggleTrigger(t: WorkflowTrigger) {
    // Disabling an enabled schedule stops its automated runs, so confirm first.
    if (
      t.enabled &&
      !(await confirm({
        title: 'Disable schedule?',
        message: 'Automated runs will stop.',
        tone: 'danger',
        confirmLabel: 'Disable',
      }))
    )
      return;
    const next = !t.enabled;
    togglingId = t.id;
    try {
      await workflowTriggers.update(t.id, { enabled: next });
      toast.success(next ? 'Schedule enabled' : 'Schedule disabled');
      await loadAll();
    } catch (e) {
      listError = errorMessage(e);
      toast.fromError(e, 'Couldn’t toggle trigger');
    } finally {
      togglingId = null;
    }
  }

  async function deleteTrigger(t: WorkflowTrigger) {
    if (!(await confirm({ title: `Delete schedule "${t.name}"?`, message: 'This cannot be undone.', tone: 'danger', confirmLabel: 'Delete' }))) return;
    try { await workflowTriggers.delete(t.id); await loadAll(); }
    catch (e) { listError = errorMessage(e);       toast.fromError(e, 'Couldn’t delete trigger');
    }
  }

  function describeRepeat(t: WorkflowTrigger): string {
    const cron = t.cron_expression ?? '';
    let m: RegExpMatchArray | null;
    m = cron.match(/^(\d+)\s+\*\s+\*\s+\*\s+\*$/);
    if (m) return `Every hour at :${m[1].padStart(2, '0')}`;
    m = cron.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+1-5$/);
    if (m) return `Weekdays at ${m[2].padStart(2, '0')}:${m[1].padStart(2, '0')}`;
    m = cron.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+(\d)$/);
    if (m) {
      const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
      return `${days[parseInt(m[3], 10)] ?? '?'} at ${m[2].padStart(2, '0')}:${m[1].padStart(2, '0')}`;
    }
    m = cron.match(/^(\d+)\s+(\d+)\s+(\d+)\s+\*\s+\*$/);
    if (m) return `Day ${m[3]} at ${m[2].padStart(2, '0')}:${m[1].padStart(2, '0')}`;
    m = cron.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+\*$/);
    if (m) return `Daily at ${m[2].padStart(2, '0')}:${m[1].padStart(2, '0')}`;
    return cron || '(no cron)';
  }
</script>

<svelte:head><title>Schedules · {workflow?.name ?? 'Workflow'} · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-6xl mx-auto space-y-5">
  <PageHeader
    title="Schedules"
    description="Run this workflow automatically on a recurring schedule."
    breadcrumbs={[
      { label: 'Workflows', href: '/workflows' },
      ...(workflow ? [{ label: workflow.name, href: `/workflows/${page.params.id}` }] : []),
      { label: 'Schedules' },
    ]}
  >
    {#snippet actions()}
      <Button variant="primary" icon={Plus} href="/workflows/{page.params.id}/schedules/new">Add schedule</Button>
    {/snippet}
  </PageHeader>

  {#if listError}<Alert tone="error" dismissible onDismiss={() => (listError = '')}>{listError}</Alert>{/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if scheduleTriggers.length === 0}
    <Card padding="none">
      <EmptyState icon={CalendarClock} title="No schedules yet" description='Click "Add schedule" to run this workflow on a cron expression.' />
    </Card>
  {:else}
    <DataTable>
      <thead>
        <tr>
          <th>Name</th>
          <th>Repeat</th>
          <th>Timezone</th>
          <th>Next run</th>
          <th>Last run</th>
          <th>Status</th>
          <th class="!text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each scheduleTriggers as trigger (trigger.id)}
          <tr>
            <td>
              <div class="font-medium text-surface-900-100">{trigger.name}</div>
              <div class="text-xs text-surface-500 font-mono mt-0.5">{trigger.cron_expression}</div>
            </td>
            <td class="text-sm text-surface-700-300">{describeRepeat(trigger)}</td>
            <td class="text-sm font-mono text-surface-700-300">{trigger.timezone ?? 'UTC'}</td>
            <td class="text-xs text-surface-600-400">{formatDateTime(trigger.next_run_at)}</td>
            <td class="text-xs text-surface-600-400">{formatDateTime(trigger.last_run_at)}</td>
            <td>
              {#if !trigger.enabled}
                <Badge tone="warning">disabled</Badge>
              {:else if trigger.last_run_status}
                <Badge tone="success">{trigger.last_run_status}</Badge>
              {:else}
                <Badge tone="primary">pending</Badge>
              {/if}
            </td>
            <td class="text-right whitespace-nowrap space-x-1">
              <Button size="xs" variant="ghost" icon={Edit} href="/workflows/{page.params.id}/schedules/{trigger.id}/edit">Edit</Button>
              <Button size="xs" variant="ghost" icon={Power} loading={togglingId === trigger.id} onclick={() => toggleTrigger(trigger)}>{trigger.enabled ? 'Disable' : 'Enable'}</Button>
              <Button size="xs" variant="danger" icon={Trash2} onclick={() => deleteTrigger(trigger)}>Delete</Button>
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
  {/if}
</div>
