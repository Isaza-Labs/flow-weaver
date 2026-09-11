<script lang="ts">
  // Form ↔ JSON toggle for node parameters.
  //
  // - When the snippet declares an `input_schema` with properties, Form
  //   mode uses JsonSchemaForm (typed inputs, labels, enums, required).
  // - When there's no schema (common for integration_action snippets
  //   whose shape is action-specific), Form mode falls back to the
  //   recursive KeyValueEditor so nested objects stay visual instead of
  //   collapsing into a textarea.
  // - JSON mode is always available as an escape hatch.
  //
  // Both modes edit the same `value` object via $bindable so the parent
  // dialog never sees stale state.

  import { Code2, ListChecks, Check, Copy } from 'lucide-svelte';
  import { copyText } from '$lib/utils/clipboard';
  import JsonSchemaForm from './JsonSchemaForm.svelte';
  import KeyValueEditor from './KeyValueEditor.svelte';

  type ParamsSchema = {
    type?: string;
    title?: string;
    description?: string;
    properties?: Record<string, unknown>;
    required?: string[];
    [key: string]: unknown;
  };

  let {
    schema = {},
    value = $bindable<Record<string, unknown>>({}),
    readonly = false,
  }: {
    schema?: ParamsSchema;
    value?: Record<string, unknown>;
    readonly?: boolean;
  } = $props();

  type Mode = 'form' | 'json';
  let mode = $state<Mode>('form');

  const hasSchemaProperties = $derived(
    !!schema.properties && Object.keys(schema.properties).length > 0,
  );

  let jsonText = $state('');
  let jsonError = $state<string | null>(null);
  let copied = $state(false);

  function stringify(v: Record<string, unknown>): string {
    return JSON.stringify(v ?? {}, null, 2);
  }

  $effect(() => {
    if (mode !== 'json') return;
    const incoming = stringify(value ?? {});
    try {
      const parsed = jsonText ? JSON.parse(jsonText) : null;
      if (parsed && stringify(parsed) === incoming) return;
    } catch {
      // fall through
    }
    jsonText = incoming;
    jsonError = null;
  });

  function setMode(next: Mode) {
    if (next === mode) return;
    if (mode === 'json' && next === 'form') {
      try {
        const parsed = JSON.parse(jsonText || '{}');
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
          value = parsed as Record<string, unknown>;
        }
        jsonError = null;
      } catch (e) {
        jsonError = (e as Error).message;
        return; // Block the switch so the user can fix the JSON.
      }
    }
    mode = next;
  }

  function onJsonInput(e: Event) {
    const text = (e.target as HTMLTextAreaElement).value;
    jsonText = text;
    if (!text.trim()) {
      value = {};
      jsonError = null;
      return;
    }
    try {
      const parsed = JSON.parse(text);
      if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
        jsonError = 'Parameters must be a JSON object ({ ... }).';
        return;
      }
      jsonError = null;
      value = parsed as Record<string, unknown>;
    } catch (e) {
      jsonError = (e as Error).message;
    }
  }

  function formatJson() {
    try {
      const parsed = JSON.parse(jsonText || '{}');
      jsonText = JSON.stringify(parsed, null, 2);
      jsonError = null;
    } catch (e) {
      jsonError = (e as Error).message;
    }
  }

  async function copyJson() {
    // On failure the user can still select + Ctrl+C from the textarea.
    if (await copyText(jsonText)) {
      copied = true;
      setTimeout(() => (copied = false), 1200);
    }
  }
</script>

<div class="space-y-2">
  <div class="flex items-center justify-between gap-2 flex-wrap">
    <div
      class="inline-flex items-center rounded-md bg-surface-200-800/40 ring-1 ring-surface-300-700/60 p-0.5"
      role="tablist"
      aria-label="Parameters view mode"
    >
      <button
        type="button"
        role="tab"
        aria-selected={mode === 'form'}
        onclick={() => setMode('form')}
        class="inline-flex items-center gap-1 h-6 px-2 text-[11px] font-medium rounded transition-colors cursor-pointer
          {mode === 'form'
            ? 'bg-surface-100-900 text-surface-900-100 shadow-sm ring-1 ring-surface-300-700/80'
            : 'text-surface-600-400 hover:text-surface-800-200'}"
      >
        <ListChecks size={11} /> Form
      </button>
      <button
        type="button"
        role="tab"
        aria-selected={mode === 'json'}
        onclick={() => setMode('json')}
        class="inline-flex items-center gap-1 h-6 px-2 text-[11px] font-medium rounded transition-colors cursor-pointer
          {mode === 'json'
            ? 'bg-surface-100-900 text-surface-900-100 shadow-sm ring-1 ring-surface-300-700/80'
            : 'text-surface-600-400 hover:text-surface-800-200'}"
      >
        <Code2 size={11} /> JSON
      </button>
    </div>

    {#if mode === 'json'}
      <div class="flex items-center gap-1">
        <button
          type="button"
          onclick={formatJson}
          class="h-6 px-2 text-[11px] font-medium text-surface-600-400 hover:text-surface-900-100 hover:bg-surface-200-800/40 rounded cursor-pointer transition-colors"
          title="Pretty-print"
        >
          Format
        </button>
        <button
          type="button"
          onclick={copyJson}
          class="inline-flex items-center gap-1 h-6 px-2 text-[11px] font-medium text-surface-600-400 hover:text-surface-900-100 hover:bg-surface-200-800/40 rounded cursor-pointer transition-colors"
          title="Copy to clipboard"
        >
          {#if copied}
            <Check size={11} class="text-success-300" /> Copied
          {:else}
            <Copy size={11} /> Copy
          {/if}
        </button>
      </div>
    {/if}
  </div>

  {#if mode === 'form'}
    {#if hasSchemaProperties}
      <JsonSchemaForm schema={schema as never} bind:value {readonly} />
    {:else}
      <KeyValueEditor bind:value {readonly} />
    {/if}
  {:else}
    <div class="relative">
      <textarea
        value={jsonText}
        oninput={onJsonInput}
        spellcheck="false"
        disabled={readonly}
        rows={Math.min(20, Math.max(8, jsonText.split('\n').length + 1))}
        class="w-full bg-surface-50-950 border rounded-md text-xs font-mono px-3 py-2 leading-relaxed focus:outline-none focus:ring-2 focus:ring-primary-500/30 resize-y disabled:opacity-60
          {jsonError ? 'border-error-500/60 focus:border-error-500' : 'border-surface-300-700 focus:border-primary-500'}"
        placeholder={'{\n  "key": "value"\n}'}
      ></textarea>
      {#if jsonError}
        <div class="mt-1 text-[11px] text-error-300 font-mono">{jsonError}</div>
      {:else}
        <div class="mt-1 text-[10px] text-surface-500">Changes apply live while the JSON parses cleanly.</div>
      {/if}
    </div>
  {/if}
</div>
