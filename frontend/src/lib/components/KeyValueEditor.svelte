<script lang="ts">
  // Recursive structured editor for plain JSON objects. Each row is a
  // key + type-aware value editor. When a field's type is `object` the
  // component renders itself one level deeper so nested objects stay
  // visual — no need to drop into a raw JSON textarea for sub-trees.
  //
  // Arrays and other non-plain shapes fall back to a JSON textarea row
  // ("json" type) so the user can still see and edit them; that escape
  // hatch is also a no-op for the common case (no array editing yet).
  //
  // Self-import is what makes the recursion work — Svelte 5 supports it
  // without ceremony.

  import KeyValueEditor from './KeyValueEditor.svelte';
  import { Plus, Trash2, ChevronDown, ChevronRight } from 'lucide-svelte';

  let {
    value = $bindable<Record<string, unknown>>({}),
    readonly = false,
    depth = 0,
  }: {
    value?: Record<string, unknown>;
    readonly?: boolean;
    depth?: number;
  } = $props();

  type FieldType = 'string' | 'number' | 'boolean' | 'object' | 'json';

  function detectType(v: unknown): FieldType {
    if (v === null) return 'string'; // null surfaces as empty string; user can flip via the type select.
    if (typeof v === 'boolean') return 'boolean';
    if (typeof v === 'number') return 'number';
    if (Array.isArray(v)) return 'json';
    if (typeof v === 'object') return 'object';
    return 'string';
  }

  // ─── Per-row local state ──────────────────────────────────────────────
  // Each row keeps an optional rename draft and an optional JSON-text
  // buffer so an in-progress invalid edit doesn't clobber `value`.
  let renameDraft = $state<Record<string, string>>({});
  let jsonRowText = $state<Record<string, string>>({});
  let jsonRowError = $state<Record<string, string>>({});

  // Collapsed state for nested objects. Default: expanded one level deep,
  // collapsed below — keeps the dialog scrollable on big trees.
  let collapsed = $state<Record<string, boolean>>({});

  const fieldEntries = $derived(Object.entries(value ?? {}));

  function setKey(oldKey: string, newKeyRaw: string) {
    const newKey = newKeyRaw.trim();
    if (!newKey || newKey === oldKey) {
      delete renameDraft[oldKey];
      renameDraft = { ...renameDraft };
      return;
    }
    if (newKey in (value ?? {})) {
      // Don't clobber an existing key — reset the draft.
      delete renameDraft[oldKey];
      renameDraft = { ...renameDraft };
      return;
    }
    const next: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value ?? {})) {
      next[k === oldKey ? newKey : k] = v;
    }
    value = next;
    delete renameDraft[oldKey];
    renameDraft = { ...renameDraft };
    if (oldKey in jsonRowText) {
      jsonRowText[newKey] = jsonRowText[oldKey];
      delete jsonRowText[oldKey];
    }
    if (oldKey in collapsed) {
      collapsed[newKey] = collapsed[oldKey];
      delete collapsed[oldKey];
    }
  }

  function setValue(key: string, v: unknown) {
    value = { ...value, [key]: v };
  }

  function removeKey(key: string) {
    const next = { ...value };
    delete next[key];
    value = next;
    delete jsonRowText[key];
    delete jsonRowError[key];
    delete collapsed[key];
  }

  function addField() {
    let base = 'new_field';
    let candidate = base;
    let i = 1;
    while (candidate in (value ?? {})) {
      candidate = `${base}_${i++}`;
    }
    value = { ...value, [candidate]: '' };
  }

  function changeType(key: string, type: FieldType) {
    const current = value[key];
    let next: unknown;
    switch (type) {
      case 'string':
        next = typeof current === 'string'
          ? current
          : current === undefined || current === null
            ? ''
            : String(current);
        break;
      case 'number':
        next = typeof current === 'number' ? current : Number(current ?? 0) || 0;
        break;
      case 'boolean':
        next = Boolean(current);
        break;
      case 'object':
        // Coerce primitives to {} but preserve existing object shape if
        // we're already there — switching object → array (json) → object
        // would otherwise wipe nested keys.
        next = current !== null && typeof current === 'object' && !Array.isArray(current)
          ? current
          : {};
        break;
      case 'json':
        next = current !== null && typeof current === 'object' ? current : [];
        break;
    }
    setValue(key, next);
  }

  function onJsonRowInput(key: string, raw: string) {
    jsonRowText[key] = raw;
    if (!raw.trim()) {
      jsonRowError[key] = '';
      setValue(key, []);
      return;
    }
    try {
      const parsed = JSON.parse(raw);
      jsonRowError[key] = '';
      setValue(key, parsed);
    } catch (e) {
      jsonRowError[key] = (e as Error).message;
    }
  }

  function jsonRowDisplay(key: string, v: unknown): string {
    if (key in jsonRowText) return jsonRowText[key];
    return JSON.stringify(v ?? null, null, 2);
  }

  function toggleCollapsed(key: string) {
    collapsed[key] = !(collapsed[key] ?? depth >= 1);
  }

  function isCollapsed(key: string): boolean {
    // Default collapsed when we're already two levels in — keeps deep
    // trees from exploding visually on first open.
    return collapsed[key] ?? depth >= 1;
  }

  // Bindable wrapper for the recursive child — needed because Svelte's
  // `bind:` needs a writable expression, and `value[key]` isn't one.
  function makeChildBinding(key: string) {
    return {
      get current() {
        const v = value[key];
        return (v && typeof v === 'object' && !Array.isArray(v))
          ? (v as Record<string, unknown>)
          : {};
      },
      set current(v: Record<string, unknown>) {
        setValue(key, v);
      },
    };
  }
</script>

<div class="space-y-2">
  {#if fieldEntries.length === 0}
    <div class="text-[11px] text-surface-500 italic px-1 py-1.5">
      {depth === 0
        ? 'No parameters yet. Click Add field to create one or switch to JSON to paste a whole object.'
        : 'Empty object — add a field below.'}
    </div>
  {:else}
    {#each fieldEntries as [key, fieldVal] (key)}
      {@const fieldType = detectType(fieldVal)}
      {@const draftKey = renameDraft[key] ?? key}
      {@const childBinding = makeChildBinding(key)}
      {@const childCount = fieldType === 'object'
        ? Object.keys((fieldVal as Record<string, unknown>) ?? {}).length
        : 0}
      <div class="rounded-md border border-surface-200-800/70 bg-surface-50-950/40 p-2 space-y-1.5">
        <div class="flex items-center gap-1.5">
          {#if fieldType === 'object'}
            <button
              type="button"
              onclick={() => toggleCollapsed(key)}
              class="inline-flex items-center justify-center w-5 h-5 rounded text-surface-500 hover:text-surface-900-100 hover:bg-surface-200-800/40 cursor-pointer transition-colors shrink-0"
              title={isCollapsed(key) ? 'Expand' : 'Collapse'}
              aria-label={isCollapsed(key) ? 'Expand object' : 'Collapse object'}
              aria-expanded={!isCollapsed(key)}
            >
              {#if isCollapsed(key)}
                <ChevronRight size={12} />
              {:else}
                <ChevronDown size={12} />
              {/if}
            </button>
          {:else}
            <span class="w-5 shrink-0"></span>
          {/if}
          <input
            type="text"
            value={draftKey}
            oninput={(e) => (renameDraft = { ...renameDraft, [key]: e.currentTarget.value })}
            onblur={() => setKey(key, renameDraft[key] ?? key)}
            onkeydown={(e) => { if (e.key === 'Enter') (e.currentTarget as HTMLInputElement).blur(); }}
            disabled={readonly}
            placeholder="key"
            class="flex-1 h-7 bg-surface-50-950 border border-surface-300-700 rounded-md text-xs font-mono px-2 focus:outline-none focus:border-primary-500 focus:ring-1 focus:ring-primary-500/30 disabled:opacity-60"
          />
          <select
            value={fieldType}
            onchange={(e) => changeType(key, e.currentTarget.value as FieldType)}
            disabled={readonly}
            class="h-7 bg-surface-50-950 border border-surface-300-700 rounded-md text-[11px] px-1.5 focus:outline-none focus:border-primary-500 disabled:opacity-60"
            title="Field type"
          >
            <option value="string">string</option>
            <option value="number">number</option>
            <option value="boolean">boolean</option>
            <option value="object">object</option>
            <option value="json">json</option>
          </select>
          {#if fieldType === 'object' && isCollapsed(key)}
            <span class="text-[10px] text-surface-500 font-mono shrink-0">{childCount} {childCount === 1 ? 'field' : 'fields'}</span>
          {/if}
          <button
            type="button"
            onclick={() => removeKey(key)}
            disabled={readonly}
            class="inline-flex items-center justify-center w-7 h-7 rounded text-surface-500 hover:text-error-300 hover:bg-error-500/10 cursor-pointer transition-colors disabled:opacity-40 disabled:cursor-not-allowed shrink-0"
            title="Remove field"
            aria-label="Remove {key}"
          >
            <Trash2 size={12} />
          </button>
        </div>

        {#if fieldType === 'boolean'}
          <div class="pl-6">
            <label class="inline-flex items-center gap-1.5 text-xs text-surface-700-300 cursor-pointer">
              <input
                type="checkbox"
                checked={fieldVal === true}
                onchange={(e) => setValue(key, e.currentTarget.checked)}
                disabled={readonly}
                class="cursor-pointer"
              />
              <span class="font-mono">{fieldVal === true ? 'true' : 'false'}</span>
            </label>
          </div>
        {:else if fieldType === 'number'}
          <div class="pl-6">
            <input
              type="number"
              value={fieldVal as number}
              oninput={(e) => setValue(key, e.currentTarget.value === '' ? 0 : Number(e.currentTarget.value))}
              disabled={readonly}
              class="w-full h-7 bg-surface-50-950 border border-surface-300-700 rounded-md text-xs font-mono px-2 focus:outline-none focus:border-primary-500 focus:ring-1 focus:ring-primary-500/30 disabled:opacity-60 tabular-nums"
            />
          </div>
        {:else if fieldType === 'object'}
          {#if !isCollapsed(key)}
            <div class="pl-3 ml-2 border-l-2 border-surface-300-700/50">
              <KeyValueEditor
                bind:value={childBinding.current}
                {readonly}
                depth={depth + 1}
              />
            </div>
          {/if}
        {:else if fieldType === 'json'}
          <div class="pl-6">
            <textarea
              value={jsonRowDisplay(key, fieldVal)}
              oninput={(e) => onJsonRowInput(key, e.currentTarget.value)}
              disabled={readonly}
              spellcheck="false"
              rows={Math.min(8, Math.max(3, JSON.stringify(fieldVal ?? [], null, 2).split('\n').length))}
              class="w-full bg-surface-50-950 border rounded-md text-xs font-mono px-2 py-1.5 leading-relaxed focus:outline-none focus:ring-1 focus:ring-primary-500/30 resize-y disabled:opacity-60
                {jsonRowError[key] ? 'border-error-500/60 focus:border-error-500' : 'border-surface-300-700 focus:border-primary-500'}"
            ></textarea>
            {#if jsonRowError[key]}
              <div class="mt-1 text-[10px] text-error-300 font-mono">{jsonRowError[key]}</div>
            {:else if Array.isArray(fieldVal)}
              <div class="mt-0.5 text-[10px] text-surface-500">Array · {(fieldVal as unknown[]).length} {(fieldVal as unknown[]).length === 1 ? 'item' : 'items'}</div>
            {/if}
          </div>
        {:else}
          <div class="pl-6">
            <input
              type="text"
              value={(fieldVal as string) ?? ''}
              oninput={(e) => setValue(key, e.currentTarget.value)}
              disabled={readonly}
              class="w-full h-7 bg-surface-50-950 border border-surface-300-700 rounded-md text-xs px-2 focus:outline-none focus:border-primary-500 focus:ring-1 focus:ring-primary-500/30 disabled:opacity-60"
            />
          </div>
        {/if}
      </div>
    {/each}
  {/if}

  {#if !readonly}
    <button
      type="button"
      onclick={addField}
      class="inline-flex items-center gap-1 h-7 px-2.5 text-[11px] font-medium rounded-md border border-dashed border-surface-300-700 text-surface-600-400 hover:text-primary-300 hover:border-primary-500/60 hover:bg-primary-500/5 cursor-pointer transition-colors"
    >
      <Plus size={12} /> Add field
    </button>
  {/if}
</div>
