<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { snippets, errorMessage, type Snippet } from '$lib/api/client';
  import { authStore } from '$lib/stores/auth.svelte';
  import TransformPlayground from '$lib/components/TransformPlayground.svelte';
  import SnippetTestDialog from '$lib/components/SnippetTestDialog.svelte';
  import {
    PageHeader, Card, Button, Badge, Alert, Spinner, Input, Textarea, Select, MermaidDiagram, FieldHint,
  } from '$lib/components/ui';
  import { Play, Save, ArrowLeft, Code2, FileJson, Repeat } from 'lucide-svelte';

  let snippet = $state<Snippet | null>(null);
  let loading = $state(true);
  let error = $state('');
  let saveStatus = $state<'idle' | 'saving' | 'saved' | 'error'>('idle');
  let saveError = $state('');
  let showTestDialog = $state(false);

  let name = $state('');
  let description = $state('');
  let code = $state('');
  let scriptLanguage = $state('python');
  let targetMode = $state('per_device');
  let maxParallel = $state(10);
  let timeoutSeconds = $state(300);
  let inputSchemaText = $state('{}');
  let outputSchemaText = $state('{}');
  let retryPolicyText = $state('{}');
  let logicDiagramMermaid = $state('');
  // S13.6 follow-up: rollback policy override. Empty string = inherit
  // the handler default; the analyzer floors it to the handler's
  // DefaultIdempotency when the handler is stricter.
  let idempotency = $state<'' | 'idempotent' | 'requires_compensation' | 'non_reversible'>('');
  // python_snippet only: lifts the sandbox network isolation for interactive
  // SSH (netmiko/paramiko). Admin-gated by the backend.
  let networkEnabled = $state(false);

  // REST
  let restUrl = $state('');
  let restMethod = $state('GET');
  let restHeaders = $state('{}');
  let restBody = $state('');

  // Ping
  let pingCount = $state(4);
  let pingTimeout = $state(5);

  // Transform
  let transformExpression = $state('');
  let transformInput = $state('{}');

  const isCodeType = $derived(snippet?.type === 'python_snippet' || snippet?.type === 'ansible_playbook');
  const isTransformType = $derived(snippet?.type === 'transform' || snippet?.type === 'jmespath');
  const isIntegrationType = $derived(snippet?.type === 'integration_action');
  const isRestType = $derived(snippet?.type === 'rest_call');
  const isPingType = $derived(snippet?.type === 'ping');
  const isAdmin = $derived(authStore.session?.role === 'admin');

  onMount(loadSnippet);

  async function loadSnippet() {
    loading = true;
    error = '';
    try {
      const snip = await snippets.get(page.params.id!);
      snippet = snip;
      name = snip.name;
      description = snip.description || '';
      code = snip.code || '';
      scriptLanguage = snip.script_language || 'python';
      targetMode = snip.target_mode || 'per_device';
      maxParallel = snip.max_parallel ?? 10;
      timeoutSeconds = snip.timeout_seconds ?? 300;
      inputSchemaText = JSON.stringify(snip.input_schema || {}, null, 2);
      outputSchemaText = JSON.stringify(snip.output_schema || {}, null, 2);
      retryPolicyText = JSON.stringify(snip.retry_policy || {}, null, 2);
      logicDiagramMermaid = snip.logic_diagram_mermaid ?? '';
      idempotency = (snip.idempotency ?? '') as typeof idempotency;
      networkEnabled = snip.network_enabled ?? false;

      if (snip.type === 'rest_call' && snip.code) {
        try {
          const parsed = JSON.parse(snip.code);
          restUrl = parsed.url || '';
          restMethod = parsed.method || 'GET';
          restHeaders = JSON.stringify(parsed.headers || {}, null, 2);
          restBody = parsed.body || '';
        } catch {
          // Code field is not JSON — keep raw, the next save will normalize.
        }
      }
      if (snip.type === 'ping' && snip.code) {
        try {
          const parsed = JSON.parse(snip.code);
          pingCount = parsed.count || 4;
          pingTimeout = parsed.timeout || 5;
        } catch {
          // Keep defaults if not parseable.
        }
      }
      if ((snip.type === 'transform' || snip.type === 'jmespath') && snip.code) {
        transformExpression = snip.code;
      }
    } catch (e) {
      error = errorMessage(e);
          toast.fromError(e, 'Action failed');
    } finally {
      loading = false;
    }
  }

  function handleCodeKeydown(e: KeyboardEvent) {
    if (e.key !== 'Tab') return;
    e.preventDefault();
    const target = e.target as HTMLTextAreaElement;
    const start = target.selectionStart;
    const end = target.selectionEnd;
    const value = target.value;
    target.value = value.substring(0, start) + '    ' + value.substring(end);
    target.selectionStart = target.selectionEnd = start + 4;
    code = target.value;
  }

  function saveTransformExpression(expr: string) {
    transformExpression = expr;
    code = expr;
  }

  async function saveSnippet() {
    if (!snippet) return;
    saveStatus = 'saving';
    saveError = '';

    try {
      let finalCode = code;
      if (isRestType) {
        finalCode = JSON.stringify({
          url: restUrl,
          method: restMethod,
          headers: JSON.parse(restHeaders || '{}'),
          body: restBody,
        });
      } else if (isPingType) {
        finalCode = JSON.stringify({ count: pingCount, timeout: pingTimeout });
      } else if (isTransformType) {
        finalCode = transformExpression;
      }

      await snippets.update(page.params.id!, {
        name,
        description: description || null,
        code: finalCode || null,
        script_language: scriptLanguage || null,
        target_mode: targetMode,
        max_parallel: maxParallel,
        timeout_seconds: timeoutSeconds,
        input_schema: JSON.parse(inputSchemaText || '{}'),
        output_schema: JSON.parse(outputSchemaText || '{}'),
        retry_policy: JSON.parse(retryPolicyText || '{}'),
        logic_diagram_mermaid: logicDiagramMermaid.trim() || null,
        idempotency: idempotency || null,
        network_enabled: networkEnabled,
      });
      saveStatus = 'saved';
      setTimeout(() => { saveStatus = 'idle'; }, 2500);
    } catch (e) {
      saveStatus = 'error';
      saveError = errorMessage(e);toast.fromError(e, 'Couldn’t save snippet');
    }
  }
</script>

<svelte:head><title>{name || 'Snippet'} · FlowWeaver</title></svelte:head>

{#if loading}
  <div class="flex items-center justify-center h-[calc(100vh-4rem)]"><Spinner size="lg" label="Loading snippet…" /></div>
{:else if error && !snippet}
  <div class="p-6"><Alert tone="error">{error}</Alert></div>
{:else if snippet}
  <!-- Top bar. `min-h-14 py-1.5 flex-wrap` (instead of fixed `h-14`) lets
       the bar grow when the snippet name + actions wrap on narrow screens
       rather than clipping the second row. -->
  <div class="flex items-center justify-between gap-3 px-3 sm:px-5 min-h-14 py-1.5 bg-surface-100-900 border-b border-surface-200-800 flex-wrap">
    <div class="flex items-center gap-2 sm:gap-3 min-w-0">
      <Button variant="ghost" size="sm" icon={ArrowLeft} href="/snippets">
        <span class="hidden sm:inline">Snippets</span>
      </Button>
      <span class="text-surface-400-600 hidden sm:inline">/</span>
      <h1 class="text-base font-semibold tracking-tight text-surface-900-100 truncate max-w-[40vw] sm:max-w-none">{name}</h1>
      <Badge tone="neutral" mono>{snippet.type}</Badge>
    </div>
    <div class="flex items-center gap-2 flex-wrap justify-end">
      {#if saveStatus === 'saved'}
        <span class="text-xs text-success-300">Saved</span>
      {:else if saveStatus === 'saving'}
        <span class="text-xs text-warning-300">Saving…</span>
      {:else if saveStatus === 'error'}
        <span class="text-xs text-error-300" title={saveError}>Save failed</span>
      {/if}
      <Button variant="secondary" icon={Play} onclick={() => (showTestDialog = true)} title="Test run">
        <span class="hidden md:inline">Test run</span>
      </Button>
      <Button variant="primary" icon={Save} onclick={saveSnippet} loading={saveStatus === 'saving'} title="Save snippet">Save</Button>
    </div>
  </div>

  <div class="flex h-[calc(100vh-7.5rem)] overflow-hidden">
    <!-- LEFT: editor -->
    <div class="w-[60%] flex flex-col border-r border-surface-200-800 min-w-0">
      {#if isCodeType}
        <div class="flex items-center justify-between px-4 h-9 border-b border-surface-200-800">
          <div class="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-surface-500">
            <Code2 size={12} />
            {snippet.type === 'python_snippet' ? 'Python code' : 'Ansible playbook'}
          </div>
          <span class="text-[10px] font-mono text-surface-500">{code.split('\n').length} lines</span>
        </div>
        <div class="flex-1 min-h-0 relative">
          <textarea
            bind:value={code}
            onkeydown={handleCodeKeydown}
            spellcheck="false"
            class="absolute inset-0 w-full h-full px-4 py-3 bg-surface-50-950 text-surface-900-100 font-mono text-sm leading-relaxed resize-none focus:outline-none border-0 placeholder:text-surface-500"
            placeholder="# Enter your code here…"
          ></textarea>
        </div>
      {:else if isTransformType}
        <div class="flex items-center justify-between px-4 h-9 border-b border-surface-200-800">
          <div class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">Transform expression <FieldHint id="snippets.transform_expression" /></div>
        </div>
        <div class="flex-1 min-h-0 p-3">
          <TransformPlayground
            bind:inputData={transformInput}
            bind:expression={transformExpression}
            onSave={saveTransformExpression}
          />
        </div>
      {:else if isIntegrationType}
        <div class="flex items-center justify-between px-4 h-9 border-b border-surface-200-800">
          <div class="text-xs font-semibold uppercase tracking-wide text-surface-500">Integration action</div>
        </div>
        <div class="flex-1 overflow-y-auto p-5 space-y-4">
          <Card>
            <div class="text-[10px] uppercase tracking-wider text-surface-500 mb-3">Connection details</div>
            {#if code}
              {#each Object.entries((() => { try { return JSON.parse(code); } catch { return { raw: code }; } })()) as [key, val]}
                <div class="flex items-start gap-3 mb-2">
                  <span class="text-xs font-mono text-surface-500 w-28 shrink-0 text-right">{key}</span>
                  <span class="text-sm text-surface-900-100 font-mono break-all">{typeof val === 'object' ? JSON.stringify(val, null, 2) : val}</span>
                </div>
              {/each}
            {:else}
              <p class="text-sm text-surface-500 italic">No integration configuration stored.</p>
            {/if}
          </Card>
          <p class="text-xs text-surface-500 italic">Integration action details are configured per-node in the workflow editor.</p>
        </div>
      {:else if isRestType}
        <div class="flex items-center justify-between px-4 h-9 border-b border-surface-200-800">
          <div class="text-xs font-semibold uppercase tracking-wide text-surface-500">REST call</div>
        </div>
        <div class="flex-1 overflow-y-auto p-5 space-y-4">
          <div class="flex gap-2">
            <Select bind:value={restMethod} class="w-28">
              <option>GET</option><option>POST</option><option>PUT</option><option>PATCH</option><option>DELETE</option>
            </Select>
            <Input bind:value={restUrl} placeholder="https://api.example.com/endpoint" />
          </div>
          <Textarea label="Headers (JSON)" help="action_test.body" bind:value={restHeaders} mono rows={4} placeholder={'{"Authorization": "Bearer ..."}'} />
          {#if ['POST', 'PUT', 'PATCH'].includes(restMethod)}
            <Textarea label="Request body" help="action_test.body" bind:value={restBody} mono rows={8} placeholder={'{"key": "value"}'} />
          {/if}
        </div>
      {:else if isPingType}
        <div class="flex items-center justify-between px-4 h-9 border-b border-surface-200-800">
          <div class="text-xs font-semibold uppercase tracking-wide text-surface-500">Ping configuration</div>
        </div>
        <div class="flex-1 overflow-y-auto p-5 space-y-4 max-w-xs">
          <Input label="Count" help="ping.count" type="number" bind:value={pingCount} min="1" max="100" />
          <Input label="Timeout (seconds)" help="snippet_test.timeout" type="number" bind:value={pingTimeout} min="1" max="300" />
        </div>
      {:else}
        <div class="flex items-center justify-between px-4 h-9 border-b border-surface-200-800">
          <div class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">Code / configuration <FieldHint id="snippets.code_editor" /></div>
        </div>
        <div class="flex-1 min-h-0 relative">
          <textarea
            bind:value={code}
            onkeydown={handleCodeKeydown}
            spellcheck="false"
            class="absolute inset-0 w-full h-full px-4 py-3 bg-surface-50-950 text-surface-900-100 font-mono text-sm resize-none focus:outline-none border-0"
            placeholder="# Configuration…"
          ></textarea>
        </div>
      {/if}
    </div>

    <!-- RIGHT: settings + schemas -->
    <div class="w-[40%] flex flex-col overflow-y-auto bg-surface-50-950 min-w-0">
      <div class="p-5 space-y-6">
        <div>
          <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 mb-3 inline-flex items-center gap-1">Settings <FieldHint id="snippets.settings_section" /></h3>
          <div class="space-y-3">
            <Input label="Name" help="snippets.name" bind:value={name} />
            <Textarea label="Description" help="snippets.description" bind:value={description} rows={3} placeholder="Describe this snippet…" />
            <Select label="Target mode" help="snippets.target_mode" bind:value={targetMode}>
              <option value="per_device">per_device</option>
              <option value="once">once</option>
              <option value="per_pool">per_pool</option>
            </Select>
            <div class="grid grid-cols-2 gap-3">
              <Input label="Max parallel" help="snippets.max_parallel" type="number" bind:value={maxParallel} min="1" max="1000" />
              <Input label="Timeout (s)" help="snippets.timeout" type="number" bind:value={timeoutSeconds} min="1" max="86400" />
            </div>
            {#if snippet.type === 'python_snippet'}
              <Select label="Script language" help="snippets.script_language" bind:value={scriptLanguage}>
                <option value="python">Python</option>
                <option value="bash">Bash</option>
              </Select>

              {#if isAdmin}
                <label class="flex items-start gap-2 text-sm text-surface-900-100">
                  <input type="checkbox" bind:checked={networkEnabled} class="mt-0.5" />
                  <span>
                    <span class="font-medium">Network-enabled · interactive SSH</span> <FieldHint id="snippets.network_enabled" />
                    <span class="block text-[11px] text-surface-500">
                      Lifts the sandbox network isolation so this snippet can use
                      netmiko/paramiko for interactive SSH (e.g. a password change that
                      prompts for confirmation). Only enable for trusted code — admin only.
                    </span>
                  </span>
                </label>
              {:else if networkEnabled}
                <p class="text-[11px] text-warning-300">
                  This snippet is network-enabled (interactive SSH); only an admin can change it.
                </p>
              {/if}
            {/if}

            <!-- S13.6 follow-up: rollback policy override. Empty value
                 inherits the handler's DefaultIdempotency. The
                 WorkflowRollbackAnalyzer floors any softening here to
                 the handler's value when the handler is stricter. -->
            <div class="space-y-1">
              <Select label="Rollback policy override" help="snippets.rollback_policy" bind:value={idempotency}>
                <option value="">— inherit handler default —</option>
                <option value="idempotent">Idempotent (safe to retry; rollback always passes)</option>
                <option value="requires_compensation">Requires compensation (needs a failure edge)</option>
                <option value="non_reversible">Non-reversible (blocks rollback)</option>
              </Select>
              <p class="text-[11px] text-surface-500">
                The handler's floor wins when stricter. Setting <code>idempotent</code>
                on a snippet whose handler ships non-reversible (e.g. <code>ssh</code>,
                <code>ansible_playbook</code>) has no effect — the analyzer ignores
                downgrades below the handler's <code>DefaultIdempotency</code>.
              </p>
            </div>
          </div>
        </div>

        <div class="border-t border-surface-200-800 pt-5">
          <div class="flex items-center gap-2 mb-2">
            <FileJson size={12} class="text-surface-500" />
            <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">Input schema <FieldHint id="snippets.input_schema" /></h3>
          </div>
          <Textarea bind:value={inputSchemaText} mono rows={8} placeholder={'{"type": "object", "properties": {}}'} />
        </div>

        <div class="border-t border-surface-200-800 pt-5">
          <div class="flex items-center gap-2 mb-2">
            <FileJson size={12} class="text-surface-500" />
            <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">Output schema <FieldHint id="snippets.output_schema" /></h3>
          </div>
          <Textarea bind:value={outputSchemaText} mono rows={8} placeholder={'{"type": "object", "properties": {}}'} />
        </div>

        <div class="border-t border-surface-200-800 pt-5">
          <div class="flex items-center gap-2 mb-2">
            <Repeat size={12} class="text-surface-500" />
            <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">Retry policy <FieldHint id="snippets.retry_policy" /></h3>
          </div>
          <Textarea bind:value={retryPolicyText} mono rows={4} placeholder={'{"max_retries": 3, "backoff": "exponential"}'} />
        </div>

        <div class="border-t border-surface-200-800 pt-5">
          <div class="flex items-center justify-between mb-2">
            <div class="flex items-center gap-2">
              <Code2 size={12} class="text-surface-500" />
              <h3 class="text-xs font-semibold uppercase tracking-wide text-surface-500 inline-flex items-center gap-1">Logic diagram <FieldHint id="snippets.logic_diagram" /></h3>
            </div>
            {#if (isCodeType || isTransformType) && !logicDiagramMermaid.trim()}
              <span class="text-[10px] text-warning-500 font-medium">required for this type</span>
            {/if}
          </div>
          <p class="text-[11px] text-surface-500 mb-2">
            Mermaid source describing this task's internal logic. Shown on the workflow canvas when a node using this snippet is selected.
            {#if isCodeType || isTransformType}
              <br/>The backend rejects save if this field is empty or doesn't start with a Mermaid directive (<code class="font-mono">flowchart</code>, <code class="font-mono">graph</code>, <code class="font-mono">sequenceDiagram</code>, …).
            {/if}
          </p>
          <Textarea
            bind:value={logicDiagramMermaid}
            mono
            rows={10}
            placeholder={'flowchart TD\n    in([input: rows]) --> loop{for each}\n    loop --> act[do thing]\n    act --> out([output])'}
          />
          {#if logicDiagramMermaid.trim()}
            <div class="mt-3 rounded-md border border-surface-200-800 bg-surface-100-900/40 p-3">
              <div class="text-[10px] uppercase tracking-wide text-surface-500 mb-2">Preview</div>
              <MermaidDiagram source={logicDiagramMermaid} />
            </div>
          {/if}
        </div>
      </div>
    </div>
  </div>

  <SnippetTestDialog {snippet} bind:open={showTestDialog} />
{/if}
