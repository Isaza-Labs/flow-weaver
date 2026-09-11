<script lang="ts">
  import { untrack } from 'svelte';
  import { workflows, workflowTriggers, type WorkflowTrigger } from '$lib/api/client';
  import { Cron } from 'croner';
  import ParamsEditor from '$lib/components/ParamsEditor.svelte';
  import DevicePicker from '$lib/components/DevicePicker.svelte';
  import { FieldHint } from '$lib/components/ui';
  import { buildSchemaDefaults } from '$lib/workflow/schemaDefaults';

  // Unique per component instance. The labels below need an explicit
  // `for`/`id`: without one a <label> binds to its first LABELABLE descendant,
  // and <button> is labelable — so FieldHint's info button (which precedes the
  // control) became the labeled control and clicking the label did nothing.
  // A literal id would collide if this form is rendered more than once.
  const uid = $props.id();

  type RepeatMode = 'interval' | 'hourly' | 'daily' | 'weekdays' | 'weekly' | 'monthly' | 'custom';
  type IntervalUnit = 'seconds' | 'minutes' | 'hours' | 'days';

  let {
    workflowId,
    existingTrigger = null,
    inputSchema = {},
    environment = '',
    onSaved,
    onCancel,
  }: {
    workflowId: string;
    existingTrigger?: WorkflowTrigger | null;
    inputSchema?: Record<string, unknown>;
    // Drives the QA lab gate in the device picker — scheduled runs go
    // through the same target resolution as manual ones.
    environment?: string;
    onSaved: (saved: WorkflowTrigger) => void;
    onCancel: () => void;
  } = $props();

  // ---------- Form state ----------

  let formName = $state(untrack(() => existingTrigger?.name ?? ''));
  let formMode = $state<RepeatMode>('daily');
  // Interval builder — emits sub-minute-capable 6-field cron (e.g. every 30s).
  let formIntervalValue = $state(5);
  let formIntervalUnit = $state<IntervalUnit>('minutes');
  let formMinute = $state(0);
  let formHour = $state(9);
  let formDayOfWeek = $state(1);
  let formDayOfMonth = $state(1);
  let formCustomCron = $state(untrack(() => existingTrigger?.cron_expression ?? '0 9 * * *'));
  let formTimezone = $state(untrack(() => existingTrigger?.timezone ?? 'UTC'));
  let formEnabled = $state(untrack(() => existingTrigger?.enabled ?? true));
  let formWebhookURL = $state(untrack(() => existingTrigger?.notification_webhook_url ?? ''));
  let formNotifyOnFailed = $state(untrack(() => (existingTrigger?.notify_on ?? ['failed']).includes('failed')));
  let formNotifyOnCompleted = $state(untrack(() => (existingTrigger?.notify_on ?? ['failed']).includes('completed')));
  // Runtime inputs baked into every fire of this schedule. Seeded from the
  // workflow's declared schema defaults, then overlaid with whatever this
  // schedule already saved. Persisted to `input_defaults`; the scheduler
  // re-reads it on each run, so the values stay static until edited here.
  let formInputDefaults = $state<Record<string, unknown>>(
    untrack(() => ({
      ...buildSchemaDefaults(inputSchema),
      ...(existingTrigger?.input_defaults ?? {}),
    })),
  );
  // Devices this schedule targets — GUID strings, seeded from the trigger on
  // edit. Sent as target_devices; the scheduler copies them onto every run.
  let formTargetDevices = $state<string[]>(untrack(() => existingTrigger?.target_devices ?? []));
  let formError = $state('');
  let saving = $state(false);

  const repeatModes: { value: RepeatMode; label: string }[] = [
    { value: 'interval', label: 'Interval' },
    { value: 'hourly', label: 'Hourly' },
    { value: 'daily', label: 'Daily' },
    { value: 'weekdays', label: 'Weekdays' },
    { value: 'weekly', label: 'Weekly' },
    { value: 'monthly', label: 'Monthly' },
    { value: 'custom', label: 'Custom' },
  ];

  const daysOfWeek = [
    { value: 0, label: 'Sunday' },
    { value: 1, label: 'Monday' },
    { value: 2, label: 'Tuesday' },
    { value: 3, label: 'Wednesday' },
    { value: 4, label: 'Thursday' },
    { value: 5, label: 'Friday' },
    { value: 6, label: 'Saturday' },
  ];

  // Fallback preset list — used only if the browser lacks
  // Intl.supportedValuesOf (all current evergreen browsers ship it).
  const timezonePresets = [
    'UTC',
    'America/New_York',
    'America/Chicago',
    'America/Denver',
    'America/Los_Angeles',
    'Europe/London',
    'Europe/Berlin',
    'Asia/Tokyo',
    'Asia/Singapore',
    'Australia/Sydney',
  ];

  // Full IANA timezone list backing the <select>. UTC is pinned first and
  // the currently-selected value is guaranteed present, so editing a
  // schedule never silently drops an unusual zone (the old datalist only
  // ever surfaced the 10 presets, which is why the field looked empty).
  const timezoneOptions: string[] = untrack(() => {
    const supported = (Intl as { supportedValuesOf?: (key: string) => string[] })
      .supportedValuesOf;
    let zones: string[] = [];
    try {
      zones = supported ? supported('timeZone') : [];
    } catch {
      zones = [];
    }
    if (zones.length === 0) zones = timezonePresets;
    const ordered = ['UTC', ...zones.filter((z) => z !== 'UTC')];
    if (formTimezone && !ordered.includes(formTimezone)) ordered.unshift(formTimezone);
    return ordered;
  });

  // Reverse-parse the existing cron expression into picker state on mount.
  // Best-effort — falls back to Custom mode if no pattern matches.
  $effect(() => {
    if (existingTrigger?.cron_expression) {
      parseExistingCron(existingTrigger.cron_expression);
    }
  });

  function parseExistingCron(cron: string) {
    const trimmed = cron.trim();
    formCustomCron = trimmed || '0 9 * * *';
    let m: RegExpMatchArray | null;
    // 6-field interval patterns emitted by the Interval builder.
    m = trimmed.match(/^\*\/(\d+)\s+\*\s+\*\s+\*\s+\*\s+\*$/);
    if (m) { formMode = 'interval'; formIntervalUnit = 'seconds'; formIntervalValue = parseInt(m[1], 10); return; }
    m = trimmed.match(/^0\s+\*\/(\d+)\s+\*\s+\*\s+\*\s+\*$/);
    if (m) { formMode = 'interval'; formIntervalUnit = 'minutes'; formIntervalValue = parseInt(m[1], 10); return; }
    m = trimmed.match(/^0\s+0\s+\*\/(\d+)\s+\*\s+\*\s+\*$/);
    if (m) { formMode = 'interval'; formIntervalUnit = 'hours'; formIntervalValue = parseInt(m[1], 10); return; }
    m = trimmed.match(/^0\s+0\s+0\s+\*\/(\d+)\s+\*\s+\*$/);
    if (m) { formMode = 'interval'; formIntervalUnit = 'days'; formIntervalValue = parseInt(m[1], 10); return; }
    m = trimmed.match(/^(\d+)\s+\*\s+\*\s+\*\s+\*$/);
    if (m) { formMode = 'hourly'; formMinute = parseInt(m[1], 10); return; }
    m = trimmed.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+1-5$/);
    if (m) { formMode = 'weekdays'; formMinute = parseInt(m[1], 10); formHour = parseInt(m[2], 10); return; }
    m = trimmed.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+(\d)$/);
    if (m) { formMode = 'weekly'; formMinute = parseInt(m[1], 10); formHour = parseInt(m[2], 10); formDayOfWeek = parseInt(m[3], 10); return; }
    m = trimmed.match(/^(\d+)\s+(\d+)\s+(\d+)\s+\*\s+\*$/);
    if (m) { formMode = 'monthly'; formMinute = parseInt(m[1], 10); formHour = parseInt(m[2], 10); formDayOfMonth = parseInt(m[3], 10); return; }
    m = trimmed.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+\*$/);
    if (m) { formMode = 'daily'; formMinute = parseInt(m[1], 10); formHour = parseInt(m[2], 10); return; }
    formMode = 'custom';
  }

  // ---------- Derived ----------

  // Builds a fixed-interval cron. Seconds/minutes use a 6-field expression
  // (leading seconds column) so sub-minute cadence like every-30s is exact.
  // `*/n` divides each parent cycle, so values that divide evenly (30s, 15m,
  // 2h) are perfectly spaced; the Next-runs preview shows the real cadence.
  function intervalCron(value: number, unit: IntervalUnit): string {
    const n = Math.max(1, Math.floor(Number(value) || 1));
    switch (unit) {
      case 'seconds': return `*/${Math.min(n, 59)} * * * * *`;
      case 'minutes': return `0 */${Math.min(n, 59)} * * * *`;
      case 'hours':   return `0 0 */${Math.min(n, 23)} * * *`;
      case 'days':    return `0 0 0 */${Math.min(n, 31)} * *`;
    }
  }

  let derivedCron = $derived.by((): string => {
    const m = formMinute;
    const h = formHour;
    switch (formMode) {
      case 'interval': return intervalCron(formIntervalValue, formIntervalUnit);
      case 'hourly': return `${m} * * * *`;
      case 'daily': return `${m} ${h} * * *`;
      case 'weekdays': return `${m} ${h} * * 1-5`;
      case 'weekly': return `${m} ${h} * * ${formDayOfWeek}`;
      case 'monthly': return `${m} ${h} ${formDayOfMonth} * *`;
      case 'custom': return formCustomCron.trim();
    }
  });

  let nextRuns = $derived.by((): Date[] => {
    const expr = derivedCron;
    const tz = formTimezone || 'UTC';
    if (!expr) return [];
    try {
      const job = new Cron(expr, { timezone: tz });
      const out: Date[] = [];
      let from = new Date();
      for (let i = 0; i < 5; i++) {
        const next = job.nextRun(from);
        if (!next) break;
        out.push(next);
        from = next;
      }
      return out;
    } catch {
      return [];
    }
  });

  let cronError = $derived.by((): string => {
    if (!derivedCron) return 'Cron expression is empty';
    try {
      new Cron(derivedCron, { timezone: formTimezone || 'UTC' });
      return '';
    } catch (e: any) {
      return e?.message ?? 'Invalid cron expression';
    }
  });

  // ---------- Save ----------

  async function save() {
    formError = '';
    if (!formName.trim()) {
      formError = 'Name is required';
      return;
    }
    if (cronError) {
      formError = cronError;
      return;
    }
    const notifyOn: string[] = [];
    if (formNotifyOnFailed) notifyOn.push('failed');
    if (formNotifyOnCompleted) notifyOn.push('completed');

    saving = true;
    try {
      let result: WorkflowTrigger;
      if (existingTrigger) {
        result = await workflowTriggers.update(existingTrigger.id, {
          name: formName.trim(),
          cron_expression: derivedCron,
          timezone: formTimezone.trim() || 'UTC',
          enabled: formEnabled,
          input_defaults: formInputDefaults,
          target_devices: formTargetDevices,
          notification_webhook_url: formWebhookURL.trim() || null,
          notify_on: notifyOn,
        });
      } else {
        result = await workflows.createTrigger(workflowId, {
          name: formName.trim(),
          type: 'schedule',
          cron_expression: derivedCron,
          timezone: formTimezone.trim() || 'UTC',
          enabled: formEnabled,
          input_defaults: formInputDefaults,
          target_devices: formTargetDevices,
          notification_webhook_url: formWebhookURL.trim() || null,
          notify_on: notifyOn,
        });
      }
      onSaved(result);
    } catch (e: any) {
      formError = e?.message ?? 'Failed to save schedule';
    } finally {
      saving = false;
    }
  }

  function formatPreview(d: Date): string {
    return d.toLocaleString(undefined, {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      timeZoneName: 'short',
    });
  }
</script>

<div class="card preset-filled-surface-100-900 border border-primary-500/40">
  <header class="px-5 py-3 border-b border-surface-200-800 flex items-center justify-between">
    <h2 class="h5">{existingTrigger ? `Edit "${existingTrigger.name}"` : 'New schedule'}</h2>
    <button type="button" onclick={onCancel} class="btn-icon btn-icon-sm preset-tonal" aria-label="Close">✕</button>
  </header>

  <div class="px-5 py-4 space-y-5">
    <label class="label" for="{uid}-name">
      <span class="label-text inline-flex items-center gap-1">Name <FieldHint id="schedule.name" /></span>
      <input id="{uid}-name" type="text" bind:value={formName} placeholder="e.g. Hourly LLDP sync" class="input" />
    </label>

    <div>
      <span class="label-text mb-2 inline-flex items-center gap-1">Repeat <FieldHint id="schedule.repeat" /></span>
      <div class="flex flex-wrap gap-2">
        {#each repeatModes as m}
          <button
            type="button"
            onclick={() => (formMode = m.value)}
            class="btn btn-sm {formMode === m.value ? 'preset-filled-primary-500' : 'preset-tonal'}"
          >
            {m.label}
          </button>
        {/each}
      </div>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
      {#if formMode === 'interval'}
        <label class="label">
          <span class="label-text">Every</span>
          <input type="number" min="1" bind:value={formIntervalValue} class="input" />
        </label>
        <label class="label">
          <span class="label-text">Unit</span>
          <select bind:value={formIntervalUnit} class="select">
            <option value="seconds">Seconds</option>
            <option value="minutes">Minutes</option>
            <option value="hours">Hours</option>
            <option value="days">Days</option>
          </select>
        </label>
        <p class="sm:col-span-2 text-xs opacity-60">
          Runs at every <code class="font-mono">*/n</code> boundary of the parent cycle. Values that divide
          evenly (e.g. 30s, 15m, 2h) are perfectly spaced — check the Next runs preview below.
        </p>
      {:else if formMode === 'hourly'}
        <label class="label">
          <span class="label-text">Minute of hour</span>
          <input type="number" min="0" max="59" bind:value={formMinute} class="input" />
        </label>
      {:else if formMode === 'daily' || formMode === 'weekdays'}
        <label class="label">
          <span class="label-text">Hour</span>
          <input type="number" min="0" max="23" bind:value={formHour} class="input" />
        </label>
        <label class="label">
          <span class="label-text">Minute</span>
          <input type="number" min="0" max="59" bind:value={formMinute} class="input" />
        </label>
      {:else if formMode === 'weekly'}
        <label class="label">
          <span class="label-text">Day of week</span>
          <select bind:value={formDayOfWeek} class="select">
            {#each daysOfWeek as d}
              <option value={d.value}>{d.label}</option>
            {/each}
          </select>
        </label>
        <div class="grid grid-cols-2 gap-2">
          <label class="label">
            <span class="label-text">Hour</span>
            <input type="number" min="0" max="23" bind:value={formHour} class="input" />
          </label>
          <label class="label">
            <span class="label-text">Minute</span>
            <input type="number" min="0" max="59" bind:value={formMinute} class="input" />
          </label>
        </div>
      {:else if formMode === 'monthly'}
        <label class="label">
          <span class="label-text">Day of month</span>
          <input type="number" min="1" max="31" bind:value={formDayOfMonth} class="input" />
        </label>
        <div class="grid grid-cols-2 gap-2">
          <label class="label">
            <span class="label-text">Hour</span>
            <input type="number" min="0" max="23" bind:value={formHour} class="input" />
          </label>
          <label class="label">
            <span class="label-text">Minute</span>
            <input type="number" min="0" max="59" bind:value={formMinute} class="input" />
          </label>
        </div>
      {:else if formMode === 'custom'}
        <label class="label sm:col-span-2" for="{uid}-cron">
          <span class="label-text"><FieldHint id="schedule.cron" /> Cron — 5 fields, or 6 with a leading seconds column (e.g. <code class="font-mono">*/30 * * * * *</code> = every 30s). <code>@hourly</code> / <code>@daily</code> / <code>@weekly</code> / <code>@monthly</code> also work.</span>
          <input id="{uid}-cron" type="text" bind:value={formCustomCron} placeholder="0 9 * * *" class="input font-mono" />
        </label>
      {/if}
    </div>

    <label class="label" for="{uid}-tz">
      <span class="label-text inline-flex items-center gap-1">Timezone <FieldHint id="schedule.timezone" /></span>
      <select id="{uid}-tz" bind:value={formTimezone} class="select">
        {#each timezoneOptions as tz}
          <option value={tz}>{tz}</option>
        {/each}
      </select>
    </label>

    <!-- Runtime input variables — persisted to input_defaults and passed
         into every scheduled run. The scheduler re-reads them on each fire,
         so they stay static until this schedule is edited. -->
    <fieldset class="card preset-outlined p-4 space-y-3">
      <legend class="px-1 text-xs uppercase opacity-60 inline-flex items-center gap-1">Runtime inputs <FieldHint id="schedule.runtime_inputs" /></legend>
      <p class="text-xs opacity-60">
        Values passed to the workflow on every run of this schedule. They
        stay fixed until you change them here.
      </p>
      <ParamsEditor schema={inputSchema} bind:value={formInputDefaults} />
    </fieldset>

    <!-- Target devices — persisted on the trigger and copied onto every
         scheduled run (per_device nodes fan out over them). Empty = run once
         with no device context. DevicePicker self-loads the device list. -->
    <fieldset class="card preset-outlined p-4 space-y-3">
      <legend class="px-1 text-xs uppercase opacity-60 inline-flex items-center gap-1">Target devices <FieldHint id="schedule.target_devices" /></legend>
      <p class="text-xs opacity-60">
        Devices this schedule runs against. Nodes that run per device fan out
        over them; leave empty to run once with no device context.
      </p>
      <DevicePicker bind:selected={formTargetDevices} {environment} />
    </fieldset>

    <label class="flex items-center gap-2 cursor-pointer">
      <input type="checkbox" bind:checked={formEnabled} class="checkbox" />
      <span class="text-sm inline-flex items-center gap-1">Enabled — schedule will fire automatically <FieldHint id="schedule.enabled" /></span>
    </label>

    <!-- Failure / completion webhook notifications -->
    <fieldset class="card preset-outlined p-4 space-y-3">
      <legend class="px-1 text-xs uppercase opacity-60">Notifications (optional)</legend>

      <label class="label" for="{uid}-hook">
        <span class="label-text inline-flex items-center gap-1">Webhook URL <FieldHint id="schedule.webhook_url" /></span>
        <input
          id="{uid}-hook"
          type="url"
          bind:value={formWebhookURL}
          placeholder="https://hooks.slack.com/services/… or any HTTP endpoint"
          class="input font-mono text-sm"
        />
        <span class="text-xs opacity-60 mt-1">
          POSTed as JSON after each scheduled run completes. Works with
          Slack incoming webhooks, Discord, Microsoft Teams, PagerDuty
          Events API, n8n, or any custom HTTP endpoint that accepts JSON.
          Leave blank to disable.
        </span>
      </label>

      <div class="flex items-center gap-4">
        <label class="flex items-center gap-2 cursor-pointer">
          <input type="checkbox" bind:checked={formNotifyOnFailed} class="checkbox" />
          <span class="text-sm inline-flex items-center gap-1">On failure <FieldHint id="schedule.notify_on_failure" /></span>
        </label>
        <label class="flex items-center gap-2 cursor-pointer">
          <input type="checkbox" bind:checked={formNotifyOnCompleted} class="checkbox" />
          <span class="text-sm inline-flex items-center gap-1">On completion (success) <FieldHint id="schedule.notify_on_completion" /></span>
        </label>
      </div>
    </fieldset>

    <div class="card preset-tonal p-3 space-y-2">
      <div class="flex items-baseline gap-2">
        <span class="text-xs uppercase opacity-60">Resolved cron</span>
        <code class="text-sm font-mono">{derivedCron}</code>
        {#if cronError}
          <span class="badge preset-tonal-error text-[10px] uppercase">invalid</span>
        {/if}
      </div>
      {#if nextRuns.length > 0}
        <div>
          <div class="text-xs uppercase opacity-60 mb-1">Next 5 runs</div>
          <ul class="text-xs space-y-0.5 font-mono">
            {#each nextRuns as run}
              <li>{formatPreview(run)}</li>
            {/each}
          </ul>
        </div>
      {:else if !cronError}
        <div class="text-xs opacity-60">No upcoming runs</div>
      {/if}
    </div>

    {#if formError}
      <aside class="card preset-tonal-error p-2 text-sm">{formError}</aside>
    {/if}
  </div>

  <footer class="px-5 py-3 border-t border-surface-200-800 flex items-center justify-end gap-2">
    <button type="button" onclick={onCancel} class="btn preset-tonal">Cancel</button>
    <button type="button" onclick={save} disabled={saving || !!cronError} class="btn preset-filled-primary-500">
      {saving ? 'Saving…' : existingTrigger ? 'Save changes' : 'Create schedule'}
    </button>
  </footer>
</div>
