<script lang="ts">
  // Detail view for one persisted artifact (moved from /admin/reports/{id}
  // when the reports view was folded into /admin/artifacts). Calling this
  // endpoint is audited server-side (the backend emits a `report.read_prompt`
  // audit row every time the prompt is decrypted), so don't expose it by link
  // from public-ish places.
  //
  // Preview strategy:
  //   html → iframe srcdoc with decoded HTML
  //   pdf  → <embed> inline
  //   csv  → first 20 rows parsed into a table
  //   xlsx → link only (browser-side parsing would bring SheetJS; not worth it)

  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { copyText } from '$lib/utils/clipboard';
  import {
    adminReports, downloadReport,
    type ReportArtifactDetail,
    errorMessage,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Alert, Spinner, formatDateTime, toast,
  } from '$lib/components/ui';
  import {
    Download, ArrowLeft, AlertTriangle, FileText,
    MessageCircle, Copy,
  } from 'lucide-svelte';

  let row = $state<ReportArtifactDetail | null>(null);
  let loading = $state(true);
  let loadError = $state<string | null>(null);

  onMount(load);

  async function load() {
    loading = true;
    loadError = null;
    try {
      row = await adminReports.get(page.params.id!);
    } catch (e) {
      loadError = errorMessage(e);
      toast.fromError(e, "Couldn't load artifact");
    } finally {
      loading = false;
    }
  }

  async function handleDownload() {
    if (!row) return;
    try {
      await downloadReport(`/api/admin/reports/${row.report_artifact_id}/download`, row.filename);
    } catch (e) {
      toast.fromError(e, "Couldn't download artifact");
    }
  }

  function fmtSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
  }

  function decodeBase64ToUtf8(b64: string): string {
    try {
      // atob gives us latin-1; round-trip through TextDecoder for proper UTF-8.
      const bin = atob(b64);
      const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      return new TextDecoder('utf-8').decode(bytes);
    } catch {
      return '';
    }
  }

  function parseCsvPreview(text: string, maxRows = 20): string[][] {
    const out: string[][] = [];
    const lines = text.split(/\r?\n/).filter((l) => l.length > 0);
    for (const line of lines.slice(0, maxRows)) {
      // Minimal RFC-4180-ish parse — good enough for preview. Full parse
      // would need state machine for embedded quotes + newlines.
      const cells: string[] = [];
      let cur = '';
      let inQuotes = false;
      for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (c === '"') {
          if (inQuotes && line[i + 1] === '"') { cur += '"'; i++; }
          else { inQuotes = !inQuotes; }
        } else if (c === ',' && !inQuotes) {
          cells.push(cur); cur = '';
        } else {
          cur += c;
        }
      }
      cells.push(cur);
      out.push(cells);
    }
    return out;
  }

  async function copyPrompt() {
    if (!row?.agent_prompt) return;
    if (await copyText(row.agent_prompt)) toast.success('Prompt copied');
    else toast.info('Copy failed');
  }
</script>

<svelte:head><title>Artifact · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title={row?.title ?? 'Artifact'}
    breadcrumbs={[
      { label: 'Admin', href: '/admin' },
      { label: 'Artifacts', href: '/admin/artifacts' },
      { label: row?.title ?? '…' },
    ]}
  >
    {#snippet actions()}
      {#if row}
        <button
          type="button"
          onclick={handleDownload}
          class="inline-flex items-center gap-1.5 px-3 h-8 rounded-md text-sm font-medium bg-primary-500/15 text-primary-300 ring-1 ring-primary-500/30 hover:bg-primary-500/25 transition-colors"
        >
          <Download size={14} />
          Download
        </button>
      {/if}
    {/snippet}
  </PageHeader>

  {#if loadError}
    <Alert tone="error">{loadError}</Alert>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if row}
    <!-- Metadata panel -->
    <Card>
      <div class="grid grid-cols-2 md:grid-cols-4 gap-x-6 gap-y-3 text-xs">
        <div>
          <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Filename</div>
          <div class="font-mono text-surface-900-100 break-all">{row.filename}</div>
        </div>
        <div>
          <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Format</div>
          <div class="text-surface-900-100">{row.format}</div>
        </div>
        <div>
          <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Size</div>
          <div class="text-surface-900-100 tabular-nums">{fmtSize(row.size_bytes)}</div>
        </div>
        <div>
          <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Source</div>
          <div class="text-surface-900-100">{row.source}</div>
        </div>
        <div>
          <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">User</div>
          <div class="text-surface-900-100">
            {#if row.username || row.user_id}
              {row.username ?? row.user_id?.slice(0, 8)}
            {:else if row.run_trigger === 'schedule'}
              Schedule
            {:else}
              —
            {/if}
          </div>
        </div>
        <div>
          <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Created</div>
          <div class="text-surface-900-100">{formatDateTime(row.created_at)}</div>
        </div>
        <div>
          <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Expires</div>
          <div class="text-surface-900-100">{formatDateTime(row.expires_at)}</div>
        </div>
        <div class="col-span-2">
          <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">SHA-256</div>
          <div class="font-mono text-[10px] text-surface-700-300 break-all">{row.sha256}</div>
        </div>
      </div>
    </Card>

    <!-- Origin (agent or workflow) -->
    {#if row.source === 'agent' || row.source === 'workflow'}
      <Card>
        <div class="flex items-center gap-2 mb-3">
          <MessageCircle size={14} class="text-primary-400" />
          <div class="text-sm font-semibold text-surface-900-100">Origin</div>
        </div>

        {#if row.source === 'agent'}
          <div class="grid grid-cols-2 md:grid-cols-3 gap-x-6 gap-y-2 text-xs mb-3">
            {#if row.agent_name}
              <div>
                <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Agent</div>
                <div class="text-surface-900-100">{row.agent_name}</div>
              </div>
            {/if}
            {#if row.agent_conversation_id}
              <div>
                <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Conversation</div>
                <a href="/ai/chat?conversation={row.agent_conversation_id}"
                   class="font-mono text-[10px] text-primary-300 hover:underline break-all">
                  {row.agent_conversation_id}
                </a>
              </div>
            {/if}
            {#if row.agent_run_id}
              <div>
                <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Agent run</div>
                <div class="font-mono text-[10px] text-surface-700-300 break-all">{row.agent_run_id}</div>
              </div>
            {/if}
          </div>

          {#if row.agent_prompt}
            <div>
              <div class="flex items-center justify-between mb-1.5">
                <div class="flex items-center gap-2">
                  <div class="text-[10px] uppercase tracking-wider text-surface-500">Triggering prompt</div>
                  {#if row.agent_prompt_redacted}
                    <span class="inline-flex items-center gap-1 text-[10px] text-warning-300 bg-warning-500/10 px-1.5 py-0.5 rounded">
                      <AlertTriangle size={10} /> redacted
                    </span>
                  {/if}
                </div>
                <button type="button" onclick={copyPrompt}
                        class="text-[10px] text-surface-500 hover:text-primary-300 inline-flex items-center gap-1">
                  <Copy size={10} /> Copy
                </button>
              </div>
              <pre class="bg-surface-50-950 border border-surface-300-700 rounded-md p-3 text-xs text-surface-900-100 whitespace-pre-wrap break-words max-h-64 overflow-auto">{row.agent_prompt}</pre>
            </div>
          {:else}
            <p class="text-xs text-surface-500 italic">No prompt captured for this artifact.</p>
          {/if}
        {:else if row.source === 'workflow'}
          <div class="grid grid-cols-2 md:grid-cols-3 gap-x-6 gap-y-2 text-xs">
            {#if row.workflow_run_id}
              <div>
                <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Run</div>
                <a href="/runs/{row.workflow_run_id}/monitor"
                   class="font-mono text-[10px] text-primary-300 hover:underline break-all">
                  {row.workflow_run_id}
                </a>
              </div>
            {/if}
            {#if row.workflow_id}
              <div>
                <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Workflow</div>
                <a href="/workflows/{row.workflow_id}"
                   class="font-mono text-[10px] text-primary-300 hover:underline break-all">
                  {row.workflow_id}
                </a>
              </div>
            {/if}
            {#if row.run_trigger}
              <div>
                <div class="text-surface-500 uppercase tracking-wider text-[10px] mb-0.5">Triggered by</div>
                <div class="text-surface-900-100">{row.run_trigger}</div>
              </div>
            {/if}
          </div>
        {/if}
      </Card>
    {/if}

    <!-- Preview -->
    <Card>
      <div class="flex items-center gap-2 mb-3">
        <FileText size={14} class="text-primary-400" />
        <div class="text-sm font-semibold text-surface-900-100">Preview</div>
      </div>

      {#if row.format === 'html'}
        {@const html = decodeBase64ToUtf8(row.base64)}
        <iframe
          title="Artifact preview"
          srcdoc={html}
          class="w-full h-[70vh] bg-white rounded border border-surface-300-700"
          sandbox="allow-same-origin"
        ></iframe>
      {:else if row.format === 'pdf'}
        <embed
          src={'data:application/pdf;base64,' + row.base64}
          type="application/pdf"
          class="w-full h-[70vh] rounded border border-surface-300-700"
        />
      {:else if row.format === 'csv'}
        {@const text = decodeBase64ToUtf8(row.base64)}
        {@const rows = parseCsvPreview(text, 20)}
        {#if rows.length > 0}
          <div class="overflow-auto max-h-[60vh]">
            <table class="w-full text-xs">
              <tbody>
                {#each rows as r, i}
                  <tr class={i === 0 ? 'font-semibold bg-surface-100-900' : ''}>
                    {#each r as c}
                      <td class="border border-surface-200-800 px-2 py-1 align-top text-surface-900-100">{c}</td>
                    {/each}
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="text-[10px] text-surface-500 mt-2">Showing first 20 rows. Download for the full file.</p>
        {:else}
          <p class="text-xs text-surface-500 italic">Empty CSV.</p>
        {/if}
      {:else}
        <p class="text-xs text-surface-500">
          XLSX preview isn't rendered in-browser. Click <b>Download</b> to open it in your spreadsheet tool.
        </p>
      {/if}
    </Card>

    <div>
      <Button variant="ghost" href="/admin/artifacts" icon={ArrowLeft}>Back to artifacts</Button>
    </div>
  {/if}
</div>
