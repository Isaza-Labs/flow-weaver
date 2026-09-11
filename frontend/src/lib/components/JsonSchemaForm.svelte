<script lang="ts">
  // JSON-schema driven form. Svelte 5 runes with $bindable.
  // Defaults: callers should pass a value pre-populated with defaults — we no
  // longer auto-mutate the value object on render, which previously caused
  // unstable reactivity loops.
  import JsonSchemaForm from './JsonSchemaForm.svelte';

  interface Schema {
    type?: string;
    title?: string;
    description?: string;
    properties?: Record<string, Schema>;
    required?: string[];
    enum?: unknown[];
    default?: unknown;
    placeholder?: string;
    minimum?: number;
    maximum?: number;
    format?: string;
    maxLength?: number;
    'x-order'?: string[];
  }

  interface Props {
    schema?: Schema;
    value?: Record<string, unknown>;
    readonly?: boolean;
    depth?: number;
  }

  let {
    schema = {},
    value = $bindable<Record<string, unknown>>({}),
    readonly = false,
    depth = 0,
  }: Props = $props();

  const properties = $derived((schema.properties || {}) as Record<string, Schema>);
  const requiredFields = $derived(new Set(schema.required ?? []));
  // Render in the schema's optional `x-order` when present. Postgres jsonb does
  // NOT preserve object key order (it normalises keys by length then bytewise),
  // so a snippet's input_schema can't rely on property declaration order — an
  // `x-order` ARRAY survives jsonb intact. Listed keys lead in that order; any
  // not listed follow in their natural order.
  const propertyKeys = $derived(orderKeys(Object.keys(properties), schema['x-order']));

  function orderKeys(keys: string[], order?: string[]): string[] {
    if (!order || order.length === 0) return keys;
    const inOrder = order.filter((k) => keys.includes(k));
    const rest = keys.filter((k) => !inOrder.includes(k));
    return [...inOrder, ...rest];
  }

  function updateField(key: string, val: unknown) {
    value = { ...value, [key]: val };
  }

  function getFieldType(prop: Schema): string {
    if (prop.enum) return 'enum';
    if (prop.type === 'boolean') return 'boolean';
    if (prop.type === 'integer' || prop.type === 'number') return 'number';
    if (prop.type === 'object' && prop.properties) return 'object';
    if (prop.type === 'array') return 'array';
    return 'string';
  }

  function effectiveValue(key: string, prop: Schema): unknown {
    return value[key] !== undefined ? value[key] : prop.default;
  }
</script>

{#if propertyKeys.length === 0}
  <!-- Schema declares no properties (e.g. integration_action whose
       config shape varies per action: integration_id/action_id/body
       aren't in the snippet's input_schema). Fall back to a raw JSON
       editor over the whole value so the user can still see and edit
       what the agent / a previous save put in `config_overrides`,
       instead of being told there's nothing to configure. -->
  <textarea
    value={JSON.stringify(value ?? {}, null, 2)}
    oninput={(e) => {
      try {
        value = JSON.parse(e.currentTarget.value);
      } catch {
        // Invalid JSON while typing — keep last good value, no commit.
      }
    }}
    disabled={readonly}
    rows="10"
    placeholder={'{}'}
    class="w-full px-3 py-2 bg-surface-50-950 border border-surface-300-700 rounded-md text-xs font-mono text-surface-900-100 placeholder:text-surface-500 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50 resize-y"
  ></textarea>
  <p class="text-[10px] text-surface-500 mt-1 italic">
    Snippet declares no input schema — editing raw JSON. Keys like
    <code class="font-mono">integration_id</code>,
    <code class="font-mono">action_id</code>,
    <code class="font-mono">body</code> live here for integration actions.
  </p>
{:else}
  <div class="space-y-3" class:pl-3={depth > 0} class:border-l={depth > 0} class:border-surface-300-700={depth > 0}>
    {#each propertyKeys as key}
      {@const prop = properties[key]}
      {@const fieldType = getFieldType(prop)}
      {@const isRequired = requiredFields.has(key)}
      {@const fieldId = `field-${key}-${depth}`}
      {@const current = effectiveValue(key, prop)}

      <div>
        {#if fieldType !== 'boolean'}
          <label for={fieldId} class="block text-xs font-medium text-surface-600-400 mb-1">
            {prop.title || key}
            <span class="ml-1.5 px-1.5 py-0.5 rounded bg-surface-200-800 text-surface-600-400 text-[10px] font-mono font-normal align-middle">{fieldType}</span>
            {#if isRequired}<span class="text-error-400 ml-0.5">*</span>{/if}
          </label>
        {/if}

        {#if prop.description}
          <p class="text-[10px] text-surface-500 mb-1">{prop.description}</p>
        {/if}

        {#if fieldType === 'string'}
          {#if prop.format === 'textarea' || (prop.maxLength && prop.maxLength > 200)}
            <textarea
              id={fieldId}
              value={(current as string) ?? ''}
              oninput={(e) => updateField(key, e.currentTarget.value)}
              disabled={readonly}
              placeholder={prop.placeholder || (prop.default as string) || ''}
              rows="3"
              class="w-full px-3 py-2 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm text-surface-900-100 placeholder:text-surface-500 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50 resize-y"
            ></textarea>
          {:else}
            <input
              id={fieldId}
              type="text"
              value={(current as string) ?? ''}
              oninput={(e) => updateField(key, e.currentTarget.value)}
              disabled={readonly}
              placeholder={prop.placeholder || (prop.default as string) || ''}
              class="w-full h-8 px-3 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm text-surface-900-100 placeholder:text-surface-500 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50"
            />
          {/if}

        {:else if fieldType === 'number'}
          <input
            id={fieldId}
            type="number"
            value={(current as number) ?? ''}
            oninput={(e) => updateField(key, e.currentTarget.valueAsNumber)}
            disabled={readonly}
            placeholder={prop.default?.toString() ?? ''}
            min={prop.minimum}
            max={prop.maximum}
            step={prop.type === 'integer' ? 1 : 'any'}
            class="w-full h-8 px-3 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm text-surface-900-100 placeholder:text-surface-500 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50"
          />

        {:else if fieldType === 'boolean'}
          <label class="flex items-center gap-2 cursor-pointer" class:opacity-50={readonly}>
            <input
              type="checkbox"
              checked={(current as boolean) ?? false}
              onchange={(e) => updateField(key, e.currentTarget.checked)}
              disabled={readonly}
              class="w-4 h-4 rounded border-surface-300-700 bg-surface-50-950 text-primary-500 focus:ring-2 focus:ring-primary-500/30"
            />
            <span class="text-xs text-surface-700-300">
              {prop.title || key}
              <span class="ml-1.5 px-1.5 py-0.5 rounded bg-surface-200-800 text-surface-600-400 text-[10px] font-mono font-normal align-middle">{fieldType}</span>
              {#if isRequired}<span class="text-error-400 ml-0.5">*</span>{/if}
            </span>
          </label>

        {:else if fieldType === 'enum'}
          <select
            id={fieldId}
            value={(current as string) ?? ''}
            onchange={(e) => updateField(key, e.currentTarget.value)}
            disabled={readonly}
            class="w-full h-8 px-3 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm text-surface-900-100 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50"
          >
            <option value="" disabled>Select…</option>
            {#each prop.enum ?? [] as option}
              <option value={option as string | number}>{option}</option>
            {/each}
          </select>

        {:else if fieldType === 'object'}
          {@const objProps = (prop.properties as Record<string, unknown> | undefined) ?? {}}
          {#if Object.keys(objProps).length > 0}
            <fieldset class="mt-1">
              <legend class="text-xs font-semibold text-surface-700-300 mb-1">
                {prop.title || key}
                <span class="ml-1.5 px-1.5 py-0.5 rounded bg-surface-200-800 text-surface-600-400 text-[10px] font-mono font-normal align-middle">{fieldType}</span>
              </legend>
              <JsonSchemaForm
                schema={prop}
                value={(value[key] as Record<string, unknown>) ?? {}}
                {readonly}
                depth={depth + 1}
              />
            </fieldset>
          {:else}
            <!-- Object with no declared properties (e.g. integration_action
                 `body` whose shape varies per action). Fall back to a raw
                 JSON textarea so the user can still edit the value instead
                 of seeing "No configurable fields". Same UX as `array`. -->
            <textarea
              id={fieldId}
              value={JSON.stringify(current ?? {}, null, 2)}
              oninput={(e) => {
                try {
                  const parsed = JSON.parse(e.currentTarget.value);
                  updateField(key, parsed);
                } catch {
                  // Invalid JSON while typing — keep last good value, no commit.
                }
              }}
              disabled={readonly}
              rows="6"
              placeholder={'{}'}
              class="w-full px-3 py-2 bg-surface-50-950 border border-surface-300-700 rounded-md text-xs font-mono text-surface-900-100 placeholder:text-surface-500 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50 resize-y"
            ></textarea>
          {/if}

        {:else if fieldType === 'array'}
          <textarea
            id={fieldId}
            value={JSON.stringify(current ?? [], null, 2)}
            oninput={(e) => {
              try {
                const parsed = JSON.parse(e.currentTarget.value);
                updateField(key, parsed);
              } catch {
                // Invalid JSON while typing — keep last good value, no commit.
              }
            }}
            disabled={readonly}
            rows="3"
            placeholder="[]"
            class="w-full px-3 py-2 bg-surface-50-950 border border-surface-300-700 rounded-md text-xs font-mono text-surface-900-100 placeholder:text-surface-500 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50 resize-y"
          ></textarea>

        {:else}
          <input
            id={fieldId}
            type="text"
            value={(current as string) ?? ''}
            oninput={(e) => updateField(key, e.currentTarget.value)}
            disabled={readonly}
            class="w-full h-8 px-3 bg-surface-50-950 border border-surface-300-700 rounded-md text-sm text-surface-900-100 placeholder:text-surface-500 focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 disabled:opacity-50"
          />
        {/if}
      </div>
    {/each}
  </div>
{/if}
