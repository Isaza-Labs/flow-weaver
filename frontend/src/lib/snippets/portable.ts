import type { Snippet } from '$lib/api/client';

// Portable snippet bundle: what /snippets exports and accepts back on import.
//
// Only the fields an author defines travel. Ids, timestamps, created_by and the
// derived run counters belong to the instance the snippet was exported FROM —
// carrying them would make an imported row claim a history it never had here.
// `verified` is left out for the same reason: it is a judgement about this
// instance's copy, not part of the snippet.

export const BUNDLE_FORMAT = 'flowweaver.snippets';
export const BUNDLE_VERSION = 1;

export const PORTABLE_FIELDS = [
  'name',
  'type',
  'description',
  'code',
  'script_language',
  'input_schema',
  'output_schema',
  'target_mode',
  'max_parallel',
  'timeout_seconds',
  'retry_policy',
  'logic_diagram_mermaid',
  'idempotency',
  // Carried deliberately: for a python_snippet, null means "the author has not
  // said" and every step using the imported copy fails until a node declares it.
  'changes_state',
  'network_enabled',
] as const;

export type PortableSnippet = Pick<Snippet, 'name' | 'type'> &
  Partial<Pick<Snippet, (typeof PORTABLE_FIELDS)[number]>>;

export interface SnippetBundle {
  format: typeof BUNDLE_FORMAT;
  version: number;
  exported_at: string;
  snippets: PortableSnippet[];
}

export function toPortable(s: Snippet): PortableSnippet {
  const out: Record<string, unknown> = {};
  for (const k of PORTABLE_FIELDS) {
    const v = s[k];
    if (v !== undefined) out[k] = v;
  }
  return out as PortableSnippet;
}

export function buildBundle(list: Snippet[], now = new Date()): SnippetBundle {
  return {
    format: BUNDLE_FORMAT,
    version: BUNDLE_VERSION,
    exported_at: now.toISOString(),
    snippets: list.map(toPortable),
  };
}

export function bundleFileName(list: Snippet[], now = new Date()): string {
  const stamp = now.toISOString().slice(0, 10);
  if (list.length === 1) {
    const slug = list[0].name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'snippet';
    return `snippet-${slug}-${stamp}.json`;
  }
  return `snippets-${stamp}.json`;
}

export function downloadBundle(list: Snippet[]) {
  const blob = new Blob([JSON.stringify(buildBundle(list), null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = bundleFileName(list);
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

export interface ParseResult {
  snippets: PortableSnippet[];
  // Per-entry problems, by 1-based position in the file. An entry listed here is
  // NOT in `snippets`; the rest can still be imported.
  problems: string[];
}

// Accepts a bundle (`{ format, snippets: [...] }`), a bare array, or a single
// snippet object — the last two so a hand-written file or a copy of one row's
// GET response imports without ceremony. Throws only when the text as a whole
// is unusable; per-entry problems come back in `problems`.
export function parseBundle(text: string): ParseResult {
  let doc: unknown;
  try {
    doc = JSON.parse(text);
  } catch (e) {
    throw new Error(`Not valid JSON: ${(e as Error).message}`);
  }

  let entries: unknown[];
  if (Array.isArray(doc)) {
    entries = doc;
  } else if (isObject(doc) && Array.isArray(doc.snippets)) {
    if (doc.format !== undefined && doc.format !== BUNDLE_FORMAT) {
      throw new Error(`Unknown bundle format "${String(doc.format)}" (expected "${BUNDLE_FORMAT}").`);
    }
    if (typeof doc.version === 'number' && doc.version > BUNDLE_VERSION) {
      throw new Error(`Bundle version ${doc.version} is newer than this FlowWeaver understands (${BUNDLE_VERSION}).`);
    }
    entries = doc.snippets;
  } else if (isObject(doc) && 'name' in doc && 'type' in doc) {
    entries = [doc];
  } else {
    throw new Error('Expected a snippet bundle, an array of snippets, or a single snippet object.');
  }

  const snippets: PortableSnippet[] = [];
  const problems: string[] = [];
  entries.forEach((raw, i) => {
    const pos = i + 1;
    if (!isObject(raw)) {
      problems.push(`#${pos}: not an object`);
      return;
    }
    const name = typeof raw.name === 'string' ? raw.name.trim() : '';
    const type = typeof raw.type === 'string' ? raw.type.trim() : '';
    if (!name || !type) {
      problems.push(`#${pos}${name ? ` "${name}"` : ''}: missing ${!name ? 'name' : 'type'}`);
      return;
    }
    const out: Record<string, unknown> = {};
    for (const k of PORTABLE_FIELDS) {
      if (raw[k] !== undefined) out[k] = raw[k];
    }
    out.name = name;
    out.type = type;
    snippets.push(out as PortableSnippet);
  });

  if (entries.length === 0) throw new Error('The file contains no snippets.');
  return { snippets, problems };
}

// Picks a free name for an imported snippet whose name is already taken.
// Same shape as the list page's "(copy)" naming so the two read alike.
export function importedName(base: string, taken: Set<string>): string {
  let candidate = `${base} (imported)`;
  for (let n = 2; taken.has(candidate); n++) candidate = `${base} (imported ${n})`;
  return candidate;
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}
