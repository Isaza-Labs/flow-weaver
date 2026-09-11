<script lang="ts">
  // Visual JSON Schema editor for the common shape we use in
  // Snippet.input_schema / output_schema — a top-level object
  // with primitive properties (plus one level of nesting for arrays and
  // sub-objects). Anything beyond that (oneOf, $ref, deep arrays of
  // objects) routes to a "raw JSON" escape hatch so the admin can still
  // edit the schema hand-coded.
  //
  // The editor keeps the emitted schema in the normalized shape:
  //   {
  //     "type": "object",
  //     "required": [...names],
  //     "properties": { name: { type, description, default?, enum?, items? } }
  //   }
  //
  // Any keys we don't recognize on the input are preserved through the
  // `extra` pass-through (raw JSON mode is the only way to edit them).

  import { Button, Input, Select, Textarea, FieldHint } from '$lib/components/ui';
  import { Plus, Trash2, Code2, Eye } from 'lucide-svelte';

  // `value` is bindable so parents can feed and read it via `bind:value`.
  let {
    value = $bindable(),
    title = 'Schema',
  }: {
    value: Record<string, unknown> | null | undefined;
    title?: string;
  } = $props();

  // The "Required" checkboxes below need an explicit `for`/`id`: a <label>
  // otherwise binds to its first LABELABLE descendant, and <button> is one — so
  // FieldHint's info button (which precedes the checkbox) became the labeled
  // control and clicking the label toggled the tooltip instead of the box.
  // Both live inside {#each}, so the id carries the row index too, and the
  // instance prefix keeps two editors on one page (input + output schema) from
  // emitting the same ids.
  const uid = $props.id();

  // Primitive JSON Schema types we support through the visual surface.
  // Anything else (oneOf/$ref/null/unions) forces raw mode.
  type PrimitiveType = 'string' | 'number' | 'integer' | 'boolean';
  type CompoundType = 'array' | 'object';
  type PropType = PrimitiveType | CompoundType;

  const PROP_TYPES: PropType[] = ['string', 'number', 'integer', 'boolean', 'array', 'object'];
  const PRIMITIVE_TYPES: PrimitiveType[] = ['string', 'number', 'integer', 'boolean'];

  // Normalized internal representation of one property. `arrayItemType` is
  // only used when type=array; `nested` only when type=object.
  interface PropRow {
    name: string;
    type: PropType;
    required: boolean;
    description: string;
    defaultValue: string;        // raw text, parsed on commit
    enumValues: string;          // comma-separated for strings
    arrayItemType: PrimitiveType;
    nested: PropRow[];           // one level of sub-object
  }

  let mode = $state<'visual' | 'raw'>('visual');
  let rows = $state<PropRow[]>([]);
  let rawText = $state('');
  let rawError = $state<string | null>(null);
  let unsupportedReason = $state<string | null>(null);

  // Keeps us from re-parsing value→rows after we already emitted a new
  // schema. Without this, `value` flipping triggers hydration which
  // wipes user edits mid-typing.
  let lastEmitted = $state('');

  // Incoming value → internal rows. Runs once on mount and whenever the
  // parent replaces `value` with something we didn't just emit ourselves.
  $effect(() => {
    const incoming = JSON.stringify(value ?? {});
    if (incoming === lastEmitted) return;

    const parsed = hydrate(value);
    if (parsed.mode === 'unsupported') {
      unsupportedReason = parsed.reason;
      rawText = JSON.stringify(value ?? {}, null, 2);
      mode = 'raw';
      rows = [];
    } else {
      unsupportedReason = null;
      rows = parsed.rows;
      rawText = JSON.stringify(value ?? {}, null, 2);
    }
  });

  // rows → schema object. Only fires when the user actually modified
  // something in visual mode, so raw edits (handled below) aren't
  // stomped.
  function commit(next: Record<string, unknown>) {
    lastEmitted = JSON.stringify(next);
    value = next;
  }

  function commitFromRows() {
    commit(rowsToSchema(rows));
  }

  function onRawCommit() {
    rawError = null;
    try {
      const parsed = JSON.parse(rawText || 'null');
      if (parsed === null || typeof parsed !== 'object') {
        rawError = 'Root must be a JSON object.';
        return;
      }
      const hydrated = hydrate(parsed as Record<string, unknown>);
      if (hydrated.mode === 'visual') {
        // Give the visual mode a fresh chance even if the user came here
        // from an unsupported schema — they may have simplified it.
        rows = hydrated.rows;
        unsupportedReason = null;
      }
      commit(parsed);
    } catch (e) {
      rawError = e instanceof Error ? e.message : 'Invalid JSON';
    }
  }

  function toggleMode() {
    if (mode === 'visual') {
      rawText = JSON.stringify(value ?? rowsToSchema(rows), null, 2);
      mode = 'raw';
    } else {
      onRawCommit();
      if (!rawError) mode = 'visual';
    }
  }

  // ─── row ops (visual mode) ──────────────────────────────────────────

  function addRow() {
    rows = [...rows, {
      name: '',
      type: 'string',
      required: false,
      description: '',
      defaultValue: '',
      enumValues: '',
      arrayItemType: 'string',
      nested: [],
    }];
    commitFromRows();
  }

  function removeRow(idx: number) {
    rows = rows.filter((_, i) => i !== idx);
    commitFromRows();
  }

  function updateRow(idx: number, patch: Partial<PropRow>) {
    rows = rows.map((r, i) => (i === idx ? { ...r, ...patch } : r));
    commitFromRows();
  }

  function addNested(idx: number) {
    const row = rows[idx];
    const nested = [...row.nested, {
      name: '',
      type: 'string' as PropType,
      required: false,
      description: '',
      defaultValue: '',
      enumValues: '',
      arrayItemType: 'string' as PrimitiveType,
      nested: [],
    }];
    updateRow(idx, { nested });
  }

  function removeNested(parentIdx: number, nestedIdx: number) {
    const nested = rows[parentIdx].nested.filter((_, i) => i !== nestedIdx);
    updateRow(parentIdx, { nested });
  }

  function updateNested(parentIdx: number, nestedIdx: number, patch: Partial<PropRow>) {
    const nested = rows[parentIdx].nested.map((n, i) =>
      i === nestedIdx ? { ...n, ...patch } : n);
    updateRow(parentIdx, { nested });
  }

  // ─── hydration + serialization ──────────────────────────────────────

  // Turns an incoming schema into rows. Returns 'unsupported' when the
  // input has features we can't round-trip losslessly — the caller falls
  // through to raw-JSON mode.
  function hydrate(input: unknown): { mode: 'visual'; rows: PropRow[] } | { mode: 'unsupported'; reason: string } {
    if (!input || typeof input !== 'object' || Array.isArray(input)) {
      return { mode: 'visual', rows: [] };
    }
    const obj = input as Record<string, unknown>;

    // Allow empty schema + schemas whose type is explicitly "object".
    if (obj.type !== undefined && obj.type !== 'object') {
      return { mode: 'unsupported', reason: `root type is "${String(obj.type)}" (only "object" is supported in visual mode)` };
    }
    if ('oneOf' in obj || 'anyOf' in obj || 'allOf' in obj || '$ref' in obj) {
      return { mode: 'unsupported', reason: 'schema uses oneOf / anyOf / allOf / $ref — edit as raw JSON' };
    }

    const props = (obj.properties ?? {}) as Record<string, unknown>;
    const required = new Set<string>(Array.isArray(obj.required) ? obj.required as string[] : []);

    const parsedRows: PropRow[] = [];
    for (const [name, raw] of Object.entries(props)) {
      if (!raw || typeof raw !== 'object') continue;
      const p = raw as Record<string, unknown>;
      const type = (p.type as PropType) ?? 'string';
      if (!PROP_TYPES.includes(type)) {
        return { mode: 'unsupported', reason: `property "${name}" has unsupported type "${String(type)}"` };
      }

      let arrayItemType: PrimitiveType = 'string';
      if (type === 'array') {
        const items = p.items as Record<string, unknown> | undefined;
        const itemType = (items?.type as PrimitiveType) ?? 'string';
        if (!PRIMITIVE_TYPES.includes(itemType)) {
          return { mode: 'unsupported', reason: `array "${name}" has non-primitive items — edit as raw JSON` };
        }
        arrayItemType = itemType;
      }

      let nested: PropRow[] = [];
      if (type === 'object') {
        const sub = hydrate(raw);
        if (sub.mode === 'unsupported') {
          return { mode: 'unsupported', reason: `nested "${name}": ${sub.reason}` };
        }
        nested = sub.rows;
      }

      parsedRows.push({
        name,
        type,
        required: required.has(name),
        description: typeof p.description === 'string' ? p.description : '',
        defaultValue: p.default === undefined ? '' : JSON.stringify(p.default),
        enumValues: Array.isArray(p.enum) ? (p.enum as unknown[]).map(String).join(', ') : '',
        arrayItemType,
        nested,
      });
    }
    return { mode: 'visual', rows: parsedRows };
  }

  function rowsToSchema(src: PropRow[]): Record<string, unknown> {
    const properties: Record<string, unknown> = {};
    const required: string[] = [];

    for (const r of src) {
      if (!r.name) continue;
      const entry: Record<string, unknown> = { type: r.type };
      if (r.description) entry.description = r.description;

      if (r.type === 'string' && r.enumValues.trim()) {
        entry.enum = r.enumValues.split(',').map(s => s.trim()).filter(Boolean);
      }
      if (r.type === 'array') {
        entry.items = { type: r.arrayItemType };
      }
      if (r.type === 'object') {
        const sub = rowsToSchema(r.nested);
        if (sub.properties) entry.properties = sub.properties;
        if (sub.required) entry.required = sub.required;
      }
      if (r.defaultValue.trim()) {
        try { entry.default = JSON.parse(r.defaultValue); }
        catch { entry.default = r.defaultValue; }
      }

      properties[r.name] = entry;
      if (r.required) required.push(r.name);
    }

    const out: Record<string, unknown> = { type: 'object', properties };
    if (required.length > 0) out.required = required;
    return out;
  }
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div class="text-xs font-semibold text-surface-700-300">{title}</div>
    <div class="flex items-center gap-2">
      {#if unsupportedReason && mode === 'raw'}
        <span class="text-[11px] text-warning-300" title={unsupportedReason}>
          advanced schema — raw mode only
        </span>
      {/if}
      <Button
        size="xs"
        variant="ghost"
        icon={mode === 'visual' ? Code2 : Eye}
        onclick={toggleMode}
      >{mode === 'visual' ? 'Edit raw JSON' : 'Visual editor'}</Button>
    </div>
  </div>

  {#if mode === 'visual'}
    {#if rows.length === 0}
      <div class="text-xs text-surface-500 italic border border-dashed border-surface-300-700 rounded px-3 py-4 text-center">
        No properties yet.
      </div>
    {/if}

    {#each rows as row, idx (idx)}
      <div class="border border-surface-200-800 rounded-md p-3 space-y-2 bg-surface-100-900/40">
        <div class="grid grid-cols-12 gap-2">
          <div class="col-span-4">
            <Input
              label="Name" help="schema.field_name"
              bind:value={row.name}
              placeholder="property_name"
              oninput={() => updateRow(idx, { name: row.name })}
            />
          </div>
          <div class="col-span-3">
            <Select label="Type" help="schema.field_type" value={row.type} onchange={(e) => updateRow(idx, { type: (e.target as HTMLSelectElement).value as PropType })}>
              {#each PROP_TYPES as t (t)}<option value={t}>{t}</option>{/each}
            </Select>
          </div>
          {#if row.type === 'array'}
            <div class="col-span-3">
              <Select label="Item type" help="schema.item_type" value={row.arrayItemType} onchange={(e) => updateRow(idx, { arrayItemType: (e.target as HTMLSelectElement).value as PrimitiveType })}>
                {#each PRIMITIVE_TYPES as t (t)}<option value={t}>{t}</option>{/each}
              </Select>
            </div>
          {:else}
            <div class="col-span-3"></div>
          {/if}
          <div class="col-span-2 flex items-end">
            <label class="inline-flex items-center gap-2 text-xs pb-2" for="{uid}-req-{idx}">
              <FieldHint id="schema.required" />
              <input
                id="{uid}-req-{idx}"
                type="checkbox"
                checked={row.required}
                onchange={(e) => updateRow(idx, { required: (e.target as HTMLInputElement).checked })}
                class="accent-primary-500"
              />
              Required
            </label>
          </div>
        </div>

        <Input
          label="Description" help="schema.field_description"
          bind:value={row.description}
          placeholder="What this field means"
          oninput={() => updateRow(idx, { description: row.description })}
        />

        {#if row.type === 'string'}
          <Input
            label="Enum (comma-separated, leave empty for free text)" help="schema.enum"
            bind:value={row.enumValues}
            placeholder="value-a, value-b"
            oninput={() => updateRow(idx, { enumValues: row.enumValues })}
          />
        {/if}

        {#if row.type !== 'object'}
          <Input
            label={'Default (JSON literal — e.g. text, 5, true, [1,2])'}
            help="schema.default"
            bind:value={row.defaultValue}
            placeholder="leave empty for none"
            oninput={() => updateRow(idx, { defaultValue: row.defaultValue })}
          />
        {/if}

        {#if row.type === 'object'}
          <div class="pl-3 border-l-2 border-primary-500/30 space-y-2">
            <div class="text-[11px] text-surface-500">Nested properties</div>
            {#if row.nested.length === 0}
              <div class="text-xs text-surface-500 italic">No nested properties.</div>
            {/if}
            {#each row.nested as sub, subIdx (subIdx)}
              <div class="border border-surface-200-800 rounded p-2 bg-surface-50-950/50 space-y-2">
                <div class="grid grid-cols-12 gap-2">
                  <div class="col-span-5">
                    <Input
                      label="Name"
                      help="schema.field_name"
                      bind:value={sub.name}
                      oninput={() => updateNested(idx, subIdx, { name: sub.name })}
                    />
                  </div>
                  <div class="col-span-4">
                    <Select
                      label="Type"
                      help="schema.field_type"
                      value={sub.type}
                      onchange={(e) => updateNested(idx, subIdx, { type: (e.target as HTMLSelectElement).value as PropType })}
                    >
                      {#each PRIMITIVE_TYPES as t (t)}<option value={t}>{t}</option>{/each}
                    </Select>
                  </div>
                  <div class="col-span-2 flex items-end">
                    <label class="inline-flex items-center gap-1.5 text-[11px] pb-2" for="{uid}-subreq-{idx}-{subIdx}">
                      <FieldHint id="schema.required" />
                      <input
                        id="{uid}-subreq-{idx}-{subIdx}"
                        type="checkbox"
                        checked={sub.required}
                        onchange={(e) => updateNested(idx, subIdx, { required: (e.target as HTMLInputElement).checked })}
                        class="accent-primary-500"
                      />
                      Req
                    </label>
                  </div>
                  <div class="col-span-1 flex items-end">
                    <Button size="xs" variant="ghost" icon={Trash2} onclick={() => removeNested(idx, subIdx)}>Del</Button>
                  </div>
                </div>
              </div>
            {/each}
            <Button size="xs" variant="ghost" icon={Plus} onclick={() => addNested(idx)}>Add nested</Button>
          </div>
        {/if}

        <div class="flex justify-end pt-1">
          <Button size="xs" variant="ghost" icon={Trash2} onclick={() => removeRow(idx)}>Remove property</Button>
        </div>
      </div>
    {/each}

    <Button size="sm" variant="primary" icon={Plus} onclick={addRow}>Add property</Button>

  {:else}
    <div>
      <Textarea
        label="Raw JSON Schema" help="schema.raw_json"
        bind:value={rawText}
        rows={14}
        onblur={onRawCommit}
      />
      {#if rawError}
        <div class="text-[11px] text-error-300 mt-1">{rawError}</div>
      {/if}
      {#if unsupportedReason}
        <div class="text-[11px] text-warning-300 mt-1">
          Visual mode disabled: {unsupportedReason}
        </div>
      {/if}
    </div>
  {/if}
</div>
