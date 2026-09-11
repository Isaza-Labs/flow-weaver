<script lang="ts">
  import { FieldHint } from '$lib/components/ui';
  let {
    inputData = $bindable('{}'),
    expression = $bindable(''),
    onSave = undefined as ((expr: string) => void) | undefined
  } = $props();

  let output = $state('');
  let error = $state('');
  let debounceTimer: ReturnType<typeof setTimeout>;
  let showReference = $state(false);

  // Generate smart examples from the actual input data
  let smartExamples = $derived(generateExamples(inputData));

  function generateExamples(jsonStr: string): {label: string, expr: string, group: string}[] {
    try {
      const data = JSON.parse(jsonStr);
      if (!data || typeof data !== 'object') return [];
      return analyzeObject(data, '');
    } catch {
      return [];
    }
  }

  function analyzeObject(obj: any, prefix: string): {label: string, expr: string, group: string}[] {
    const suggestions: {label: string, expr: string, group: string}[] = [];
    if (!obj || typeof obj !== 'object') return suggestions;

    const keys = Object.keys(obj);

    for (const key of keys) {
      const val = obj[key];
      const path = prefix ? `${prefix}.${key}` : key;

      if (Array.isArray(val)) {
        const g = `Array: ${key}`;
        suggestions.push({ label: `All ${key}`, expr: path, group: g });
        suggestions.push({ label: `Count`, expr: `length(${path})`, group: g });
        suggestions.push({ label: `First`, expr: `${path}[0]`, group: g });
        suggestions.push({ label: `Last`, expr: `${path}[-1]`, group: g });
        suggestions.push({ label: `Slice first 5`, expr: `${path}[:5]`, group: g });

        if (val.length > 0 && typeof val[0] === 'object' && val[0] !== null) {
          const itemKeys = Object.keys(val[0]);

          // Project each field
          for (const ik of itemKeys.slice(0, 6)) {
            suggestions.push({ label: `→ ${ik}`, expr: `${path}[*].${ik}`, group: g });
          }

          // Unique values for string fields
          for (const ik of itemKeys) {
            const sample = val[0][ik];
            if (typeof sample === 'string' && sample.length < 30) {
              // Filter by this value
              suggestions.push({
                label: `Where ${ik}='${sample}'`,
                expr: `${path}[?${ik}=='${sample}']`,
                group: g
              });
              // Filter NOT equal
              suggestions.push({
                label: `Where ${ik}!='${sample}'`,
                expr: `${path}[?${ik}!='${sample}']`,
                group: g
              });
              // Get unique values (collect all)
              suggestions.push({
                label: `All ${ik} values`,
                expr: `${path}[*].${ik}`,
                group: g
              });
              break;
            }
          }

          // Filter by numeric comparison
          for (const ik of itemKeys) {
            if (typeof val[0][ik] === 'number') {
              suggestions.push({
                label: `Where ${ik} > ${val[0][ik]}`,
                expr: `${path}[?${ik} > \`${val[0][ik]}\`]`,
                group: g
              });
              // Sort by (JMESPath sort_by)
              suggestions.push({
                label: `Sort by ${ik}`,
                expr: `sort_by(${path}, &${ik})`,
                group: g
              });
              // Max/min
              suggestions.push({
                label: `Max ${ik}`,
                expr: `max_by(${path}, &${ik})`,
                group: g
              });
              suggestions.push({
                label: `Min ${ik}`,
                expr: `min_by(${path}, &${ik})`,
                group: g
              });
              break;
            }
          }

          // Boolean filters
          for (const ik of itemKeys) {
            if (typeof val[0][ik] === 'boolean') {
              suggestions.push({
                label: `Where ${ik} is true`,
                expr: `${path}[?${ik}==\`true\`]`,
                group: g
              });
              break;
            }
          }

          // Null check
          suggestions.push({
            label: `Non-null items`,
            expr: `${path}[?@ != \`null\`]`,
            group: g
          });

          // Reshape with first 3 fields
          if (itemKeys.length > 2) {
            const pick = itemKeys.slice(0, 3).map(k => `${k}: ${k}`).join(', ');
            suggestions.push({ label: `Reshape (3 fields)`, expr: `${path}[*].{${pick}}`, group: g });
          }
          // Reshape with all fields
          if (itemKeys.length > 3) {
            const pick = itemKeys.map(k => `${k}: ${k}`).join(', ');
            suggestions.push({ label: `Reshape (all)`, expr: `${path}[*].{${pick}}`, group: g });
          }

          // Flatten nested arrays
          for (const ik of itemKeys) {
            if (Array.isArray(val[0][ik])) {
              suggestions.push({
                label: `Flatten ${ik}`,
                expr: `${path}[].${ik}[]`,
                group: g
              });
              break;
            }
          }

          // Count with filter
          for (const ik of itemKeys) {
            if (typeof val[0][ik] === 'string' && val[0][ik].length < 30) {
              suggestions.push({
                label: `Count where ${ik}='${val[0][ik]}'`,
                expr: `length(${path}[?${ik}=='${val[0][ik]}'])`,
                group: g
              });
              break;
            }
          }
        }
      } else if (typeof val === 'object' && val !== null) {
        const g = `Object: ${key}`;
        suggestions.push({ label: key, expr: path, group: g });
        // Nested keys
        const nestedKeys = Object.keys(val);
        for (const nk of nestedKeys.slice(0, 5)) {
          const nval = val[nk];
          suggestions.push({ label: `${key}.${nk}`, expr: `${path}.${nk}`, group: g });
          // Go one more level if it's an object
          if (typeof nval === 'object' && nval !== null && !Array.isArray(nval)) {
            for (const nnk of Object.keys(nval).slice(0, 3)) {
              suggestions.push({ label: `${key}.${nk}.${nnk}`, expr: `${path}.${nk}.${nnk}`, group: g });
            }
          }
        }
        suggestions.push({ label: `Keys of ${key}`, expr: `keys(${path})`, group: g });
        suggestions.push({ label: `Values of ${key}`, expr: `values(${path})`, group: g });
      } else {
        suggestions.push({ label: `${key}`, expr: path, group: 'Fields' });
      }
    }

    // Top-level summary suggestions
    if (keys.length > 0 && !prefix) {
      const g = 'Summary';
      suggestions.push({ label: 'All keys', expr: 'keys(@)', group: g });
      if (keys.length > 2) {
        const pick = keys.slice(0, 5).map(k => `${k}: ${k}`).join(', ');
        suggestions.push({ label: 'Pick fields', expr: `{${pick}}`, group: g });
      }
      // Identity
      suggestions.push({ label: 'Full object', expr: '@', group: g });
      // Type check
      suggestions.push({ label: 'Type', expr: `type(@)`, group: g });
    }

    return suggestions;
  }

  // Group suggestions by category
  let groupedExamples = $derived(() => {
    const groups: Record<string, {label: string, expr: string}[]> = {};
    for (const ex of smartExamples) {
      if (!groups[ex.group]) groups[ex.group] = [];
      groups[ex.group].push({ label: ex.label, expr: ex.expr });
    }
    return groups;
  });

  // JMESPath reference
  const reference = [
    { category: 'Basics', items: [
      { syntax: 'fieldName', desc: 'Access a field' },
      { syntax: 'a.b.c', desc: 'Nested field access' },
      { syntax: 'a[0]', desc: 'Array index (0-based)' },
      { syntax: 'a[-1]', desc: 'Last element' },
      { syntax: 'a[0:5]', desc: 'Slice (first 5)' },
      { syntax: '@', desc: 'Current element (identity)' },
      { syntax: '*', desc: 'Wildcard (all values of object)' },
    ]},
    { category: 'Projections', items: [
      { syntax: 'items[*].name', desc: 'Get name from each item' },
      { syntax: 'items[*].{n: name, s: status}', desc: 'Reshape each item' },
      { syntax: 'items[].tags[]', desc: 'Flatten nested arrays' },
      { syntax: '*.name', desc: 'Get name from all object values' },
    ]},
    { category: 'Filters', items: [
      { syntax: "items[?status=='active']", desc: 'Filter by string value' },
      { syntax: 'items[?count > `10`]', desc: 'Filter by number (use backticks)' },
      { syntax: "items[?!(status=='down')]", desc: 'Negated filter' },
      { syntax: "items[?status=='up' && speed > `1000`]", desc: 'AND filter' },
      { syntax: "items[?status=='up' || status=='idle']", desc: 'OR filter' },
      { syntax: 'items[?tags[?@ == `critical`]]', desc: 'Filter by nested array contains' },
      { syntax: "items[?contains(name, 'spine')]", desc: 'Filter by substring' },
      { syntax: "items[?starts_with(name, 'dc1')]", desc: 'Filter by prefix' },
      { syntax: "items[?ends_with(name, '-01')]", desc: 'Filter by suffix' },
    ]},
    { category: 'Functions', items: [
      { syntax: 'length(items)', desc: 'Count elements' },
      { syntax: 'sort(items)', desc: 'Sort array' },
      { syntax: 'sort_by(items, &name)', desc: 'Sort by field' },
      { syntax: 'reverse(items)', desc: 'Reverse array' },
      { syntax: 'max_by(items, &speed)', desc: 'Item with max value' },
      { syntax: 'min_by(items, &speed)', desc: 'Item with min value' },
      { syntax: 'sum(items[*].count)', desc: 'Sum numeric values' },
      { syntax: "join(', ', items[*].name)", desc: 'Join strings' },
      { syntax: 'keys(@)', desc: 'Object keys as array' },
      { syntax: 'values(@)', desc: 'Object values as array' },
      { syntax: 'type(field)', desc: 'Type of value (string, number, etc.)' },
      { syntax: 'not_null(a, b, c)', desc: 'First non-null value' },
      { syntax: 'to_string(`42`)', desc: 'Convert to string' },
      { syntax: 'to_number(field)', desc: 'Convert to number' },
      { syntax: "contains('hello world', 'hello')", desc: 'String/array contains' },
    ]},
    { category: 'Multi-select', items: [
      { syntax: '[name, status, site]', desc: 'Pick multiple fields as array' },
      { syntax: '{n: name, s: status}', desc: 'Pick fields as object' },
      { syntax: '{total: length(items), first: items[0].name}', desc: 'Computed summary' },
    ]},
    { category: 'Pipe & Expressions', items: [
      { syntax: "items[?status=='up'] | length(@)", desc: 'Pipe: filter then count' },
      { syntax: "items[*].name | sort(@)", desc: 'Pipe: project then sort' },
      { syntax: "items | [0]", desc: 'Pipe: get first after expression' },
      { syntax: "items[*].speed | max(@)", desc: 'Pipe: max of projected values' },
    ]},
    { category: 'Network Automation Examples', items: [
      { syntax: "results[?status.value=='active']", desc: 'Netbox: active devices' },
      { syntax: "results[*].{name: name, site: site.display, role: role.display}", desc: 'Netbox: device summary' },
      { syntax: "results[?site.display=='Atlanta']", desc: 'Netbox: devices at site' },
      { syntax: "length(results[?status.value=='active'])", desc: 'Netbox: count active' },
      { syntax: "interfaces[?status=='down'].name", desc: 'Down interfaces' },
      { syntax: "bgp_neighbors[?state!='Established']", desc: 'BGP issues' },
      { syntax: "{up: length(interfaces[?status=='up']), down: length(interfaces[?status=='down'])}", desc: 'Interface status counts' },
    ]},
  ];

  function truncate(s: string): string {
    return s.length > 30 ? s.slice(0, 27) + '...' : s;
  }

  async function evaluate() {
    if (!expression.trim() || !inputData.trim()) {
      output = '';
      error = '';
      return;
    }
    try {
      const res = await fetch('/api/v1/transform/preview', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          input: JSON.parse(inputData),
          expression: expression,
          language: 'jmespath'
        })
      });
      const data = await res.json();
      if (res.ok) {
        output = JSON.stringify(data.result, null, 2);
        error = '';
      } else {
        output = '';
        error = data.error || 'Unknown error';
      }
    } catch (e: any) {
      output = '';
      error = e.message;
    }
  }

  function onExpressionChange() {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(evaluate, 300);
  }

  function onInputChange() {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => { evaluate(); }, 500);
  }
</script>

<div class="flex flex-col h-full gap-2">
  <div class="grid grid-cols-3 gap-3 flex-1 min-h-0">
    <!-- Left: input JSON -->
    <div class="flex flex-col min-h-0">
      <div class="flex items-center justify-between mb-1">
        <label for="tp-input-data" class="text-xs font-medium text-surface-600-400">Input data</label>
        <button
          type="button"
          onclick={() => { try { inputData = JSON.stringify(JSON.parse(inputData), null, 2); } catch { /* invalid JSON, leave as-is */ } }}
          class="text-xs text-primary-300 hover:text-primary-200 transition-colors"
        >Format</button>
      </div>
      <FieldHint id="transform.input_data" />
      <textarea
        id="tp-input-data"
        bind:value={inputData}
        oninput={onInputChange}
        class="flex-1 bg-surface-50-950 border border-surface-300-700 rounded-md p-2 font-mono text-xs text-success-300 resize-none focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30 min-h-0"
        spellcheck="false"
        placeholder={'{"key": "value"}'}
      ></textarea>
    </div>

    <!-- Center: expression + suggestions -->
    <div class="flex flex-col min-h-0">
      <label for="tp-expression" class="text-xs font-medium text-surface-600-400 mb-1 inline-flex items-center gap-1">JMESPath expression <FieldHint id="transform.expression" /></label>
      <textarea
        id="tp-expression"
        bind:value={expression}
        oninput={onExpressionChange}
        class="h-16 shrink-0 bg-surface-50-950 border border-surface-300-700 rounded-md p-2 font-mono text-sm text-warning-300 resize-none focus:outline-none focus:border-primary-500 focus:ring-2 focus:ring-primary-500/30"
        spellcheck="false"
        placeholder="items[?status=='active'].name"
      ></textarea>

      <div class="mt-2 flex-1 overflow-y-auto min-h-0">
        {#if smartExamples.length > 0}
          {@const groups = groupedExamples()}
          {#each Object.entries(groups) as [group, items]}
            <div class="mb-2">
              <p class="text-[10px] text-surface-500 uppercase tracking-wider mb-0.5">{group}</p>
              <div class="flex flex-wrap gap-1">
                {#each items as ex}
                  <button
                    type="button"
                    onclick={() => { expression = ex.expr; onExpressionChange(); }}
                    class="text-[11px] px-1.5 py-0.5 bg-surface-200-800 rounded text-surface-600-400 hover:text-warning-300 hover:bg-surface-300-700 font-mono truncate max-w-[180px] transition-colors"
                    title={ex.expr}
                  >
                    {ex.label}
                  </button>
                {/each}
              </div>
            </div>
          {/each}
        {:else}
          <p class="text-xs text-surface-500 italic">Paste input data to get suggestions</p>
        {/if}
      </div>

      {#if error}
        <div class="mt-1 p-2 bg-error-500/10 border border-error-500/30 rounded-md text-xs text-error-300 font-mono overflow-auto max-h-16 shrink-0">
          {error}
        </div>
      {/if}

      <div class="flex gap-2 mt-2 shrink-0">
        {#if onSave}
          <button
            type="button"
            onclick={() => onSave?.(expression)}
            class="h-7 px-3 inline-flex items-center bg-primary-500 hover:bg-primary-400 text-white rounded-md text-xs font-medium transition-colors"
          >Save expression</button>
        {/if}
        <button
          type="button"
          onclick={() => (showReference = !showReference)}
          class="h-7 px-3 inline-flex items-center bg-surface-200-800 hover:bg-surface-300-700 text-surface-700-300 rounded-md text-xs font-medium transition-colors"
        >{showReference ? 'Hide' : 'Show'} reference</button>
      </div>
    </div>

    <!-- Right: output -->
    <div class="flex flex-col min-h-0">
      <span class="text-xs font-medium text-surface-600-400 mb-1">Result</span>
      <pre class="flex-1 bg-surface-50-950 border border-surface-300-700 rounded-md p-2 font-mono text-xs text-primary-300 overflow-auto whitespace-pre-wrap min-h-0">{output || '(no result)'}</pre>
    </div>
  </div>

  {#if showReference}
    <div class="bg-surface-50-950 border border-surface-300-700 rounded-lg p-3 max-h-64 overflow-y-auto shrink-0">
      <div class="flex items-center justify-between mb-2">
        <h3 class="text-xs font-semibold text-surface-700-300 inline-flex items-center gap-1">JMESPath quick reference <FieldHint id="transform.reference" /></h3>
        <button type="button" onclick={() => (showReference = false)} class="text-xs text-surface-500 hover:text-surface-700-300 transition-colors">Close</button>
      </div>
      <div class="grid grid-cols-2 gap-x-6 gap-y-3">
        {#each reference as section}
          <div>
            <h4 class="text-[10px] font-bold text-warning-400 uppercase tracking-wider mb-1">{section.category}</h4>
            {#each section.items as item}
              <button
                type="button"
                class="flex gap-2 mb-0.5 group cursor-pointer text-left w-full"
                onclick={() => { expression = item.syntax; onExpressionChange(); }}
              >
                <code class="text-[11px] text-primary-300 font-mono group-hover:text-warning-300 shrink-0 max-w-[260px] truncate transition-colors" title={item.syntax}>{item.syntax}</code>
                <span class="text-[10px] text-surface-500 truncate">{item.desc}</span>
              </button>
            {/each}
          </div>
        {/each}
      </div>
    </div>
  {/if}
</div>
