import type { Workflow } from '$lib/api/client';

// Mirrors the engine's InputRegex (backend VariableResolver.cs): matches
// `{{ input<path> }}`. The `\b` after `input` stops `{{ inputs }}` /
// `{{ input_date }}` from matching. Group 1 is the path — ".email",
// ".devices_list[0].id", "['pass']", "" (whole object), or " | filter".
const INPUT_REF = /\{\{\s*input\b([^}]*?)\s*\}\}/g;

// Reduce a captured path to its top-level input key, plus whether it was
// indexed (a hint the value is an array). Returns null for `{{ input }}`
// (whole object), `{{ input | to_json }}`, or `{{ input[0] }}` (input itself
// is an array) — none of those name a single field.
function topLevelInputKey(rawPath: string): { key: string; isArray: boolean } | null {
  let p = rawPath.trim();
  if (p.startsWith('.')) p = p.slice(1);
  p = p.trimStart();
  if (!p || p.startsWith('|')) return null;
  if (p.startsWith('[')) {
    const bracket = p.match(/^\[\s*(['"])(.*?)\1\s*\](\s*\[)?/);
    return bracket ? { key: bracket[2], isArray: !!bracket[3] } : null;
  }
  // Stop the key at the first `.`, `[`, whitespace, or `|` (filter pipe).
  const dotted = p.match(/^([^.[\s|]+)(\[)?/);
  return dotted ? { key: dotted[1], isArray: !!dotted[2] } : null;
}

// Recursively walk any config value (string / array / object) and record every
// {{ input.X }} top-level key it references. `acc` maps key → isArray.
function collect(value: unknown, acc: Map<string, boolean>): void {
  if (typeof value === 'string') {
    INPUT_REF.lastIndex = 0;
    let m: RegExpExecArray | null;
    while ((m = INPUT_REF.exec(value)) !== null) {
      const hit = topLevelInputKey(m[1]);
      if (hit) acc.set(hit.key, (acc.get(hit.key) ?? false) || hit.isArray);
    }
  } else if (Array.isArray(value)) {
    for (const item of value) collect(item, acc);
  } else if (value && typeof value === 'object') {
    for (const v of Object.values(value)) collect(v, acc);
  }
}

// Scan a workflow's node `config_overrides` for {{ input.X }} references and
// synthesize a minimal JSON Schema (one property per referenced variable) so a
// run/schedule form can render a labelled field per input the workflow actually
// asks for. A reference carries no type, so each field is a string unless it
// was indexed (`input.foo[0]` → array). Returns {} when nothing is referenced.
export function deriveInputSchemaFromWorkflow(
  workflow: Pick<Workflow, 'nodes'> | null | undefined,
): Record<string, unknown> {
  const found = new Map<string, boolean>();
  for (const node of workflow?.nodes ?? []) {
    collect(node?.config_overrides ?? {}, found);
  }
  if (found.size === 0) return {};

  const properties: Record<string, { type: string; title: string }> = {};
  const order: string[] = [];
  for (const [key, isArray] of found) {
    properties[key] = { type: isArray ? 'array' : 'string', title: key };
    order.push(key);
  }
  return { type: 'object', properties, 'x-order': order };
}

// The schema that should drive an input form for a workflow: the explicitly
// declared `input_schema` when it has properties, otherwise one derived from
// the DAG's {{ input.X }} references. Authored schemas win; derivation covers
// the common case where none was declared (there is no UI to author one).
export function effectiveInputSchema(
  workflow: Workflow | null | undefined,
): Record<string, unknown> {
  const declared = workflow?.input_schema;
  const props =
    declared && typeof declared === 'object'
      ? (declared.properties as Record<string, unknown> | undefined)
      : undefined;
  if (props && Object.keys(props).length > 0) return declared as Record<string, unknown>;
  return deriveInputSchemaFromWorkflow(workflow);
}
