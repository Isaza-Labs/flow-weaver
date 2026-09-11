// Client-side mirror of flow_weaver_backend/Services/Engine/VariableResolver.cs
// grammar. We use it for two things:
//
//   1. Parse — the variable picker and inline-help components need to know
//      which `{{ … }}` tokens a string contains and where each one starts
//      so they can highlight or insert at the right cursor position.
//
//   2. Lint — before saving a node config, we walk every string in the
//      payload and warn about references that obviously won't resolve at
//      runtime (unknown step id, unknown filter name). The backend's
//      `FindUnresolvedTemplates` catches these too, but design-time
//      feedback shaves off the round trip and avoids a failed step run.
//
// Stay in lock-step with the C# regex/parser. If you change the grammar
// here, change it there too — the integration test suite expects matching
// output for the same input.

export type TemplateNamespace = 'steps' | 'device' | 'input' | 'run';

export interface FilterCall {
  name: string;
  args: string[];
}

export interface TemplateRef {
  ns: TemplateNamespace;
  // Only set when ns === 'steps' (the upstream node id, possibly hyphenated).
  stepId?: string;
  // Path expression after the namespace prefix, e.g. `.output.foo`,
  // `.devices_by_name['router-1'].output.stdout`, or '' for a bare
  // `{{ device }}` / `{{ input }}` reference.
  path: string;
  filters: FilterCall[];
  // Range in the source string: [start, end) — useful for highlighting
  // or for splicing in a replacement (variable picker insertion).
  start: number;
  end: number;
  // Verbatim original text (what's between `{{` and `}}` plus the braces).
  raw: string;
}

const KNOWN_FILTERS = new Set([
  'trim',
  'upper',
  'lower',
  'truncate',
  'json',
  'default',
  'strip_ansi',
  'strip',
]);

// Same patterns as VariableResolver.cs (StepsRegex, DeviceRegex,
// InputRegex, RunRegex). The `[^}]*?` non-greedy capture lets the body
// include internal whitespace and `|` for filters.
const RX_STEPS = /\{\{\s*steps\.([\w-]+)\.output([^}]*?)\s*\}\}/g;
const RX_DEVICE = /\{\{\s*device(\b[^}]*?)\s*\}\}/g;
const RX_INPUT = /\{\{\s*input(\b[^}]*?)\s*\}\}/g;
const RX_RUN = /\{\{\s*run(\b[^}]*?)\s*\}\}/g;

// Residual scan — anything that *looks* like a template but didn't match
// one of the three above. Catches typos like `{{ step.x.output.y }}`
// (missing `s`) or unsupported namespaces.
const RX_RESIDUAL = /\{\{\s*[^}]+\s*\}\}/g;

export function parseTemplates(text: string): TemplateRef[] {
  if (!text) return [];
  const out: TemplateRef[] = [];

  for (const m of text.matchAll(RX_STEPS)) {
    const expr = parseExpression(m[2] ?? '');
    out.push({
      ns: 'steps',
      stepId: m[1],
      path: expr.path,
      filters: expr.filters,
      start: m.index ?? 0,
      end: (m.index ?? 0) + m[0].length,
      raw: m[0],
    });
  }
  for (const m of text.matchAll(RX_DEVICE)) {
    const expr = parseExpression(m[1] ?? '');
    out.push({
      ns: 'device',
      path: expr.path,
      filters: expr.filters,
      start: m.index ?? 0,
      end: (m.index ?? 0) + m[0].length,
      raw: m[0],
    });
  }
  for (const m of text.matchAll(RX_INPUT)) {
    const expr = parseExpression(m[1] ?? '');
    out.push({
      ns: 'input',
      path: expr.path,
      filters: expr.filters,
      start: m.index ?? 0,
      end: (m.index ?? 0) + m[0].length,
      raw: m[0],
    });
  }
  for (const m of text.matchAll(RX_RUN)) {
    const expr = parseExpression(m[1] ?? '');
    out.push({
      ns: 'run',
      path: expr.path,
      filters: expr.filters,
      start: m.index ?? 0,
      end: (m.index ?? 0) + m[0].length,
      raw: m[0],
    });
  }

  return out.sort((a, b) => a.start - b.start);
}

interface ParsedExpression {
  path: string;
  filters: FilterCall[];
}

function parseExpression(raw: string): ParsedExpression {
  if (!raw) return { path: '', filters: [] };
  // Split on top-level `|` (not inside quotes or parens).
  const parts: string[] = [];
  let buf = '';
  let quote = '';
  let paren = 0;
  for (const c of raw) {
    if (quote) {
      buf += c;
      if (c === quote) quote = '';
      continue;
    }
    if (c === '\'' || c === '"') { quote = c; buf += c; continue; }
    if (c === '(') { paren++; buf += c; continue; }
    if (c === ')') { if (paren > 0) paren--; buf += c; continue; }
    if (c === '|' && paren === 0) { parts.push(buf); buf = ''; continue; }
    buf += c;
  }
  parts.push(buf);

  const path = parts[0]?.trim() ?? '';
  const filters: FilterCall[] = [];
  for (let i = 1; i < parts.length; i++) {
    const tok = parts[i].trim();
    if (!tok) continue;
    const lp = tok.indexOf('(');
    if (lp < 0) {
      filters.push({ name: tok, args: [] });
      continue;
    }
    const rp = tok.lastIndexOf(')');
    if (rp <= lp) {
      filters.push({ name: tok, args: [] });
      continue;
    }
    filters.push({
      name: tok.slice(0, lp).trim(),
      args: splitArgs(tok.slice(lp + 1, rp)),
    });
  }
  return { path, filters };
}

function splitArgs(raw: string): string[] {
  const parts: string[] = [];
  let buf = '';
  let quote = '';
  for (const c of raw) {
    if (quote) {
      buf += c;
      if (c === quote) quote = '';
      continue;
    }
    if (c === '\'' || c === '"') { quote = c; buf += c; continue; }
    if (c === ',') { parts.push(unquote(buf.trim())); buf = ''; continue; }
    buf += c;
  }
  const last = buf.trim();
  if (last.length || parts.length) parts.push(unquote(last));
  return parts;
}

function unquote(s: string): string {
  if (s.length >= 2 && ((s[0] === '\'' && s[s.length - 1] === '\'')
    || (s[0] === '"' && s[s.length - 1] === '"'))) {
    return s.slice(1, -1);
  }
  return s;
}

// ── Linter ────────────────────────────────────────────────────────

export type LintSeverity = 'error' | 'warning';

export interface LintIssue {
  severity: LintSeverity;
  message: string;
  template: TemplateRef;
}

export interface LintContext {
  // Upstream node ids reachable from the node being edited (output of
  // DAG ancestor walk, including transitively-reachable nodes).
  upstreamNodeIds: string[];
  // Optional: known top-level fields for each upstream node's output. If
  // a path's first segment isn't here, the linter warns (advisory only —
  // schemas are not exhaustive).
  upstreamOutputFields?: Record<string, string[]>;
  // Whether the current target node has a single device context (per_device
  // mode) — controls whether `{{ device.X }}` references are valid.
  hasDeviceContext?: boolean;
  // Whether the workflow declares an input schema with at least one field.
  hasInputContext?: boolean;
}

export function lintText(text: string, ctx: LintContext): LintIssue[] {
  if (!text) return [];
  const issues: LintIssue[] = [];
  const refs = parseTemplates(text);

  // Detect residual templates (typos / unsupported namespaces).
  const matched = new Set(refs.map((r) => `${r.start}:${r.end}`));
  for (const m of text.matchAll(RX_RESIDUAL)) {
    const key = `${m.index ?? 0}:${(m.index ?? 0) + m[0].length}`;
    if (matched.has(key)) continue;
    issues.push({
      severity: 'error',
      message: `Unrecognised template — only steps.<id>.output, device, input, and run are supported`,
      template: {
        ns: 'steps',
        path: '',
        filters: [],
        start: m.index ?? 0,
        end: (m.index ?? 0) + m[0].length,
        raw: m[0],
      },
    });
  }

  for (const ref of refs) {
    if (ref.ns === 'steps') {
      if (!ref.stepId) continue;
      if (!ctx.upstreamNodeIds.includes(ref.stepId)) {
        issues.push({
          severity: 'error',
          message: `Step '${ref.stepId}' is not an upstream node — referenced step must complete before this one runs.`,
          template: ref,
        });
        continue;
      }
      const fields = ctx.upstreamOutputFields?.[ref.stepId];
      if (fields && ref.path) {
        const first = firstPathSegment(ref.path);
        if (first && !fields.includes(first)) {
          issues.push({
            severity: 'warning',
            message: `'${first}' is not declared in ${ref.stepId}.output_schema (advisory — snippets may emit undeclared fields).`,
            template: ref,
          });
        }
      }
    } else if (ref.ns === 'device' && ctx.hasDeviceContext === false) {
      issues.push({
        severity: 'warning',
        message: `{{ device.* }} won't resolve — this node runs in once mode with multiple targets and has no single device context.`,
        template: ref,
      });
    } else if (ref.ns === 'input' && ctx.hasInputContext === false) {
      issues.push({
        severity: 'warning',
        message: `{{ input.* }} won't resolve — workflow has no declared input schema.`,
        template: ref,
      });
    }

    for (const f of ref.filters) {
      if (!KNOWN_FILTERS.has(f.name.toLowerCase())) {
        issues.push({
          severity: 'warning',
          message: `Unknown filter '${f.name}' — supported: ${[...KNOWN_FILTERS].sort().join(', ')}.`,
          template: ref,
        });
      }
    }
  }

  return issues.sort((a, b) => a.template.start - b.template.start);
}

function firstPathSegment(path: string): string | null {
  // Path starts with `.foo` or `[…]`. Strip a leading `.output` to get
  // the next segment, e.g. `.output.devices[0]` → `devices`.
  const trimmed = path.replace(/^\.output/, '');
  const m = trimmed.match(/^\.([\w-]+)/);
  return m?.[1] ?? null;
}

// ── Walker for nested structures ──────────────────────────────────

// Finds every template inside a JSON-like value (string / array / object)
// and returns flat list with dot-path locations. Used by the dialog to
// lint an entire `config_overrides` blob in one pass.
export function lintConfig(
  config: unknown,
  ctx: LintContext,
  path = '$',
): Array<{ location: string; issue: LintIssue }> {
  const out: Array<{ location: string; issue: LintIssue }> = [];
  walk(config, path, (text, location) => {
    for (const issue of lintText(text, ctx)) {
      out.push({ location, issue });
    }
  });
  return out;
}

function walk(node: unknown, path: string, onString: (s: string, p: string) => void): void {
  if (node == null) return;
  if (typeof node === 'string') { onString(node, path); return; }
  if (Array.isArray(node)) {
    node.forEach((v, i) => walk(v, `${path}[${i}]`, onString));
    return;
  }
  if (typeof node === 'object') {
    for (const [k, v] of Object.entries(node as Record<string, unknown>)) {
      walk(v, `${path}.${k}`, onString);
    }
  }
}

// ── Helpers exposed for the picker ────────────────────────────────

// All filters the resolver supports, in display order.
export const SUPPORTED_FILTERS: FilterCall[] = [
  { name: 'trim', args: [] },
  { name: 'default', args: ['-'] },
  { name: 'truncate', args: ['80'] },
  { name: 'json', args: [] },
  { name: 'upper', args: [] },
  { name: 'lower', args: [] },
  { name: 'strip_ansi', args: [] },
  { name: 'strip', args: [] },
];

// Renders a `{{ … }}` string from a parsed template. Inverse of
// `parseTemplates` — used when the picker assembles a reference from
// user choices.
export function renderTemplate(ref: Omit<TemplateRef, 'start' | 'end' | 'raw'>): string {
  const ns = ref.ns === 'steps' ? `steps.${ref.stepId ?? '<id>'}.output` : ref.ns;
  const filters = ref.filters
    .map((f) => f.args.length > 0 ? `${f.name}(${f.args.map(quoteArg).join(', ')})` : f.name)
    .join(' | ');
  const tail = filters ? ` | ${filters}` : '';
  return `{{ ${ns}${ref.path}${tail} }}`;
}

function quoteArg(s: string): string {
  if (s === '' || /^-?\d+(\.\d+)?$/.test(s)) return s;
  return `'${s.replace(/'/g, "\\'")}'`;
}
