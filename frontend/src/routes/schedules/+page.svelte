<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { workflows, workflowTriggers, errorMessage, type WorkflowTrigger } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, IconButton, Badge, StatCard,
    EmptyState, Alert, Spinner, SearchInput, Select, formatDateTime, confirm,
  } from '$lib/components/ui';
  import {
    CalendarClock, CalendarDays, RefreshCw, Power, Trash2, ArrowUpDown,
    Calendar, CheckCircle2, PauseCircle,
  } from 'lucide-svelte';

  type EnrichedTrigger = WorkflowTrigger & { workflow_name: string };

  let allTriggers = $state<EnrichedTrigger[]>([]);
  let workflowMap = $state<Map<string, string>>(new Map());
  let loading = $state(true);
  let listError = $state('');
  // Per-trigger in-flight flag so the toggle can't be double-clicked mid-request.
  let togglingId = $state<string | null>(null);

  let filterStatus = $state<'all' | 'enabled' | 'disabled'>('all');
  let filterText = $state('');
  let sortKey = $state<'next_run_at' | 'name' | 'workflow_name'>('next_run_at');
  let sortDir = $state<'asc' | 'desc'>('asc');

  const filteredTriggers = $derived.by(() => {
    const text = filterText.trim().toLowerCase();
    let out = allTriggers.filter((t) => {
      if (filterStatus === 'enabled' && !t.enabled) return false;
      if (filterStatus === 'disabled' && t.enabled) return false;
      if (text) {
        const hay = `${t.name} ${t.workflow_name} ${t.cron_expression ?? ''} ${t.timezone ?? ''}`.toLowerCase();
        if (!hay.includes(text)) return false;
      }
      return true;
    });
    out = [...out].sort((a, b) => {
      let av: string | number = '';
      let bv: string | number = '';
      if (sortKey === 'next_run_at') {
        av = a.next_run_at ? new Date(a.next_run_at).getTime() : Number.MAX_SAFE_INTEGER;
        bv = b.next_run_at ? new Date(b.next_run_at).getTime() : Number.MAX_SAFE_INTEGER;
      } else if (sortKey === 'name') {
        av = a.name.toLowerCase(); bv = b.name.toLowerCase();
      } else if (sortKey === 'workflow_name') {
        av = a.workflow_name.toLowerCase(); bv = b.workflow_name.toLowerCase();
      }
      if (av < bv) return sortDir === 'asc' ? -1 : 1;
      if (av > bv) return sortDir === 'asc' ? 1 : -1;
      return 0;
    });
    return out;
  });

  const counts = $derived({
    total: allTriggers.length,
    enabled: allTriggers.filter((t) => t.enabled).length,
    disabled: allTriggers.filter((t) => !t.enabled).length,
  });

  onMount(loadAll);

  async function loadAll() {
    loading = true;
    listError = '';
    try {
      const [triggerResp, draftWfs, prodWfs] = await Promise.all([
        workflowTriggers.list(500, 0),
        workflows.list(500, 0, 'draft'),
        workflows.list(500, 0, 'production'),
      ]);
      const map = new Map<string, string>();
      for (const wf of draftWfs?.data ?? []) if (wf?.id) map.set(wf.id, wf.name ?? '(unnamed)');
      for (const wf of prodWfs?.data ?? []) if (wf?.id && !map.has(wf.id)) map.set(wf.id, wf.name ?? '(unnamed)');
      workflowMap = map;
      allTriggers = (triggerResp?.data ?? [])
        .filter((t) => t.type === 'schedule')
        .map((t) => ({ ...t, workflow_name: map.get(t.workflow_id) ?? '(unknown workflow)' }));
    } catch (e) {
      listError = errorMessage(e);
          toast.fromError(e, 'Couldn’t load all');
    } finally {
      loading = false;
    }
  }

  async function toggleTrigger(t: EnrichedTrigger) {
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

  async function deleteTrigger(t: EnrichedTrigger) {
    if (!(await confirm({ title: `Delete schedule "${t.name}"?`, message: `On workflow "${t.workflow_name}". This cannot be undone.`, tone: 'danger', confirmLabel: 'Delete' }))) return;
    try { await workflowTriggers.delete(t.id); await loadAll(); }
    catch (e) { listError = errorMessage(e);       toast.fromError(e, 'Couldn’t delete trigger');
    }
  }

  function setSort(key: typeof sortKey) {
    if (sortKey === key) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    else { sortKey = key; sortDir = 'asc'; }
  }

  function describeRepeat(t: WorkflowTrigger): string {
    const cron = (t.cron_expression ?? '').trim();
    let m: RegExpMatchArray | null;
    // 6-field interval patterns (from the Interval builder).
    m = cron.match(/^\*\/(\d+)\s+\*\s+\*\s+\*\s+\*\s+\*$/);
    if (m) return `Every ${m[1]}s`;
    m = cron.match(/^0\s+\*\/(\d+)\s+\*\s+\*\s+\*\s+\*$/);
    if (m) return `Every ${m[1]} min`;
    m = cron.match(/^0\s+0\s+\*\/(\d+)\s+\*\s+\*\s+\*$/);
    if (m) return `Every ${m[1]}h`;
    m = cron.match(/^0\s+0\s+0\s+\*\/(\d+)\s+\*\s+\*$/);
    if (m) return m[1] === '1' ? 'Daily 00:00' : `Every ${m[1]} days`;
    m = cron.match(/^(\d+)\s+\*\s+\*\s+\*\s+\*$/);
    if (m) return `Hourly :${m[1].padStart(2, '0')}`;
    m = cron.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+1-5$/);
    if (m) return `Weekdays ${m[2].padStart(2, '0')}:${m[1].padStart(2, '0')}`;
    m = cron.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+(\d)$/);
    if (m) {
      const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
      return `${days[parseInt(m[3], 10)] ?? '?'} ${m[2].padStart(2, '0')}:${m[1].padStart(2, '0')}`;
    }
    m = cron.match(/^(\d+)\s+(\d+)\s+(\d+)\s+\*\s+\*$/);
    if (m) return `Day ${m[3]} ${m[2].padStart(2, '0')}:${m[1].padStart(2, '0')}`;
    m = cron.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+\*$/);
    if (m) return `Daily ${m[2].padStart(2, '0')}:${m[1].padStart(2, '0')}`;
    return cron || '(no cron)';
  }

  function arrow(key: typeof sortKey): string {
    if (sortKey !== key) return '';
    return sortDir === 'asc' ? ' ↑' : ' ↓';
  }
</script>

<svelte:head><title>Schedules · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader title="All schedules" description="Every cron-driven schedule across every workflow.">
    {#snippet actions()}
      <Button variant="secondary" icon={CalendarDays} href="/schedules/calendar">Calendar view</Button>
    {/snippet}
  </PageHeader>

  <div class="grid grid-cols-3 gap-3">
    <StatCard label="Total" value={counts.total} icon={Calendar} />
    <StatCard label="Enabled" value={counts.enabled} icon={CheckCircle2} tone="success" />
    <StatCard label="Disabled" value={counts.disabled} icon={PauseCircle} tone="warning" />
  </div>

  <div class="flex items-center gap-3 flex-wrap">
    <SearchInput bind:value={filterText} placeholder="Filter by name, workflow, cron, timezone…" width="w-80" />
    <Select bind:value={filterStatus}>
      <option value="all">All ({counts.total})</option>
      <option value="enabled">Enabled ({counts.enabled})</option>
      <option value="disabled">Disabled ({counts.disabled})</option>
    </Select>
    <Button icon={RefreshCw} variant="ghost" onclick={loadAll}>Refresh</Button>
  </div>

  {#if listError}<Alert tone="error" dismissible onDismiss={() => (listError = '')}>{listError}</Alert>{/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if allTriggers.length === 0}
    <Card padding="none">
      <EmptyState
        icon={CalendarClock}
        title="No schedules yet"
        description='Add one from any workflow page: Workflows → pick a workflow → Schedules.'
      />
    </Card>
  {:else if filteredTriggers.length === 0}
    <Card><p class="text-sm text-surface-500 text-center py-6">No schedules match the current filter.</p></Card>
  {:else}
    <DataTable>
      <thead>
        <tr>
          <th>
            <button type="button" onclick={() => setSort('workflow_name')} class="flex items-center gap-1 hover:text-surface-700-300 transition-colors">
              Workflow{arrow('workflow_name')} <ArrowUpDown size={10} class="opacity-40" />
            </button>
          </th>
          <th>
            <button type="button" onclick={() => setSort('name')} class="flex items-center gap-1 hover:text-surface-700-300 transition-colors">
              Schedule{arrow('name')} <ArrowUpDown size={10} class="opacity-40" />
            </button>
          </th>
          <th>Repeat</th>
          <th>Timezone</th>
          <th>
            <button type="button" onclick={() => setSort('next_run_at')} class="flex items-center gap-1 hover:text-surface-700-300 transition-colors">
              Next run{arrow('next_run_at')} <ArrowUpDown size={10} class="opacity-40" />
            </button>
          </th>
          <th>Last run</th>
          <th>Status</th>
          <th class="!text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each filteredTriggers as trigger (trigger.id)}
          <tr>
            <td>
              <a href="/workflows/{trigger.workflow_id}" class="text-primary-300 hover:text-primary-200 transition-colors font-medium">{trigger.workflow_name}</a>
            </td>
            <td>
              <div class="font-medium text-surface-900-100">{trigger.name}</div>
              <div class="text-xs font-mono text-surface-500 mt-0.5">{trigger.cron_expression}</div>
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
              <Button size="xs" variant="ghost" href="/workflows/{trigger.workflow_id}/schedules">Open</Button>
              <IconButton icon={Power} label={trigger.enabled ? 'Disable' : 'Enable'} loading={togglingId === trigger.id} onclick={() => toggleTrigger(trigger)} />
              <IconButton icon={Trash2} label="Delete" variant="danger" onclick={() => deleteTrigger(trigger)} />
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
  {/if}
</div>
