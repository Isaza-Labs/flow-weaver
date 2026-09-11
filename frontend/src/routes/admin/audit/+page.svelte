<script lang="ts">
  import { onMount } from 'svelte';
  import {
    audit, authEvents, users,
    type AuditEvent, type AuthEvent, type User,
    errorMessage,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Input, Select, Tabs, Spinner, Alert,
    Badge, StatusBadge, EmptyState, Pagination,
    formatDateTime, truncate, toast,
  } from '$lib/components/ui';
  import { ShieldCheck, FileText, RefreshCw, ChevronDown, ChevronRight, Filter, Download } from 'lucide-svelte';

  type TabKey = 'domain' | 'auth';

  // Tab + pagination state. The domain endpoint exposes offset pagination,
  // so its Prev/Next re-fetches by offset. The auth endpoint has no offset
  // param, so we fetch a larger window once (AUTH_FETCH_LIMIT) and page over
  // it client-side for Prev/Next parity with the domain tab.
  let tab = $state<TabKey>('domain');
  let page = $state(0);
  const pageSize = 50;
  const AUTH_FETCH_LIMIT = 500;

  // Shared filters. Dates are <input type="date"> values (local, YYYY-MM-DD);
  // we widen them to UTC day boundaries when serializing to the backend.
  let entityType = $state('');
  let entityId = $state('');
  let actor = $state('');
  let action = $state('');
  let actionPrefix = $state('');
  let authEvent = $state('');
  let userId = $state('');
  let from = $state('');
  let to = $state('');

  // Quick-filter presets surface security-sensitive categories that span
  // multiple action verbs. Selecting one resets the manual filters so the
  // user sees exactly the rows behind the chip.
  type CategoryPreset = {
    key: string;
    label: string;
    entityType: string;
    actionPrefix: string;
    description: string;
  };
  const CATEGORIES: CategoryPreset[] = [
    {
      key: 'allow-private-network',
      label: 'AllowPrivateNetwork',
      entityType: 'integration',
      actionPrefix: 'allow_private_network',
      description: 'SSRF guard opt-out activations, deactivations and daily snapshots.',
    },
    {
      key: 'git-webhook-ingest',
      label: 'Git webhook ingest',
      entityType: 'GitWebhook',
      actionPrefix: 'git_webhook.ingest',
      description: 'Public webhook hits — accepted, rejected, rate limited, queue-full.',
    },
    {
      key: 'slo-breaches',
      label: 'SLO breaches',
      entityType: 'slo',
      actionPrefix: 'slo.breach',
      description: 'Daily sweep flags metrics that crossed their target.',
    },
    {
      key: 'workflow-imports',
      label: 'Workflow imports',
      entityType: 'workflow_import',
      actionPrefix: 'workflow_import',
      description: 'Cross-system imports: started, analyzed, committed, failed.',
    },
  ];
  let activeCategory = $state<string | null>(null);

  function applyCategory(key: string | null) {
    activeCategory = key;
    if (key === null) {
      entityType = '';
      action = '';
      actionPrefix = '';
    } else {
      const preset = CATEGORIES.find((c) => c.key === key);
      if (preset) {
        entityType = preset.entityType;
        action = '';
        actionPrefix = preset.actionPrefix;
      }
    }
    page = 0;
    void load();
  }

  let domainEvents = $state<AuditEvent[]>([]);
  let authEventRows = $state<AuthEvent[]>([]);
  let authFetchTruncated = $state(false);
  let userMap = $state<Map<string, User>>(new Map());
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let expanded = $state<Set<string>>(new Set());

  // Client-side page slice for the auth tab (the endpoint has no offset).
  const authPageRows = $derived(authEventRows.slice(page * pageSize, page * pageSize + pageSize));

  // Auth event kinds that ship in the backend today — keeps the select
  // bounded instead of leaving it freeform and unusable.
  const AUTH_EVENT_KINDS = [
    'login_success', 'login_failure', 'logout',
    'password_change', 'lockout', 'refresh', 'token_revoked',
  ];

  onMount(async () => {
    try {
      const list = await users.list();
      userMap = new Map(list.map((u) => [u.user_id, u]));
    } catch {
      // Non-fatal: we fall back to showing raw user_ids.
    }
    await load();
  });

  // Re-reads the active tab's endpoint. Filters changing calls this with
  // page=0 reset so the user doesn't stay on a page that no longer exists.
  async function load() {
    loading = true;
    loadError = null;
    try {
      if (tab === 'domain') {
        domainEvents = await audit.events({
          entity_type: entityType || undefined,
          entity_id: entityId || undefined,
          action: action || undefined,
          action_prefix: actionPrefix || undefined,
          user_id: userId || undefined,
          actor: actor || undefined,
          from: dayStart(from),
          to: dayEnd(to),
          limit: pageSize,
          offset: page * pageSize,
        });
      } else {
        // /auth/events has no offset param, so we pull the backend's max
        // window (500) once and page over it client-side. If the fetch comes
        // back full we may be hiding older rows — surface that as a note.
        const fetched = await authEvents.list({
          user_id: userId || undefined,
          from: dayStart(from),
          to: dayEnd(to),
          limit: AUTH_FETCH_LIMIT,
        });
        authFetchTruncated = fetched.length >= AUTH_FETCH_LIMIT;
        authEventRows = authEvent
          ? fetched.filter((e) => e.event === authEvent)
          : fetched;
        // The active filter may have shrunk the set below the current page.
        if (page * pageSize >= authEventRows.length) page = 0;
      }
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load events");
    } finally {
      loading = false;
    }
  }

  function dayStart(v: string): string | undefined {
    return v ? new Date(`${v}T00:00:00Z`).toISOString() : undefined;
  }

  function dayEnd(v: string): string | undefined {
    return v ? new Date(`${v}T23:59:59Z`).toISOString() : undefined;
  }

  async function onTabChange(next: TabKey) {
    tab = next;
    page = 0;
    expanded = new Set();
    await load();
  }

  async function onApplyFilters() {
    page = 0;
    await load();
  }

  async function onResetFilters() {
    entityType = '';
    entityId = '';
    actor = '';
    action = '';
    actionPrefix = '';
    authEvent = '';
    userId = '';
    from = '';
    to = '';
    activeCategory = null;
    page = 0;
    await load();
  }

  async function onPageChange(p: number) {
    page = p;
    // Domain pages by offset (needs a refetch); auth pages client-side over
    // the already-fetched window.
    if (tab === 'domain') await load();
  }

  // Download-to-CSV flow. Re-fetches events with the current filters and
  // the backend's max `limit` (500) so the file reflects the active view
  // without being limited to the on-screen page. For true bulk export a
  // streaming /export endpoint would replace this — 500 rows covers the
  // vast majority of audit / compliance workflows we see today.
  let exporting = $state(false);

  async function exportCsv() {
    if (exporting) return;
    exporting = true;
    try {
      const rows = tab === 'domain'
        ? await audit.events({
            entity_type: entityType || undefined,
            entity_id: entityId || undefined,
            action: action || undefined,
            action_prefix: actionPrefix || undefined,
            user_id: userId || undefined,
            actor: actor || undefined,
            from: dayStart(from),
            to: dayEnd(to),
            limit: 500,
            offset: 0,
          })
        : (() => {
            // Auth endpoint has no offset; just pull top 500.
            return authEvents.list({
              user_id: userId || undefined,
              from: dayStart(from),
              to: dayEnd(to),
              limit: 500,
            });
          })();

      const data = await Promise.resolve(rows);
      const filtered = tab === 'auth' && authEvent
        ? (data as AuthEvent[]).filter((e) => e.event === authEvent)
        : data;

      if (!filtered || (filtered as unknown[]).length === 0) {
        toast.info('Nothing to export for the current filters');
        return;
      }

      const csv = tab === 'domain'
        ? toDomainCsv(filtered as AuditEvent[])
        : toAuthCsv(filtered as AuthEvent[]);

      downloadCsv(`${tab}-audit-${timestamp()}.csv`, csv);
    } catch (e) {
      toast.fromError(e, 'Export failed');
    } finally {
      exporting = false;
    }
  }

  function toDomainCsv(rows: AuditEvent[]): string {
    const header = ['at', 'entity_type', 'entity_id', 'action', 'user_id', 'username', 'actor', 'ip', 'request_id', 'before_json', 'after_json'];
    const lines = [header.join(',')];
    for (const r of rows) {
      lines.push([
        r.at,
        r.entity_type,
        r.entity_id ?? '',
        r.action,
        r.user_id ?? '',
        r.user_id ? (userMap.get(r.user_id)?.username ?? '') : '',
        r.actor ?? '',
        r.ip ?? '',
        r.request_id ?? '',
        JSON.stringify(r.before_json ?? {}),
        JSON.stringify(r.after_json ?? {}),
      ].map(csvCell).join(','));
    }
    return lines.join('\n');
  }

  function toAuthCsv(rows: AuthEvent[]): string {
    const header = ['at', 'event', 'user_id', 'username', 'ip', 'user_agent', 'metadata'];
    const lines = [header.join(',')];
    for (const r of rows) {
      lines.push([
        r.at,
        r.event,
        r.user_id ?? '',
        r.user_id ? (userMap.get(r.user_id)?.username ?? '') : '',
        r.ip ?? '',
        r.user_agent ?? '',
        r.metadata ? JSON.stringify(r.metadata) : '',
      ].map(csvCell).join(','));
    }
    return lines.join('\n');
  }

  // RFC 4180 cell: double quotes wrap any cell that contains a comma, a
  // quote, or a newline; internal quotes double up.
  function csvCell(v: unknown): string {
    const s = v == null ? '' : String(v);
    return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  }

  function timestamp(): string {
    const d = new Date();
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}`;
  }

  function downloadCsv(filename: string, content: string) {
    // UTF-8 BOM so Excel opens accented chars correctly.
    const blob = new Blob(['\ufeff' + content], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  }

  function toggleRow(id: string) {
    const next = new Set(expanded);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expanded = next;
  }

  // Automation rows have no user_id — nobody is signed in — so `actor` is
  // what distinguishes a scheduled run from an inbound webhook. Without it
  // every unattended change rendered as an indistinguishable em dash.
  function resolveActor(e: AuditEvent): string {
    if (e.user_id) {
      const u = userMap.get(e.user_id);
      return u ? u.username : truncate(e.user_id, 8);
    }
    return e.actor ?? '—';
  }

  function resolveUser(id: string | null): string {
    if (!id) return '—';
    const u = userMap.get(id);
    return u ? u.username : truncate(id, 8);
  }

  // Event badges lean on StatusBadge's tone mapping: success/failure/
  // lockout read as error, login_success reads as success, etc.
  function authEventTone(evt: string): string {
    if (evt.endsWith('_failure') || evt === 'lockout' || evt === 'token_revoked') return 'failed';
    if (evt === 'login_success') return 'success';
    return 'info';
  }

  // Domain audit actions come through as simple verbs (create/update/delete).
  // Map to a StatusBadge-compatible string so colors stay consistent with
  // everything else in the app.
  function auditActionTone(act: string): string {
    switch (act) {
      case 'create': return 'success';
      case 'delete': return 'failed';
      case 'update': return 'info';
      default: return 'pending';
    }
  }
</script>

<svelte:head><title>Audit log · Admin · Flow Weaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Audit log"
    description="Trail of sign-ins, sensitive writes, and entity mutations."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={Download} loading={exporting} onclick={exportCsv}>Download CSV</Button>
      <Button variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
    {/snippet}
  </PageHeader>

  <div class="flex items-center justify-between gap-3 flex-wrap">
    <Tabs
      bind:value={tab}
      tabs={[
        { value: 'domain' as TabKey, label: 'Domain events' },
        { value: 'auth' as TabKey, label: 'Auth events' },
      ]}
      onChange={onTabChange}
    />
    <div class="text-xs text-surface-500 inline-flex items-center gap-1.5">
      <Filter size={12} /> Filters below are ANDed together.
    </div>
  </div>

  {#if tab === 'domain'}
    <div class="flex flex-wrap items-center gap-2 text-xs">
      <span class="text-surface-500">Categories:</span>
      <button
        type="button"
        class="px-2 py-1 rounded border {activeCategory === null ? 'bg-primary-500 text-white border-primary-500' : 'border-surface-300-700 text-surface-700-300 hover:bg-surface-100-900'}"
        onclick={() => applyCategory(null)}
      >All</button>
      {#each CATEGORIES as cat}
        <button
          type="button"
          title={cat.description}
          class="px-2 py-1 rounded border {activeCategory === cat.key ? 'bg-primary-500 text-white border-primary-500' : 'border-surface-300-700 text-surface-700-300 hover:bg-surface-100-900'}"
          onclick={() => applyCategory(cat.key)}
        >{cat.label}</button>
      {/each}
    </div>
  {/if}

  <!-- Filters -->
  <Card>
    <div class="p-4 grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-3">
      {#if tab === 'domain'}
        <Input label="Entity type" help="audit.entity_type" bind:value={entityType} placeholder="device, workflow, …" />
        <Input label="Entity id" help="audit.entity_id" bind:value={entityId} placeholder="uuid — full history of one record" />
        <Input label="Action" help="audit.action" bind:value={action} placeholder="create / update / delete" />
        <Input label="Actor" help="audit.actor" bind:value={actor} placeholder="username or workflow-runner" />
      {:else}
        <Select label="Event" help="audit.event" bind:value={authEvent}>
          <option value="">All events</option>
          {#each AUTH_EVENT_KINDS as kind}
            <option value={kind}>{kind}</option>
          {/each}
        </Select>
        <div></div>
      {/if}
      <Input label="User id" help="audit.user_id" bind:value={userId} placeholder="uuid of actor" />
      <div class="grid grid-cols-2 gap-2">
        <Input label="From" help="audit.date_range" type="date" bind:value={from} />
        <Input label="To" help="audit.date_range" type="date" bind:value={to} />
      </div>
      <div class="flex items-end gap-2 md:col-span-2 lg:col-span-4">
        <Button variant="primary" onclick={onApplyFilters}>Apply filters</Button>
        <Button variant="ghost" onclick={onResetFilters}>Reset</Button>
      </div>
    </div>
  </Card>

  <!-- Table -->
  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading events…" /></div>
  {:else if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if tab === 'domain'}
    {#if domainEvents.length === 0}
      <Card padding="none">
        <EmptyState
          icon={FileText}
          title="No domain events"
          description="Nothing matches the current filters. Try widening the date range."
        />
      </Card>
    {:else}
      <Card padding="none">
        <table class="w-full text-sm">
          <caption class="sr-only">Domain audit events — entity mutations with before/after state.</caption>
          <thead class="border-b border-surface-200-800">
            <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
              <th class="px-4 py-2 w-8"></th>
              <th class="px-4 py-2">When</th>
              <th class="px-4 py-2">Entity</th>
              <th class="px-4 py-2">Action</th>
              <th class="px-4 py-2">User</th>
              <th class="px-4 py-2">IP</th>
            </tr>
          </thead>
          <tbody>
            {#each domainEvents as e (e.audit_event_id)}
              {@const open = expanded.has(e.audit_event_id)}
              <tr class="border-b border-surface-200-800/50 hover:bg-surface-100-900/50 cursor-pointer"
                  onclick={() => toggleRow(e.audit_event_id)}>
                <td class="px-4 py-2 align-top text-surface-500">
                  {#if open}<ChevronDown size={12} />{:else}<ChevronRight size={12} />{/if}
                </td>
                <td class="px-4 py-2 text-surface-700-300 font-mono tabular-nums whitespace-nowrap">{formatDateTime(e.at)}</td>
                <td class="px-4 py-2">
                  <div class="font-medium text-surface-900-100">{e.entity_type}</div>
                  {#if e.entity_id}
                    <div class="text-[11px] text-surface-500 font-mono">{truncate(e.entity_id, 8)}</div>
                  {/if}
                </td>
                <td class="px-4 py-2"><StatusBadge status={auditActionTone(e.action)} label={e.action} /></td>
                <td class="px-4 py-2 text-surface-700-300">
                  {resolveActor(e)}
                  {#if !e.user_id && e.actor}
                    <div class="text-[11px] text-surface-500">automation</div>
                  {/if}
                </td>
                <td class="px-4 py-2 text-surface-500 font-mono text-[11px]">{e.ip ?? '—'}</td>
              </tr>
              {#if open}
                <tr class="bg-surface-50-950 border-b border-surface-200-800/50">
                  <td colspan="6" class="px-4 py-3 space-y-3 text-xs">
                    <div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
                      <div>
                        <div class="text-[11px] font-semibold uppercase tracking-wide text-surface-500 mb-1">Before</div>
                        <pre class="bg-surface-100-900 rounded p-2 text-[11px] overflow-auto max-h-48">{JSON.stringify(e.before_json ?? {}, null, 2)}</pre>
                      </div>
                      <div>
                        <div class="text-[11px] font-semibold uppercase tracking-wide text-surface-500 mb-1">After</div>
                        <pre class="bg-surface-100-900 rounded p-2 text-[11px] overflow-auto max-h-48">{JSON.stringify(e.after_json ?? {}, null, 2)}</pre>
                      </div>
                    </div>
                    <div class="flex gap-4 text-[11px] text-surface-500">
                      {#if e.request_id}<div>Request: <span class="font-mono text-surface-700-300">{e.request_id}</span></div>{/if}
                      {#if e.user_agent}<div class="truncate">UA: <span class="font-mono text-surface-700-300">{e.user_agent}</span></div>{/if}
                    </div>
                  </td>
                </tr>
              {/if}
            {/each}
          </tbody>
        </table>
      </Card>

      <!-- Offset pagination. The backend doesn't return a total count so we
           enable next when we got a full page and disable when we didn't. -->
      <div class="flex items-center justify-between text-xs text-surface-500">
        <div>Page {page + 1} — showing {domainEvents.length} event{domainEvents.length === 1 ? '' : 's'}</div>
        <div class="flex items-center gap-2">
          <Button size="sm" variant="ghost" disabled={page === 0} onclick={() => onPageChange(Math.max(0, page - 1))}>Prev</Button>
          <Button size="sm" variant="ghost" disabled={domainEvents.length < pageSize} onclick={() => onPageChange(page + 1)}>Next</Button>
        </div>
      </div>
    {/if}
  {:else}
    {#if authEventRows.length === 0}
      <Card padding="none">
        <EmptyState
          icon={ShieldCheck}
          title="No auth events"
          description="Sign-ins, lockouts, and token changes will appear here as they happen."
        />
      </Card>
    {:else}
      {#if authFetchTruncated}
        <Alert tone="info">
          Showing the first {AUTH_FETCH_LIMIT} auth events for these filters. Narrow the date range
          or user to see older entries.
        </Alert>
      {/if}
      <Card padding="none">
        <table class="w-full text-sm">
          <caption class="sr-only">Authentication events — sign-ins, lockouts, and token changes.</caption>
          <thead class="border-b border-surface-200-800">
            <tr class="text-left text-[11px] uppercase tracking-wide text-surface-500">
              <th class="px-4 py-2 w-8"></th>
              <th class="px-4 py-2">When</th>
              <th class="px-4 py-2">Event</th>
              <th class="px-4 py-2">User</th>
              <th class="px-4 py-2">IP</th>
            </tr>
          </thead>
          <tbody>
            {#each authPageRows as e (e.auth_event_id)}
              {@const open = expanded.has(e.auth_event_id)}
              <tr class="border-b border-surface-200-800/50 hover:bg-surface-100-900/50 cursor-pointer"
                  onclick={() => toggleRow(e.auth_event_id)}>
                <td class="px-4 py-2 align-top text-surface-500">
                  {#if open}<ChevronDown size={12} />{:else}<ChevronRight size={12} />{/if}
                </td>
                <td class="px-4 py-2 text-surface-700-300 font-mono tabular-nums whitespace-nowrap">{formatDateTime(e.at)}</td>
                <td class="px-4 py-2"><StatusBadge status={authEventTone(e.event)} label={e.event} /></td>
                <td class="px-4 py-2 text-surface-700-300">{resolveUser(e.user_id)}</td>
                <td class="px-4 py-2 text-surface-500 font-mono text-[11px]">{e.ip ?? '—'}</td>
              </tr>
              {#if open && e.metadata}
                <tr class="bg-surface-50-950 border-b border-surface-200-800/50">
                  <td colspan="5" class="px-4 py-3 text-xs">
                    <div class="text-[11px] font-semibold uppercase tracking-wide text-surface-500 mb-1">Metadata</div>
                    <pre class="bg-surface-100-900 rounded p-2 text-[11px] overflow-auto max-h-48">{JSON.stringify(e.metadata, null, 2)}</pre>
                    {#if e.user_agent}
                      <div class="mt-2 text-[11px] text-surface-500 truncate">UA: <span class="font-mono text-surface-700-300">{e.user_agent}</span></div>
                    {/if}
                  </td>
                </tr>
              {/if}
            {/each}
          </tbody>
        </table>
      </Card>

      <!-- Client-side pagination over the fetched window (the auth endpoint
           has no offset param), mirroring the domain tab's Prev/Next. -->
      <div class="flex items-center justify-between text-xs text-surface-500">
        <div>Page {page + 1} — showing {authPageRows.length} of {authEventRows.length} event{authEventRows.length === 1 ? '' : 's'}</div>
        <div class="flex items-center gap-2">
          <Button size="sm" variant="ghost" disabled={page === 0} onclick={() => onPageChange(Math.max(0, page - 1))}>Prev</Button>
          <Button size="sm" variant="ghost" disabled={(page + 1) * pageSize >= authEventRows.length} onclick={() => onPageChange(page + 1)}>Next</Button>
        </div>
      </div>
    {/if}
  {/if}
</div>
