<script lang="ts">
  import { goto } from '$app/navigation';
  import {
    importWizard, errorMessage,
    type AnalysisReport, type ImportDraftSnapshot,
    type ResolvedSnippet, type ResolvedIntegration,
    type ConflictResolution, type MissingSnippet, type MissingIntegration,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, Input, Select, Alert, Spinner, Textarea, Dialog, toast,
    FieldHint,
  } from '$lib/components/ui';
  import {
    Upload, FileText, AlertTriangle, ShieldAlert, Sparkles, GitMerge,
    CheckCircle, XCircle, Loader2, ArrowLeft, Eye, RefreshCw,
  } from 'lucide-svelte';

  // S15: Cross-system workflow import wizard. Four steps:
  //   1. Upload   — pick a file, optionally hint the format.
  //   2. Analyse  — server-side pipeline runs async; we stream progress.
  //   3. Review   — user resolves missing deps + conflicts + duplicates.
  //   4. Commit   — server creates the workflow + needed stubs.

  type Step = 'upload' | 'analyzing' | 'review' | 'committed' | 'failed';
  let step = $state<Step>('upload');

  // Stepper segments shown to the user. The fourth slot swaps from
  // "committed" to a red "failed" label when the analysis blew up so
  // the indicator matches the error card below instead of pretending
  // we're still on track to commit.
  const stepperSteps = $derived(
    step === 'failed'
      ? ['upload', 'analyzing', 'failed']
      : ['upload', 'analyzing', 'review', 'committed'],
  );

  // --- Step 1: upload state
  // Hidden native input + custom trigger — see the markup for why.
  let fileInput = $state<HTMLInputElement | null>(null);
  let fileName = $state('');
  let fileBytes = $state<Uint8Array | null>(null);
  let formatHint = $state<string>('');
  const formatOptions = [
    { value: '', label: 'Auto-detect' },
    { value: 'flow_weaver_v1', label: 'FlowWeaver v1 (our format)' },
    { value: 'n8n', label: 'n8n' },
    { value: 'itential', label: 'Itential IAP / Operations Manager' },
    // "Smart" path: the pipeline first runs every deterministic
    // detector and falls back to the LLM-driven AgentTranslator only
    // when no detector recognises the shape with high confidence.
    // Different from the explicit format choices above, which force
    // their translator regardless of detection.
    { value: 'generic_dag', label: 'Smart routing (let the system pick)' },
  ];

  // --- Step 2/3: draft state
  let importToken = $state<string | null>(null);
  let snapshot = $state<ImportDraftSnapshot | null>(null);
  let streamCloser: { close: () => void } | null = null;
  let progressMessage = $state('');

  // --- Step 3: user choices
  let conflictResolution = $state<ConflictResolution>('fresh_copy');
  let newName = $state('');
  let duplicateAction = $state<'skip' | 'update_existing' | 'import_as_new'>('import_as_new');
  let snippetActions = $state<Record<string, ResolvedSnippet>>({});
  let integrationActions = $state<Record<string, ResolvedIntegration>>({});
  let generatingSnippet = $state<string | null>(null);
  let committing = $state(false);

  // Preview dialog for AI-drafted snippet bodies. The wizard stores the
  // raw payload under snippetActions[id].generated_snippet; opening the
  // dialog binds previewSnippetId so the user can inspect / regenerate /
  // discard before the commit ships the draft to the backend.
  let previewSnippetId = $state<string | null>(null);
  const previewSnippet = $derived(
    previewSnippetId ? snippetActions[previewSnippetId]?.generated_snippet : null,
  );
  // Per-snippet rewrite hints. Persist across opens of the preview
  // dialog so the user can iterate ("nope, make it POST instead", "now
  // hit /v2/devices") without retyping context every round.
  let rewriteHints = $state<Record<string, string>>({});

  // --- Step 4: result
  let committedWorkflowId = $state<string | null>(null);

  async function onFileChosen(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    if (file.size > 5 * 1024 * 1024) {
      toast.error('File too large', { description: 'Max 5 MiB per import.' });
      return;
    }
    fileName = file.name;
    const buf = await file.arrayBuffer();
    fileBytes = new Uint8Array(buf);
  }

  async function startAnalyze() {
    if (!fileBytes) return;
    try {
      const { import_token } = await importWizard.analyze(fileBytes, formatHint || undefined);
      importToken = import_token;
      step = 'analyzing';
      // Subscribe to the SSE stream for live progress.
      streamCloser = importWizard.stream(
        import_token,
        (evt) => {
          snapshot = evt as ImportDraftSnapshot;
          progressMessage = evt.progress ?? '';
          if (evt.status === 'ready') {
            step = 'review';
            initialiseChoices(evt.report!);
          } else if (evt.status === 'failed') {
            step = 'failed';
          }
        },
        async (reason) => {
          // The SSE channel ended. `done` is the expected close (server
          // already pushed ready/failed/committed); `aborted` is us
          // calling .close() during cancel/unmount; `error` means the
          // connection dropped without a terminal status. In that last
          // case do a one-shot GET to recover the current draft state
          // — the analysis itself runs server-side independently of the
          // SSE pipe, so the result is still available.
          if (reason !== 'error') return;
          if (!importToken) return;
          try {
            const snap = await importWizard.status(importToken);
            snapshot = snap;
            progressMessage = snap.progress ?? '';
            if (snap.status === 'ready') {
              step = 'review';
              if (snap.report) initialiseChoices(snap.report);
            } else if (snap.status === 'failed') {
              step = 'failed';
            } else {
              progressMessage = 'Connection to the server was lost. Refresh the page or retry.';
            }
          } catch {
            progressMessage = 'Connection to the server was lost. Refresh the page or retry.';
          }
        },
      );
    } catch (e) {
      toast.fromError(e, 'Analyze failed');
    }
  }

  function initialiseChoices(report: AnalysisReport) {
    // Sensible defaults: stub all snippets, create_needs_config all integrations.
    //
    // Phase 2: if the backend pre-drafted a body (`pregenerated_snippet`
    // is set), pre-select `action: "generated"` with that body so the
    // user lands on "review & accept" instead of "generate from
    // scratch". The same Preview / Regenerate / Discard set of buttons
    // applies; the only difference is the click-zero starting point.
    const sActions: Record<string, ResolvedSnippet> = {};
    for (const m of report.missing_dependencies.snippets) {
      if (m.pregenerated_snippet && typeof m.pregenerated_snippet === 'object') {
        sActions[m.id_in_import] = {
          action: 'generated',
          generated_snippet: m.pregenerated_snippet as Record<string, unknown>,
        };
      } else {
        sActions[m.id_in_import] = { action: 'stub' };
      }
    }
    snippetActions = sActions;

    const iActions: Record<string, ResolvedIntegration> = {};
    for (const m of report.missing_dependencies.integrations) {
      iActions[m.id_in_import] = { action: 'create_needs_config' };
    }
    integrationActions = iActions;

    if (report.conflicts.name_collision) {
      conflictResolution = 'rename';
      newName = guessNewName(report);
    } else {
      conflictResolution = 'fresh_copy';
    }
  }

  function guessNewName(report: AnalysisReport): string {
    const pw = report.proposed_workflow as { name?: string };
    return `${pw.name ?? 'Imported'} (imported)`;
  }

  function onSnippetActionChange(
    id: string,
    action: 'stub' | 'generated' | 'map' | 'skip',
    targetId?: string,
  ) {
    const next = { ...snippetActions };
    // For 'skip' we drop any pregenerated body — the snippet is not
    // going to land, so keeping the AI draft around is just clutter.
    const generated = action === 'skip' ? undefined : snippetActions[id]?.generated_snippet;
    next[id] = { action, target_id: targetId, generated_snippet: generated };
    snippetActions = next;
  }

  function onIntegrationActionChange(id: string, action: 'create_needs_config' | 'map', targetId?: string) {
    integrationActions = {
      ...integrationActions,
      [id]: { action, target_id: targetId },
    };
  }

  async function generateForSnippet(id: string, hint?: string) {
    if (!importToken) return;
    generatingSnippet = id;
    try {
      // Pass the user's rewrite hint through to the backend handler so
      // the LLM can produce a refined draft instead of the same body
      // again. The handler clips the hint to 512 chars defensively.
      const cleanHint = hint?.trim();
      const result = await importWizard.generateSnippet(importToken, id, cleanHint || undefined);
      const generated = result.generated_snippet ?? result.fallback_stub;
      if (!generated) {
        toast.error('Generation failed', { description: result.error ?? 'No content returned.' });
        return;
      }
      snippetActions = {
        ...snippetActions,
        [id]: { action: 'generated', generated_snippet: generated },
      };
      // Open the preview straight away so the user sees what they're
      // about to commit instead of a silent "draft ready" badge.
      previewSnippetId = id;
    } catch (e) {
      toast.fromError(e, 'Generation failed');
    } finally {
      generatingSnippet = null;
    }
  }

  function discardGenerated(id: string) {
    const next = { ...snippetActions };
    next[id] = { action: 'stub', generated_snippet: undefined };
    snippetActions = next;
    previewSnippetId = null;
    toast.info('Discarded draft', { description: 'Will fall back to an empty stub on commit.' });
  }

  function readString(obj: unknown, key: string): string {
    if (obj && typeof obj === 'object' && key in obj) {
      const v = (obj as Record<string, unknown>)[key];
      if (typeof v === 'string') return v;
    }
    return '';
  }

  function readJsonPretty(obj: unknown, key: string): string {
    if (obj && typeof obj === 'object' && key in obj) {
      try { return JSON.stringify((obj as Record<string, unknown>)[key], null, 2); }
      catch { return ''; }
    }
    return '';
  }

  async function commit() {
    if (!importToken || !snapshot?.report) return;
    committing = true;
    try {
      const res = await importWizard.commit(importToken, {
        conflict_resolution: conflictResolution,
        new_name: conflictResolution === 'rename' ? newName : undefined,
        // The server refuses anything else: an import is a create, and qa /
        // production are reached through promotion and its gates.
        target_environment: 'draft',
        duplicate_action: duplicateAction,
        snippets: snippetActions,
        integrations: integrationActions,
      });
      committedWorkflowId = res.workflow_id;
      step = 'committed';
      for (const w of res.warnings) toast.warning('Heads up', { description: w });
    } catch (e) {
      // Surface schema / reference validation details when the backend
      // returns them. Without this, the user just sees "Commit failed"
      // and has no way to know which field broke.
      const body = (e as { body?: { details?: unknown; error?: string } })?.body;
      const details = Array.isArray(body?.details) ? body!.details as string[] : null;
      if (details && details.length > 0) {
        toast.error(body?.error ?? 'Commit failed', {
          description: details.slice(0, 5).join('\n')
            + (details.length > 5 ? `\n…and ${details.length - 5} more` : ''),
        });
      } else {
        toast.fromError(e, 'Commit failed');
      }
    } finally {
      committing = false;
    }
  }

  function cancel() {
    streamCloser?.close();
    streamCloser = null;
    if (importToken) {
      void importWizard.delete(importToken).catch(() => {});
    }
    goto('/workflows');
  }

  // Cleanup on unmount.
  $effect(() => () => {
    streamCloser?.close();
    if (importToken && step !== 'committed') {
      void importWizard.delete(importToken).catch(() => {});
    }
  });
</script>

<svelte:head><title>Import workflow · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-5xl mx-auto space-y-5">
  <PageHeader
    title="Import workflow"
    description="Upload a workflow definition from FlowWeaver v1, n8n, Itential, or any DAG. The agent translates it and surfaces missing dependencies."
  >
    {#snippet actions()}
      <Button variant="ghost" icon={ArrowLeft} href="/workflows">Back</Button>
    {/snippet}
  </PageHeader>

  <!-- Stepper indicator. `stepperSteps` swaps the last segment to a
       red "failed" label when the analysis blew up. -->
  <div class="flex items-center gap-2 text-xs">
    {#each stepperSteps as s, i}
      <span
        class={step === s
          ? (s === 'failed' ? 'font-semibold text-error-400' : 'font-semibold text-primary-300')
          : 'text-surface-500'}
      >
        {i + 1}. {s}
      </span>
      {#if i < stepperSteps.length - 1}<span class="text-surface-600">›</span>{/if}
    {/each}
  </div>

  {#if step === 'upload'}
    <Card>
      <div class="p-5 space-y-4">
        <div>
          <span class="block text-xs font-medium text-surface-700-300 mb-1.5">Workflow file <FieldHint id="workflows.import_file" /></span>
          <!-- The browser renders a native file input's button label and its
               "no file chosen" text in the OS locale — "Examinar…" /
               "Ningún archivo seleccionado" on a Spanish machine — which is
               untranslatable stray text in an English UI. Hide the input and
               drive it from our own Button; the picked file is echoed below.
               Same pattern as /ai/skills and /integrations. -->
          <input
            bind:this={fileInput}
            id="import-file"
            type="file"
            accept=".yaml,.yml,.json,application/json,application/x-yaml,text/yaml"
            onchange={onFileChosen}
            class="sr-only"
            tabindex="-1"
            aria-hidden="true"
          />
          <Button variant="secondary" icon={FileText} onclick={() => fileInput?.click()}>
            {fileName ? 'Choose another file' : 'Choose file'}
          </Button>
          {#if fileName}
            <div class="mt-2 text-xs text-surface-500 inline-flex items-center gap-1.5">
              <FileText size={12} /> {fileName} · {fileBytes?.byteLength ?? 0} bytes
            </div>
          {/if}
        </div>

        <Select label="Format hint (optional)" help="import.format_hint" bind:value={formatHint}>
          {#each formatOptions as opt}
            <option value={opt.value}>{opt.label}</option>
          {/each}
        </Select>

        <div class="text-[11px] text-surface-500">
          Native FlowWeaver YAML / JSON exports round-trip exactly. Foreign formats are translated by the agent and may flag missing snippets / integrations for you to resolve.
        </div>

        <div class="flex gap-2 pt-2">
          <Button variant="primary" icon={Upload} disabled={!fileBytes} onclick={startAnalyze}>Analyze</Button>
          <Button variant="ghost" onclick={cancel}>Cancel</Button>
        </div>
      </div>
    </Card>
  {:else if step === 'analyzing'}
    <Card>
      <div class="p-6 flex flex-col items-center text-center space-y-3">
        <Spinner size="lg" />
        <div class="text-sm font-medium">{progressMessage || 'Starting analysis…'}</div>
        <div class="text-xs text-surface-500">
          The pipeline is detecting the format, translating to v1, resolving dependencies, and checking conflicts.
        </div>
      </div>
    </Card>
  {:else if step === 'failed'}
    <Alert tone="error">
      <div class="space-y-1">
        <div class="font-medium">Import analysis failed</div>
        <div class="text-xs">{snapshot?.error ?? progressMessage ?? 'Unknown error'}</div>
      </div>
    </Alert>
    <div class="flex gap-2">
      <Button variant="ghost" onclick={() => { step = 'upload'; importToken = null; snapshot = null; }}>Try again</Button>
    </div>
  {:else if step === 'review' && snapshot?.report}
    {@const r = snapshot.report}
    <div class="space-y-4">
      <!-- Format detection summary -->
      <Card>
        <div class="p-4 flex items-center justify-between">
          <div>
            <div class="text-xs text-surface-500">Format detected</div>
            <div class="text-sm font-mono">{r.format_detected} <span class="text-surface-500">(confidence {(r.confidence * 100).toFixed(0)}%)</span></div>
          </div>
          {#if r.translation_notes.length > 0}
            <details class="text-xs">
              <summary class="cursor-pointer text-primary-400 hover:underline">Translation notes ({r.translation_notes.length})</summary>
              <ul class="mt-1 list-disc list-inside text-surface-500">
                {#each r.translation_notes as note}<li>{note}</li>{/each}
              </ul>
            </details>
          {/if}
        </div>
      </Card>

      <!-- Structural duplicate -->
      {#if r.conflicts.structural_duplicate}
        {@const sd = r.conflicts.structural_duplicate}
        <Alert tone="warning">
          <div class="flex items-start gap-2">
            <GitMerge size={14} class="mt-0.5 flex-shrink-0" />
            <div class="space-y-2 flex-1">
              <div class="font-medium">Structural duplicate detected: "{sd.matching_workflow_name}"</div>
              <div class="text-xs text-surface-500">{sd.diff_summary} · Match score: {(sd.match_score * 100).toFixed(0)}%</div>
              <Select bind:value={duplicateAction}>
                <option value="import_as_new">Import as new (default)</option>
                <option value="skip">Skip — cancel import</option>
                <option value="update_existing">Update existing workflow</option>
              </Select>
            </div>
          </div>
        </Alert>
      {/if}

      <!-- Name collision -->
      {#if r.conflicts.name_collision}
        <Card>
          <div class="p-4 space-y-3">
            <div class="flex items-center gap-2 text-xs text-warning-400">
              <AlertTriangle size={14} />
              <span class="font-medium">A workflow with the same name exists in {r.conflicts.name_collision.matching_workflow_environment} (v{r.conflicts.name_collision.matching_workflow_version})</span>
            </div>
            <Select label="Resolution" help="import.conflict_resolution" bind:value={conflictResolution}>
              <option value="rename">Rename the import</option>
              <option value="fresh_copy">Import as fresh copy with timestamped name</option>
              <option value="replace">Replace the existing workflow (requires owner grant)</option>
              <option value="keep_existing">Keep existing — cancel this import</option>
            </Select>
            {#if conflictResolution === 'rename'}
              <Input label="New name" help="import.new_name" bind:value={newName} />
            {/if}
          </div>
        </Card>
      {/if}

      <!-- Rollback risk -->
      {#if r.rollback_risk && (r.rollback_risk.non_reversible.length + r.rollback_risk.requires_compensation.length) > 0}
        <Alert tone={r.rollback_risk.non_reversible.length > 0 ? 'error' : 'warning'}>
          <div class="flex items-start gap-2">
            <ShieldAlert size={14} class="mt-0.5 flex-shrink-0" />
            <div class="space-y-1 text-xs">
              {#if r.rollback_risk.non_reversible.length > 0}
                <div class="font-medium">{r.rollback_risk.non_reversible.length} non-reversible step(s)</div>
                <ul class="list-disc list-inside font-mono">
                  {#each r.rollback_risk.non_reversible as it}
                    <li>{it.snippet_name} <span class="text-surface-500">({it.snippet_type})</span></li>
                  {/each}
                </ul>
              {/if}
              {#if r.rollback_risk.requires_compensation.length > 0}
                <div class="font-medium">{r.rollback_risk.requires_compensation.length} step(s) need a compensation edge</div>
              {/if}
            </div>
          </div>
        </Alert>
      {/if}

      <!-- Missing snippets -->
      {#if r.missing_dependencies.snippets.length > 0}
        <Card>
          <div class="p-4 space-y-3">
            <div class="text-sm font-semibold">Missing snippets ({r.missing_dependencies.snippets.length})</div>
            {#each r.missing_dependencies.snippets as m (m.id_in_import)}
              {@const isSkipped = snippetActions[m.id_in_import]?.action === 'skip'}
              <div class="border {isSkipped ? 'border-error-500/40 bg-error-500/5 opacity-75' : (m.pregenerated_snippet ? 'border-success-500/40 bg-success-500/5' : 'border-surface-300-700')} rounded p-3 space-y-2">
                <div class="flex items-center justify-between">
                  <div>
                    <div class="font-mono text-sm {isSkipped ? 'line-through text-surface-500' : ''}">{m.id_in_import}</div>
                    <div class="text-[11px] text-surface-500">Inferred type: {m.inferred_type}</div>
                  </div>
                  {#if isSkipped}
                    <span class="inline-flex items-center gap-1 text-[11px] text-error-300 bg-error-500/15 rounded px-1.5 py-0.5">
                      <XCircle size={11} /> Skipped
                    </span>
                  {:else if m.pregenerated_snippet}
                    <span class="inline-flex items-center gap-1 text-[11px] text-success-300 bg-success-500/15 rounded px-1.5 py-0.5">
                      <Sparkles size={11} /> AI-drafted
                    </span>
                  {/if}
                </div>
                <Select
                  label="Action"
                  help="import.snippet_action"
                  value={snippetActions[m.id_in_import]?.action ?? 'stub'}
                  onchange={(e: Event) => onSnippetActionChange(m.id_in_import, (e.target as HTMLSelectElement).value as any)}
                >
                  <option value="stub">Create empty stub (default)</option>
                  <option value="generated">Generate with AI</option>
                  {#if m.candidates_for_mapping.length > 0}
                    <option value="map">Map to existing snippet</option>
                  {/if}
                  <option value="skip">Skip — drop nodes that use it</option>
                </Select>
                {#if isSkipped}
                  <div class="text-[11px] text-error-300/90 bg-error-500/10 rounded px-2 py-1.5">
                    Nodes referencing <code class="font-mono">{m.id_in_import}</code> will be removed from the
                    imported workflow on commit, along with their incident edges.
                  </div>
                {/if}
                {#if snippetActions[m.id_in_import]?.action === 'map'}
                  {@const selectedTargetId = snippetActions[m.id_in_import]?.target_id ?? ''}
                  {@const selectedCandidate = m.candidates_for_mapping.find((c) => c.id === selectedTargetId)}
                  <Select
                    label={m.inferred_type === 'integration_action' ? 'Target (snippet or integration)' : 'Target snippet'}
                    help="import.dependency_target"
                    value={selectedTargetId}
                    onchange={(e: Event) => onSnippetActionChange(m.id_in_import, 'map', (e.target as HTMLSelectElement).value)}
                  >
                    <option value="">— choose —</option>
                    {#each m.candidates_for_mapping as c}
                      <option value={c.id}>
                        {c.kind === 'integration' ? '🔌 ' : ''}{c.name} ({c.type}) — similarity {(c.similarity_score * 100).toFixed(0)}%
                      </option>
                    {/each}
                  </Select>
                  {#if selectedCandidate?.kind === 'integration'}
                    <div class="text-[11px] text-primary-300/90 bg-primary-500/10 rounded px-2 py-1.5">
                      Will attach an action named <code class="font-mono">{m.id_in_import}</code> under
                      integration <strong>{selectedCandidate.name}</strong> on commit.
                      Existing actions under that integration with the same name are reused.
                    </div>
                  {/if}
                {:else if snippetActions[m.id_in_import]?.action === 'generated'}
                  <div class="flex items-center gap-2">
                    <Button
                      size="sm"
                      variant="secondary"
                      icon={Sparkles}
                      loading={generatingSnippet === m.id_in_import}
                      onclick={() => generateForSnippet(m.id_in_import)}
                    >Draft body with AI</Button>
                    {#if snippetActions[m.id_in_import]?.generated_snippet}
                      <span class="text-xs text-success-400 inline-flex items-center gap-1"><CheckCircle size={12} /> draft ready</span>
                      <Button
                        size="sm"
                        variant="ghost"
                        icon={Eye}
                        onclick={() => (previewSnippetId = m.id_in_import)}
                      >Preview</Button>
                      <Button
                        size="sm"
                        variant="ghost"
                        icon={RefreshCw}
                        loading={generatingSnippet === m.id_in_import}
                        onclick={() => generateForSnippet(m.id_in_import)}
                      >Regenerate</Button>
                    {/if}
                  </div>
                {/if}
              </div>
            {/each}
          </div>
        </Card>
      {/if}

      <!-- Missing integrations -->
      {#if r.missing_dependencies.integrations.length > 0}
        <Card>
          <div class="p-4 space-y-3">
            <div class="text-sm font-semibold">Missing integrations ({r.missing_dependencies.integrations.length})</div>
            <Alert tone="info">
              <div class="text-xs">
                Auto-created integrations land in <code>needs_config</code> status. Runs that reference them will refuse to start until an admin adds credentials.
              </div>
            </Alert>
            {#each r.missing_dependencies.integrations as m (m.id_in_import)}
              <div class="border border-surface-300-700 rounded p-3 space-y-2">
                <div>
                  <div class="font-mono text-sm">{m.id_in_import}</div>
                  {#if m.inferred_base_url}<div class="text-[11px] text-surface-500">Inferred base URL: {m.inferred_base_url}</div>{/if}
                </div>
                <Select
                  label="Action"
                  help="import.integration_action"
                  value={integrationActions[m.id_in_import]?.action ?? 'create_needs_config'}
                  onchange={(e: Event) => onIntegrationActionChange(m.id_in_import, (e.target as HTMLSelectElement).value as any)}
                >
                  <option value="create_needs_config">Create with needs_config status</option>
                  {#if m.candidates_for_mapping.length > 0}
                    <option value="map">Map to existing integration</option>
                  {/if}
                </Select>
                {#if integrationActions[m.id_in_import]?.action === 'map'}
                  <Select
                    label="Target integration"
                    help="import.target_integration"
                    value={integrationActions[m.id_in_import]?.target_id ?? ''}
                    onchange={(e: Event) => onIntegrationActionChange(m.id_in_import, 'map', (e.target as HTMLSelectElement).value)}
                  >
                    <option value="">— choose —</option>
                    {#each m.candidates_for_mapping as c}
                      <option value={c.id}>{c.name} ({c.type}) — similarity {(c.similarity_score * 100).toFixed(0)}%</option>
                    {/each}
                  </Select>
                {/if}
              </div>
            {/each}
          </div>
        </Card>
      {/if}

      <!-- Target environment -->
      <Card>
        <div class="p-4 space-y-3">
          <p class="text-sm text-surface-700-300">
            The imported workflow lands in <strong>Draft</strong>. Promote it to QA and
            production afterwards, through the usual gates.
          </p>
        </div>
      </Card>

      <!-- Commit -->
      <div class="flex gap-2">
        <Button
          variant="primary"
          icon={Upload}
          loading={committing}
          onclick={commit}
        >Commit import</Button>
        <Button variant="ghost" onclick={cancel}>Cancel</Button>
      </div>
    </div>
  {:else if step === 'committed'}
    <Card>
      <div class="p-6 space-y-3 text-center">
        <CheckCircle size={32} class="text-success-400 mx-auto" />
        <div class="text-sm font-medium">Workflow imported</div>
        {#if committedWorkflowId}
          <Button variant="primary" href={`/workflows/${committedWorkflowId}`}>Open workflow</Button>
        {/if}
      </div>
    </Card>
  {/if}
</div>

<!--
  AI-drafted snippet preview. Mounts at the bottom so it overlays every
  step of the wizard. The dialog reads from snippetActions so we don't
  duplicate state; "Discard" rolls the action back to "stub" so commit
  still has a valid resolution if the user opted out.
-->
<Dialog
  open={previewSnippetId !== null}
  title="AI-drafted snippet"
  description="Review the body the agent produced before committing. Regenerate if it misses the mark."
  size="xl"
  onClose={() => (previewSnippetId = null)}
>
  {#if previewSnippet}
    {@const name = readString(previewSnippet, 'name')}
    {@const type = readString(previewSnippet, 'type')}
    {@const description = readString(previewSnippet, 'description')}
    {@const scriptLanguage = readString(previewSnippet, 'script_language')}
    {@const targetMode = readString(previewSnippet, 'target_mode')}
    {@const code = readString(previewSnippet, 'code')}
    {@const inputSchema = readJsonPretty(previewSnippet, 'input_schema')}
    {@const outputSchema = readJsonPretty(previewSnippet, 'output_schema')}
    <div class="space-y-4 text-sm">
      <div class="grid grid-cols-2 gap-3">
        <div>
          <div class="text-[11px] uppercase tracking-[0.08em] text-surface-500">Name</div>
          <div class="font-mono text-sm">{name || '—'}</div>
        </div>
        <div>
          <div class="text-[11px] uppercase tracking-[0.08em] text-surface-500">Type</div>
          <div class="font-mono text-sm">{type || '—'}</div>
        </div>
        <div>
          <div class="text-[11px] uppercase tracking-[0.08em] text-surface-500">Script language</div>
          <div class="font-mono text-sm">{scriptLanguage || '—'}</div>
        </div>
        <div>
          <div class="text-[11px] uppercase tracking-[0.08em] text-surface-500">Target mode</div>
          <div class="font-mono text-sm">{targetMode || '—'}</div>
        </div>
      </div>

      {#if description}
        <div>
          <div class="text-[11px] uppercase tracking-[0.08em] text-surface-500 mb-1">Description</div>
          <p class="text-surface-700-300">{description}</p>
        </div>
      {/if}

      <div>
        <div class="text-[11px] uppercase tracking-[0.08em] text-surface-500 mb-1">Code</div>
        <pre class="bg-surface-100-900/60 ring-1 ring-surface-200-800/80 rounded p-3 text-xs font-mono whitespace-pre-wrap max-h-72 overflow-auto">{code || '(empty)'}</pre>
      </div>

      <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
        <div>
          <div class="text-[11px] uppercase tracking-[0.08em] text-surface-500 mb-1">Input schema</div>
          <pre class="bg-surface-100-900/60 ring-1 ring-surface-200-800/80 rounded p-3 text-[11px] font-mono whitespace-pre-wrap max-h-56 overflow-auto">{inputSchema || '{}'}</pre>
        </div>
        <div>
          <div class="text-[11px] uppercase tracking-[0.08em] text-surface-500 mb-1">Output schema</div>
          <pre class="bg-surface-100-900/60 ring-1 ring-surface-200-800/80 rounded p-3 text-[11px] font-mono whitespace-pre-wrap max-h-56 overflow-auto">{outputSchema || '{}'}</pre>
        </div>
      </div>

      <Alert tone="info">
        <div class="text-xs">
          You can edit this snippet after commit from <code>/snippets</code>. Mark it
          <strong>verified</strong> once you've reviewed the body — until then it's
          flagged as AI-drafted in the listing.
        </div>
      </Alert>

      <!-- Rewrite-with-hint: lets the user nudge the agent toward a
           different body without leaving the preview. The hint is
           persisted in `rewriteHints` so iterating is cheap — type,
           rewrite, inspect, refine the hint, rewrite again. -->
      <div class="space-y-2 pt-1">
        <Textarea
          label="Tell the agent what to change (optional)"
          help="import.agent_instructions"
          placeholder="e.g. use POST instead of GET; hit /v2/devices and require a `device_id` input"
          rows={3}
          value={previewSnippetId ? (rewriteHints[previewSnippetId] ?? '') : ''}
          oninput={(e: Event) => {
            if (!previewSnippetId) return;
            rewriteHints = {
              ...rewriteHints,
              [previewSnippetId]: (e.target as HTMLTextAreaElement).value,
            };
          }}
        />
        <div class="text-[11px] text-surface-500">
          Empty hint = regenerate with the same instructions. With a hint, the agent
          rewrites the draft using your guidance.
        </div>
      </div>
    </div>
  {/if}
  {#snippet footer()}
    {#if previewSnippetId}
      {@const hint = rewriteHints[previewSnippetId]?.trim() ?? ''}
      <Button
        variant="ghost"
        icon={XCircle}
        onclick={() => discardGenerated(previewSnippetId!)}
      >Discard draft</Button>
      <Button
        variant="secondary"
        icon={RefreshCw}
        loading={generatingSnippet === previewSnippetId}
        onclick={() => generateForSnippet(previewSnippetId!, hint || undefined)}
      >{hint.length > 0 ? 'Rewrite with hint' : 'Regenerate'}</Button>
      <Button variant="primary" icon={CheckCircle} onclick={() => (previewSnippetId = null)}>
        Looks good
      </Button>
    {/if}
  {/snippet}
</Dialog>
