<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { workflows, workflowTriggers, errorMessage, type WorkflowTrigger } from '$lib/api/client';
  import { Cron } from 'croner';
  import {
    PageHeader, Card, Button, Badge, IconButton, Alert,
  } from '$lib/components/ui';
  import { ChevronLeft, ChevronRight, X } from 'lucide-svelte';

  type Fire = { when: Date; trigger: WorkflowTrigger; workflowName: string };
  type DayCell = { date: Date; inCurrentMonth: boolean; isToday: boolean; fires: Fire[] };

  let triggers = $state<WorkflowTrigger[]>([]);
  let workflowMap = $state<Map<string, string>>(new Map());
  let loading = $state(true);
  let listError = $state('');

  let viewYear = $state(new Date().getFullYear());
  let viewMonth = $state(new Date().getMonth());
  let selectedDay = $state<Date | null>(null);

  const monthNames = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  const dayNamesShort = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

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
      triggers = (triggerResp?.data ?? []).filter((t) => t.type === 'schedule' && t.enabled && t.cron_expression);
    } catch (e) {
      listError = errorMessage(e);
          toast.fromError(e, 'Couldn’t load all');
    } finally {
      loading = false;
    }
  }

  const monthGrid = $derived.by((): DayCell[] => {
    const firstOfMonth = new Date(viewYear, viewMonth, 1);
    const firstWeekday = firstOfMonth.getDay();
    const gridStart = new Date(viewYear, viewMonth, 1 - firstWeekday);

    const cells: DayCell[] = [];
    const today = new Date();
    const todayKey = `${today.getFullYear()}-${today.getMonth()}-${today.getDate()}`;

    for (let i = 0; i < 42; i++) {
      const date = new Date(gridStart);
      date.setDate(gridStart.getDate() + i);
      const key = `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
      cells.push({ date, inCurrentMonth: date.getMonth() === viewMonth, isToday: key === todayKey, fires: [] });
    }

    const rangeStart = new Date(gridStart); rangeStart.setHours(0, 0, 0, 0);
    const rangeEnd = new Date(gridStart); rangeEnd.setDate(gridStart.getDate() + 42); rangeEnd.setHours(0, 0, 0, 0);

    for (const trigger of triggers) {
      const expr = trigger.cron_expression!;
      const tz = trigger.timezone || 'UTC';
      let job: Cron;
      try { job = new Cron(expr, { timezone: tz }); }
      catch { continue; }
      let from = new Date(rangeStart.getTime() - 1);
      let safety = 500;
      while (safety-- > 0) {
        const next = job.nextRun(from);
        if (!next || next >= rangeEnd) break;
        const idx = Math.floor(
          (new Date(next.getFullYear(), next.getMonth(), next.getDate()).getTime()
            - new Date(gridStart.getFullYear(), gridStart.getMonth(), gridStart.getDate()).getTime())
          / (24 * 60 * 60 * 1000),
        );
        if (idx >= 0 && idx < 42) {
          cells[idx].fires.push({ when: next, trigger, workflowName: workflowMap.get(trigger.workflow_id) ?? '(unknown workflow)' });
        }
        from = next;
      }
    }

    for (const cell of cells) {
      cell.fires.sort((a, b) => a.when.getTime() - b.when.getTime());
    }
    return cells;
  });

  const totalFiresThisMonth = $derived(monthGrid.filter((c) => c.inCurrentMonth).reduce((acc, c) => acc + c.fires.length, 0));
  const selectedDayCell = $derived(
    selectedDay
      ? monthGrid.find(
          (c) => c.date.getFullYear() === selectedDay!.getFullYear()
            && c.date.getMonth() === selectedDay!.getMonth()
            && c.date.getDate() === selectedDay!.getDate(),
        )
      : null,
  );

  function prevMonth() {
    if (viewMonth === 0) { viewMonth = 11; viewYear -= 1; } else viewMonth -= 1;
    selectedDay = null;
  }
  function nextMonth() {
    if (viewMonth === 11) { viewMonth = 0; viewYear += 1; } else viewMonth += 1;
    selectedDay = null;
  }
  function jumpToToday() {
    const t = new Date(); viewYear = t.getFullYear(); viewMonth = t.getMonth(); selectedDay = t;
  }
  function selectCell(cell: DayCell) { selectedDay = cell.date; }
  function formatTime(d: Date): string { return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); }
  function formatLongDate(d: Date): string { return d.toLocaleDateString(undefined, { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' }); }
</script>

<svelte:head><title>Schedule calendar · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Schedule calendar"
    description="All scheduled workflow runs for the visible month, in your local timezone."
    breadcrumbs={[{ label: 'Schedules', href: '/schedules' }, { label: 'Calendar' }]}
  />

  {#if listError}<Alert tone="error">{listError}</Alert>{/if}

  <div class="flex items-center justify-between gap-3 flex-wrap">
    <div class="flex items-center gap-2">
      <IconButton icon={ChevronLeft} label="Previous month" variant="secondary" onclick={prevMonth} />
      <h2 class="text-base font-semibold tracking-tight min-w-48 text-center">{monthNames[viewMonth]} {viewYear}</h2>
      <IconButton icon={ChevronRight} label="Next month" variant="secondary" onclick={nextMonth} />
      <Button size="sm" variant="primary" onclick={jumpToToday}>Today</Button>
    </div>
    <div class="text-sm text-surface-600-400">
      {#if loading}Loading…
      {:else}
        <span class="font-semibold tabular-nums">{totalFiresThisMonth}</span> scheduled runs ·
        <span class="font-semibold tabular-nums">{triggers.length}</span> active schedule{triggers.length === 1 ? '' : 's'}
      {/if}
    </div>
  </div>

  <Card padding="none">
    <div class="grid grid-cols-7 border-b border-surface-200-800">
      {#each dayNamesShort as name}
        <div class="px-2 py-2 text-[10px] font-semibold uppercase tracking-wider text-surface-500 text-center">{name}</div>
      {/each}
    </div>

    <div class="grid grid-cols-7">
      {#each monthGrid as cell, i (i)}
        {@const isSelected = selectedDay && selectedDay.getTime() === cell.date.getTime()}
        <button
          type="button"
          onclick={() => selectCell(cell)}
          class="h-28 p-2 text-left border-b border-r border-surface-200-800/60 transition-colors flex flex-col gap-1 overflow-hidden
                 {cell.inCurrentMonth ? 'bg-surface-100-900' : 'bg-surface-50-950 opacity-50'}
                 {cell.isToday ? 'ring-2 ring-primary-500 ring-inset' : ''}
                 {isSelected ? 'bg-primary-500/15' : 'hover:bg-surface-200-800/40'}"
        >
          <div class="flex items-center justify-between">
            <span class="text-xs font-semibold tabular-nums {cell.isToday ? 'text-primary-300' : 'text-surface-700-300'}">{cell.date.getDate()}</span>
            {#if cell.fires.length > 0}
              <Badge tone="primary" size="xs">{cell.fires.length}</Badge>
            {/if}
          </div>
          <div class="text-[10px] space-y-0.5 overflow-hidden">
            {#each cell.fires.slice(0, 3) as fire}
              <div class="truncate text-surface-600-400">
                <span class="font-mono">{formatTime(fire.when)}</span> · {fire.workflowName}
              </div>
            {/each}
            {#if cell.fires.length > 3}
              <div class="text-[10px] text-surface-500">+{cell.fires.length - 3} more</div>
            {/if}
          </div>
        </button>
      {/each}
    </div>
  </Card>

  {#if selectedDayCell}
    <Card padding="none">
      <header class="px-5 py-3 border-b border-surface-200-800 flex items-center justify-between">
        <h2 class="text-sm font-semibold tracking-tight text-surface-900-100">{formatLongDate(selectedDayCell.date)}</h2>
        <IconButton icon={X} label="Close" onclick={() => (selectedDay = null)} />
      </header>
      <div class="px-5 py-4">
        {#if selectedDayCell.fires.length === 0}
          <p class="text-sm text-surface-500">No scheduled runs on this day.</p>
        {:else}
          <ul class="space-y-2">
            {#each selectedDayCell.fires as fire}
              <li class="flex items-baseline gap-3 text-sm flex-wrap">
                <span class="font-mono text-surface-700-300 tabular-nums w-12">{formatTime(fire.when)}</span>
                <a href="/workflows/{fire.trigger.workflow_id}" class="text-primary-300 hover:text-primary-200">{fire.workflowName}</a>
                <span class="text-surface-400-600">·</span>
                <a href="/workflows/{fire.trigger.workflow_id}/schedules/{fire.trigger.id}/edit" class="text-surface-700-300 hover:text-surface-900-100">{fire.trigger.name}</a>
                <span class="text-surface-400-600">·</span>
                <span class="font-mono text-xs text-surface-500">{fire.trigger.cron_expression}</span>
                <span class="text-surface-400-600">·</span>
                <span class="font-mono text-xs text-surface-500">{fire.trigger.timezone ?? 'UTC'}</span>
              </li>
            {/each}
          </ul>
        {/if}
      </div>
    </Card>
  {/if}
</div>
