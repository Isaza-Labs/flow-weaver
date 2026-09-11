<script lang="ts">
  // Groups the tool calls that accompany an assistant message into one
  // collapsible block. While the stream still has running calls the block
  // auto-expands so the user can see progress; once every call is done it
  // collapses to a single summary row to keep the transcript clean.
  //
  // Tool names are humanized via `TOOL_LABELS` — unknown names fall back
  // to the raw snake_case so new tools show up without breaking the UI.

  import { ChevronDown, Wrench, CheckCircle2, XCircle, Loader2, Download, FileText } from 'lucide-svelte';
  import type { ToolAttachment } from '$lib/api/ai-stream';
  import { downloadReport } from '$lib/api/client';
  import { toast } from '$lib/components/ui';

  type Status = 'running' | 'ok' | 'failed';

  interface ToolCall {
    name: string;
    status: Status;
    args_preview?: unknown;
    preview?: unknown;
    attachment?: ToolAttachment;
  }

  let { calls }: { calls: ToolCall[] } = $props();

  // Surfaces the generate_report attachment as a prominent download tile
  // ABOVE the tool-call summary. Without this the bytes are buried inside
  // the truncated preview and the user sees "Ran 1 tool" with no obvious
  // way to grab the file.
  const attachments = $derived(
    calls
      .filter((c) => c.attachment && c.status === 'ok')
      .map((c) => c.attachment as ToolAttachment),
  );

  function fmtSize(bytes?: number): string {
    if (bytes == null) return '';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
  }

  async function handleDownload(att: ToolAttachment) {
    try {
      await downloadReport(att.download_url, att.filename);
    } catch (e) {
      toast.fromError(e, "Couldn't download report");
    }
  }

  // Snake_case → human label. Kept compact on purpose — anything missing
  // still renders via the fallback so the chat UI never hides a tool.
  const TOOL_LABELS: Record<string, string> = {
    list_apis: 'Listed APIs',
    discover_operations: 'Searched API operations',
    operation_detail: 'Loaded operation details',
    execute_operation: 'Called the API',
    load_skill: 'Loaded an integration skill',

    query_devices: 'Searched devices',
    get_device: 'Loaded device',
    list_device_pools: 'Listed device pools',
    list_credentials: 'Listed credentials',

    list_workflows: 'Listed workflows',
    get_workflow: 'Loaded workflow',
    create_workflow: 'Created workflow',
    update_workflow: 'Updated workflow',
    clone_workflow: 'Cloned workflow',
    promote_workflow: 'Promoted workflow',
    delete_workflow: 'Deleted workflow',
    diff_workflow: 'Compared workflow versions',
    run_workflow: 'Ran workflow',
    verify_workflow: 'Verified workflow',
    get_workflow_versions: 'Loaded workflow versions',

    list_services: 'Listed services',
    get_service: 'Loaded service',
    create_service: 'Created service',
    update_service: 'Updated service',
    delete_service: 'Deleted service',

    list_runs: 'Listed runs',
    get_run: 'Loaded run',
    get_run_steps: 'Loaded run steps',

    list_triggers: 'Listed triggers',
    get_trigger: 'Loaded trigger',

    list_integrations: 'Listed integrations',
    get_integration: 'Loaded integration',
    integration_execute: 'Called integration',

    create_workflow_plan: 'Drafted a plan',
    update_workflow_plan: 'Updated the plan',
    submit_plan_for_approval: 'Submitted plan for approval',
    build_plan: 'Built plan',
  };

  function labelFor(name: string): string {
    return TOOL_LABELS[name] ?? name.replace(/_/g, ' ');
  }

  const total = $derived(calls.length);
  const running = $derived(calls.some((c) => c.status === 'running'));
  const failed = $derived(calls.some((c) => c.status === 'failed'));
  const allDone = $derived(!running && total > 0);

  // Default: open while something is still running, closed once everything
  // finished. User can toggle manually; we track whether they overrode us
  // so their choice sticks across subsequent status updates.
  let userOverride = $state<boolean | null>(null);
  const open = $derived(userOverride ?? running);

  function toggle() {
    userOverride = !open;
  }

  function summaryText(): string {
    if (running) {
      const last = [...calls].reverse().find((c) => c.status === 'running');
      return last ? `Running ${labelFor(last.name)}…` : 'Working…';
    }
    if (failed) {
      const bad = calls.filter((c) => c.status === 'failed').length;
      return `Ran ${total} ${total === 1 ? 'tool' : 'tools'} · ${bad} failed`;
    }
    return `Ran ${total} ${total === 1 ? 'tool' : 'tools'}`;
  }
</script>

{#each attachments as att}
  <button
    type="button"
    onclick={() => handleDownload(att)}
    class="w-full flex items-center gap-3 p-3 rounded-lg border border-primary-500/30 bg-primary-500/5 hover:bg-primary-500/10 transition-colors mb-2 text-left"
  >
    <div class="w-9 h-9 rounded-md bg-primary-500/15 text-primary-300 flex items-center justify-center shrink-0 ring-1 ring-primary-500/30">
      <FileText size={16} />
    </div>
    <div class="flex-1 min-w-0">
      <div class="text-sm font-medium text-surface-900-100 truncate">{att.filename}</div>
      <div class="text-[11px] text-surface-500 mt-0.5 flex items-center gap-2">
        {#if att.format}<span class="uppercase font-semibold tracking-wider">{att.format}</span>{/if}
        {#if att.size_bytes != null}<span>·</span><span class="tabular-nums">{fmtSize(att.size_bytes)}</span>{/if}
      </div>
    </div>
    <Download size={14} class="text-primary-300 shrink-0" />
  </button>
{/each}

{#if total > 0}
  <div class="rounded-lg border border-surface-200-800/80 bg-surface-100-900/40 overflow-hidden">
    <button
      type="button"
      onclick={toggle}
      class="w-full flex items-center gap-2 px-3 py-2 text-xs text-surface-700-300 hover:bg-surface-200-800/30 transition-colors"
    >
      {#if running}
        <Loader2 size={12} class="text-primary-300 animate-spin shrink-0" />
      {:else if failed}
        <XCircle size={12} class="text-error-300 shrink-0" />
      {:else}
        <CheckCircle2 size={12} class="text-success-300 shrink-0" />
      {/if}
      <Wrench size={11} class="text-surface-500 shrink-0" />
      <span class="flex-1 text-left truncate">{summaryText()}</span>
      {#if allDone}
        <span class="text-[10px] text-surface-500 tabular-nums">{total}</span>
      {/if}
      <ChevronDown
        size={12}
        class="text-surface-500 shrink-0 transition-transform duration-150 {open ? 'rotate-180' : ''}"
      />
    </button>

    {#if open}
      <ul class="border-t border-surface-200-800/80 divide-y divide-surface-200-800/50">
        {#each calls as tc, i (i)}
          <li class="flex items-center gap-2 px-3 py-1.5 text-xs">
            {#if tc.status === 'running'}
              <Loader2 size={11} class="text-primary-300 animate-spin shrink-0" />
            {:else if tc.status === 'ok'}
              <CheckCircle2 size={11} class="text-success-300 shrink-0" />
            {:else}
              <XCircle size={11} class="text-error-300 shrink-0" />
            {/if}
            <span class="flex-1 text-surface-800-200 truncate">{labelFor(tc.name)}</span>
            <code class="text-[10px] text-surface-500 font-mono truncate max-w-[240px]" title={tc.name}>
              {tc.name}
            </code>
          </li>
        {/each}
      </ul>
    {/if}
  </div>
{/if}
