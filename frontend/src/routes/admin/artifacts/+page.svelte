<script lang="ts">
  // Artifacts — THE admin view over generated documents (the old
  // /admin/reports list was folded into this one). Organised by origin
  // instead of exposing the raw source filter: "Report" is what workflow
  // runs produced, "Export" is everything generated without a workflow —
  // the agent in chat, or a direct API call. Rows link into the
  // /admin/artifacts/{id} detail, which keeps the audit surface
  // (read_prompt rows, origin card) in one place.

  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import {
    adminReports, users, downloadReport,
    type ReportArtifactSummary, type User,
    errorMessage,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Input, Select, Alert, Spinner,
    EmptyState, formatDateTime, toast, DataTable, confirm, Tabs,
  } from '$lib/components/ui';
  import {
    Files, Download, Trash2, RefreshCw, Search, AlertTriangle, Workflow, Bot, Cable,
  } from 'lucide-svelte';

  type Section = 'report' | 'export';

  // Section ↔ source mapping is the whole point of this page: the tab picks
  // the backend `source` filter (comma = OR), so pagination and totals stay
  // server-side. Export covers every non-workflow origin: agent chat + API.
  const SECTION_SOURCE: Record<Section, string> = { report: 'workflow', export: 'agent,api' };

  function sectionFromUrl(): Section {
    return page.url.searchParams.get('section') === 'export' ? 'export' : 'report';
  }

  let section = $state<Section>(sectionFromUrl());

  let rows = $state<ReportArtifactSummary[]>([]);
  let total = $state(0);
  let userMap = $state<Map<string, User>>(new Map());
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  // Filters. Empty strings mean "no filter" — the qs helper drops them.
  let userId = $state('');
  let format = $state('');
  let search = $state('');
  let from = $state('');
  let to = $state('');

  let limit = 50;
  let offset = $state(0);

  onMount(async () => {
    try {
      const list = await users.list();
      userMap = new Map(list.map((u) => [u.user_id, u]));
    } catch {
      // Non-fatal — server already left-joins so username usually resolves.
    }
    await load();
  });

  async function load() {
    loading = true;
    loadError = null;
    try {
      const res = await adminReports.list({
        user_id: userId || undefined,
        format: format || undefined,
        source: SECTION_SOURCE[section],
        search: search || undefined,
        from: dayStart(from),
        to: dayEnd(to),
        limit,
        offset,
      });
      rows = res.data;
      total = res.total;
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load artifacts");
    } finally {
      loading = false;
    }
  }

  function onSectionChange(v: Section) {
    offset = 0;
    // Keep the section in the URL so shortcuts and shared links land on the
    // right tab; replaceState so tab-flipping doesn't pollute history.
    void goto(v === 'report' ? '/admin/artifacts' : `/admin/artifacts?section=${v}`, {
      replaceState: true, keepFocus: true, noScroll: true,
    });
    void load();
  }

  async function onDownload(row: ReportArtifactSummary) {
    try {
      await downloadReport(`/api/admin/reports/${row.report_artifact_id}/download`, row.filename);
    } catch (e) {
      toast.fromError(e, "Couldn't download artifact");
    }
  }

  async function onDelete(id: string, title: string) {
    if (!(await confirm({
      title: `Delete artifact "${title}"?`,
      message: 'It will be soft-deleted and purged after the retention grace period.',
      tone: 'danger',
      confirmLabel: 'Delete',
    }))) return;
    try {
      await adminReports.delete(id);
      toast.success('Artifact deleted');
      await load();
    } catch (e) {
      toast.fromError(e, "Couldn't delete artifact");
    }
  }

  function dayStart(v: string): string | undefined {
    if (!v) return undefined;
    const d = new Date(v + 'T00:00:00Z');
    return isNaN(d.getTime()) ? undefined : d.toISOString();
  }
  function dayEnd(v: string): string | undefined {
    if (!v) return undefined;
    const d = new Date(v + 'T23:59:59Z');
    return isNaN(d.getTime()) ? undefined : d.toISOString();
  }

  function fmtSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
  }

  function formatTone(format: string): 'accent' | 'success' | 'warning' | 'info' {
    switch (format) {
      case 'pdf': return 'warning';
      case 'xlsx': return 'success';
      case 'csv': return 'info';
      default: return 'accent';
    }
  }

  function resolveUsername(row: ReportArtifactSummary): string {
    if (row.username) return row.username;
    if (row.user_id) {
      const u = userMap.get(row.user_id);
      if (u) return u.username ?? row.user_id.slice(0, 8);
      return row.user_id.slice(0, 8);
    }
    // No acting user. A schedule-triggered run is attributed to the schedule
    // (the entity that fired it) rather than showing an empty cell.
    if (row.run_trigger === 'schedule') return 'Schedule';
    return '—';
  }
</script>

<svelte:head><title>Artifacts · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Artifacts"
    description="Every document generated by the platform. Reports come out of workflow runs; exports are produced without a workflow — by the agent in chat or a direct API call. Opening a document's detail logs a read_prompt audit row."
    breadcrumbs={[{ label: 'Admin', href: '/admin' }, { label: 'Artifacts' }]}
  >
    {#snippet actions()}
      <Button size="sm" variant="ghost" icon={RefreshCw} onclick={() => load()}>
        Refresh
      </Button>
    {/snippet}
  </PageHeader>

  <Tabs
    bind:value={section}
    label="Artifact origin"
    tabs={[
      { value: 'report', label: 'Report' },
      { value: 'export', label: 'Export' },
    ]}
    onChange={onSectionChange}
  />

  <Card>
    <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-3">
      <div class="lg:col-span-2">
        <Input label="Search title" help="artifacts.search_title" bind:value={search} placeholder="inventory…" />
      </div>
      <Select label="Format" help="artifacts.format" bind:value={format}>
        <option value="">Any</option>
        <option value="pdf">PDF</option>
        <option value="xlsx">XLSX</option>
        <option value="csv">CSV</option>
        <option value="html">HTML</option>
      </Select>
      <Select label="User" help="artifacts.user" bind:value={userId}>
        <option value="">Any</option>
        {#each Array.from(userMap.values()) as u}
          <option value={u.user_id}>{u.username ?? u.user_id.slice(0, 8)}</option>
        {/each}
      </Select>
      <div class="grid grid-cols-2 gap-2">
        <Input type="date" label="From" help="audit.date_range" bind:value={from} />
        <Input type="date" label="To" help="audit.date_range" bind:value={to} />
      </div>
    </div>
    <div class="mt-3 flex justify-end">
      <Button size="sm" variant="primary" icon={Search} onclick={() => { offset = 0; load(); }}>
        Apply
      </Button>
    </div>
  </Card>

  {#if loadError}
    <Alert tone="error">{loadError}</Alert>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if rows.length === 0}
    <Card padding="none">
      {#if section === 'report'}
        <EmptyState
          icon={Files}
          title="No reports yet"
          description="When a workflow run generates a document it will land here."
        />
      {:else}
        <EmptyState
          icon={Files}
          title="No exports yet"
          description="When the agent generates a document in chat, or one is requested through the API — outside any workflow — it will land here."
        />
      {/if}
    </Card>
  {:else}
    <DataTable>
      <thead>
        <tr>
          <th>Created</th>
          <th>Title</th>
          <th>Format</th>
          <th>Size</th>
          <th>User</th>
          <th>Origin</th>
          <th class="!text-right">Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each rows as row (row.report_artifact_id)}
          <tr>
            <td class="whitespace-nowrap text-xs text-surface-500">
              {formatDateTime(row.created_at)}
            </td>
            <td>
              <a href="/admin/artifacts/{row.report_artifact_id}" class="font-medium text-surface-900-100 hover:text-primary-300">
                {row.title}
              </a>
              <div class="text-[10px] text-surface-500 mt-0.5 font-mono truncate">{row.filename}</div>
            </td>
            <td>
              <span class="inline-block text-[10px] uppercase tracking-wider px-2 py-0.5 rounded font-semibold
                {formatTone(row.format) === 'warning' ? 'bg-warning-500/15 text-warning-300 ring-1 ring-warning-500/30' :
                 formatTone(row.format) === 'success' ? 'bg-success-500/15 text-success-300 ring-1 ring-success-500/30' :
                 formatTone(row.format) === 'info'    ? 'bg-info-500/15 text-info-300 ring-1 ring-info-500/30' :
                                                        'bg-primary-500/15 text-primary-300 ring-1 ring-primary-500/30'}">
                {row.format}
              </span>
            </td>
            <td class="text-xs tabular-nums text-surface-700-300">{fmtSize(row.size_bytes)}</td>
            <td class="text-xs text-surface-700-300">{resolveUsername(row)}</td>
            <td class="text-xs">
              {#if row.workflow_run_id}
                <a href="/runs/{row.workflow_run_id}/monitor" class="inline-flex items-center gap-1.5 text-surface-700-300 hover:text-primary-300">
                  <Workflow size={12} class="text-info-400" />
                  <span class="font-mono text-[10px]">{row.workflow_run_id.slice(0, 8)}</span>
                </a>
              {:else if row.agent_conversation_id}
                <a href="/ai/chat?conversation={row.agent_conversation_id}" class="inline-flex items-center gap-1.5 text-surface-700-300 hover:text-primary-300">
                  <Bot size={12} class="text-primary-400" />
                  <span>Chat</span>
                  {#if row.agent_prompt_redacted}
                    <AlertTriangle size={12} class="text-warning-400" />
                    <span class="text-warning-300 text-[10px]">redacted</span>
                  {/if}
                </a>
              {:else if row.source === 'api'}
                <span class="inline-flex items-center gap-1.5 text-surface-700-300">
                  <Cable size={12} class="text-warning-400" />
                  <span>API</span>
                </span>
              {:else}
                <span class="text-surface-500 italic">—</span>
              {/if}
            </td>
            <td class="!pr-3">
              <div class="flex items-center justify-end gap-1">
                <button
                  type="button"
                  onclick={() => onDownload(row)}
                  class="p-1.5 rounded hover:bg-surface-200-800/50 text-surface-500 hover:text-primary-300 transition-colors"
                  title="Download"
                  aria-label="Download">
                  <Download size={14} />
                </button>
                <button
                  type="button"
                  onclick={() => onDelete(row.report_artifact_id, row.title)}
                  class="p-1.5 rounded hover:bg-error-500/10 text-surface-500 hover:text-error-300 transition-colors"
                  title="Delete"
                  aria-label="Delete">
                  <Trash2 size={14} />
                </button>
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>

    <div class="flex items-center justify-between text-xs text-surface-500">
      <div>{rows.length} of {total}</div>
      <div class="flex gap-2">
        <Button size="xs" variant="ghost" disabled={offset === 0}
                onclick={() => { offset = Math.max(0, offset - limit); load(); }}>
          Previous
        </Button>
        <Button size="xs" variant="ghost" disabled={offset + rows.length >= total}
                onclick={() => { offset += limit; load(); }}>
          Next
        </Button>
      </div>
    </div>
  {/if}
</div>
