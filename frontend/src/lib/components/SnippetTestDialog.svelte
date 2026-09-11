<script lang="ts">
  import { toast } from '$lib/components/ui';
  import {
    snippets,
    errorMessage,
    type Snippet,
    type SnippetTestResponse,
  } from '$lib/api/client';
  import DevicePicker from '$lib/components/DevicePicker.svelte';
  import {
    Dialog, Button, Badge, Alert, Textarea, Input,
  } from '$lib/components/ui';
  import { Play } from 'lucide-svelte';

  let {
    snippet,
    open = $bindable(false),
  }: {
    snippet: Snippet | null;
    open: boolean;
  } = $props();

  let inputText = $state('{}');
  let timeoutSeconds = $state(60);
  let selectedDevices = $state<string[]>([]);
  let running = $state(false);
  let elapsed = $state(0);
  let timerHandle: ReturnType<typeof setInterval> | null = null;
  let inputError = $state('');
  let result = $state<SnippetTestResponse | null>(null);
  let runError = $state('');

  const isPerDevice = $derived(snippet?.target_mode === 'per_device');

  $effect(() => {
    if (open) {
      result = null;
      runError = '';
      inputError = '';
      elapsed = 0;
      const defaults = extractInputDefaults(snippet?.input_schema);
      inputText = JSON.stringify(defaults, null, 2);
    }
  });

  function extractInputDefaults(schema: unknown): Record<string, unknown> {
    if (!schema || typeof schema !== 'object') return {};
    const props = (schema as { properties?: Record<string, unknown> }).properties;
    if (!props || typeof props !== 'object') return {};
    const out: Record<string, unknown> = {};
    for (const [key, def] of Object.entries(props)) {
      if (def && typeof def === 'object' && 'default' in (def as object)) {
        out[key] = (def as { default: unknown }).default;
      }
    }
    return out;
  }

  async function runTest() {
    if (!snippet) return;
    inputError = '';
    runError = '';
    result = null;

    let parsed: Record<string, unknown> = {};
    if (inputText.trim()) {
      try {
        parsed = JSON.parse(inputText);
      } catch (e) {
        inputError = 'Input is not valid JSON: ' + (e instanceof Error ? e.message : String(e));
        return;
      }
    }

    running = true;
    elapsed = 0;
    const startedAt = Date.now();
    timerHandle = setInterval(() => {
      elapsed = Math.round((Date.now() - startedAt) / 100) / 10;
    }, 100);

    try {
      result = await snippets.test(snippet.id, {
        input: parsed,
        target_devices: isPerDevice ? selectedDevices : undefined,
        timeout_seconds: timeoutSeconds,
      });
    } catch (e) {
      runError = errorMessage(e);toast.fromError(e, 'Couldn’t run test');
    } finally {
      running = false;
      if (timerHandle) {
        clearInterval(timerHandle);
        timerHandle = null;
      }
    }
  }

  function statusTone(status: string): 'success' | 'error' | 'warning' | 'neutral' {
    switch (status) {
      case 'success': return 'success';
      case 'failure': case 'failed': return 'error';
      case 'timeout': return 'warning';
      default: return 'neutral';
    }
  }

  function formatJson(obj: unknown): string {
    if (obj === undefined || obj === null) return '';
    try { return JSON.stringify(obj, null, 2); } catch { return String(obj); }
  }

  function hasContent(value: unknown): boolean {
    if (value === undefined || value === null) return false;
    if (typeof value === 'string') return value.length > 0;
    if (Array.isArray(value)) return value.length > 0;
    if (typeof value === 'object') return Object.keys(value).length > 0;
    return true;
  }
</script>

{#if snippet}
  <Dialog bind:open size="xl" closable={!running} title="Test run · {snippet.name}">
    <div class="flex items-center gap-2 mb-4">
      <Badge tone="neutral">{snippet.type}</Badge>
      <Badge tone="neutral">{snippet.target_mode}</Badge>
    </div>

    <div class="space-y-5">
      <div>
        <Textarea
          label="Input (JSON)" help="snippet_test.input"
          bind:value={inputText}
          rows={6}
          mono
          placeholder={'{}'}
          hint="JSON object passed to the snippet runner. Defaults from input_schema are pre-filled where present."
        />
        {#if inputError}
          <div class="mt-2"><Alert tone="error">{inputError}</Alert></div>
        {/if}
      </div>

      {#if isPerDevice}
        <div>
          <div class="text-xs font-medium text-surface-600-400 mb-2">Target devices</div>
          <DevicePicker bind:selected={selectedDevices} />
          <p class="text-xs text-surface-500 mt-1">
            This snippet runs once per selected device. Picking none will still create one step but with no device assigned.
          </p>
        </div>
      {/if}

      <div class="max-w-xs">
        <Input label="Timeout (seconds)" help="snippet_test.timeout" type="number" bind:value={timeoutSeconds} min="5" max="600" />
      </div>

      {#if running}
        <div class="flex items-center gap-3 p-3 bg-primary-500/10 border border-primary-500/20 rounded-md">
          <span class="w-2 h-2 rounded-full bg-primary-400 animate-pulse"></span>
          <div class="text-sm">Running… <span class="font-mono opacity-70">{elapsed.toFixed(1)}s</span></div>
        </div>
      {:else if runError}
        <Alert tone="error">{runError}</Alert>
      {:else if result}
        <div class="space-y-3">
          <div class="flex items-baseline gap-3">
            <Badge tone={statusTone(result.status)}>{result.status}</Badge>
            <span class="text-sm font-mono text-surface-600-400 tabular-nums">{result.duration_ms}ms</span>
            <a href="/runs/{result.run_id}" class="text-xs text-primary-300 hover:text-primary-200 ml-auto">View run →</a>
          </div>

          {#if result.error}
            <Alert tone="error" title="Error">
              <pre class="text-xs whitespace-pre-wrap font-mono">{result.error}</pre>
            </Alert>
          {/if}

          {#if result.steps && result.steps.length > 0}
            <div class="space-y-3">
              {#each result.steps as step, i (step.step_run_id)}
                <div class="bg-surface-50-950 border border-surface-300-700 rounded-md p-3 space-y-3">
                  <div class="flex items-baseline gap-2 flex-wrap">
                    <span class="text-[10px] uppercase tracking-wider text-surface-500">Step {i + 1} / {result.steps.length}</span>
                    <Badge tone={statusTone(step.status)} size="xs">{step.status}</Badge>
                    {#if step.device_id}
                      <span class="text-xs font-mono text-surface-600-400">device: {step.device_id}</span>
                    {/if}
                    <span class="text-[10px] font-mono text-surface-500 ml-auto">{step.step_run_id.slice(0, 8)}…</span>
                  </div>

                  {#if step.error}
                    <div>
                      <div class="text-[10px] uppercase tracking-wider text-surface-500 mb-1">Error</div>
                      <pre class="text-xs whitespace-pre-wrap font-mono bg-error-500/10 text-error-300 p-2 rounded max-h-48 overflow-auto">{step.error}</pre>
                    </div>
                  {/if}

                  <div>
                    <div class="text-[10px] uppercase tracking-wider text-surface-500 mb-1">Input</div>
                    {#if hasContent(step.input_payload)}
                      <pre class="text-xs font-mono whitespace-pre-wrap bg-surface-100-900 text-surface-700-300 p-2 rounded max-h-48 overflow-auto">{formatJson(step.input_payload)}</pre>
                    {:else}
                      <div class="text-xs text-surface-500 italic font-mono bg-surface-100-900 p-2 rounded">(empty)</div>
                    {/if}
                  </div>

                  <div>
                    <div class="text-[10px] uppercase tracking-wider text-surface-500 mb-1">Output</div>
                    {#if hasContent(step.output)}
                      <pre class="text-xs font-mono whitespace-pre-wrap bg-surface-100-900 text-surface-700-300 p-2 rounded max-h-64 overflow-auto">{formatJson(step.output)}</pre>
                    {:else}
                      <div class="text-xs text-surface-500 italic font-mono bg-surface-100-900 p-2 rounded">(empty)</div>
                    {/if}
                  </div>

                  <details open={!!step.logs}>
                    <summary class="text-[10px] uppercase tracking-wider text-surface-500 cursor-pointer select-none">
                      Logs {#if !step.logs}<span class="italic normal-case opacity-70">(empty)</span>{/if}
                    </summary>
                    {#if step.logs}
                      <pre class="text-xs font-mono whitespace-pre-wrap mt-1 bg-surface-100-900 text-surface-700-300 p-2 rounded max-h-48 overflow-auto">{step.logs}</pre>
                    {/if}
                  </details>
                </div>
              {/each}
            </div>
          {:else}
            <Alert tone="warning">
              The run completed but no step rows were produced. This usually means the worker did not pick up the job — check that the worker daemon is running.
            </Alert>
          {/if}
        </div>
      {/if}
    </div>

    {#snippet footer()}
      <Button variant="ghost" onclick={() => (open = false)} disabled={running}>Close</Button>
      <Button variant="primary" icon={Play} onclick={runTest} loading={running}>
        {running ? 'Running…' : 'Run test'}
      </Button>
    {/snippet}
  </Dialog>
{/if}
