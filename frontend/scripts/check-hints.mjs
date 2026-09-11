// Keeps the per-control help registry and the markup that consumes it in
// step, in both directions:
//
//   used but not declared  — the ⓘ renders nothing, so the control silently
//                            has no help and nobody notices until a user asks
//   declared but not used  — dead copy, which rots quietly and then reappears
//                            when someone reuses the key expecting it to match
//
// Also flags:
//   - declared entries whose text is obviously unfinished (empty label, or a
//     `help` shorter than a sentence), because a registry that type-checks is
//     not the same thing as a registry that says anything
//   - labelled form controls carrying no `help` key at all, which is the only
//     way to catch a whole view nobody wrote help for: without it the two
//     halves could agree perfectly and still cover a fraction of the UI
//
// Run: npm run check:hints
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('..', import.meta.url));
const SRC = join(ROOT, 'src');
const REGISTRY = join(SRC, 'lib', 'guides', 'fields.ts');

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) walk(full, out);
    else if (name.endsWith('.svelte')) out.push(full);
  }
  return out;
}

// --- declared keys -------------------------------------------------------
const registrySrc = readFileSync(REGISTRY, 'utf8');
const declared = new Map(); // key -> { label, help }

// Entries look like:  'devices.credential': { label: '…', help: '…', … },
const entryRe = /^ {2}'([^']+)': \{\n([\s\S]*?)^ {2}\},$/gm;
for (const m of registrySrc.matchAll(entryRe)) {
  const [, key, body] = m;
  const label = /label:\s*'((?:[^'\\]|\\.)*)'/.exec(body)?.[1] ?? '';
  const help = /help:\s*'((?:[^'\\]|\\.)*)'/.exec(body)?.[1] ?? '';
  declared.set(key, { label, help });
}

// --- used keys -----------------------------------------------------------
const used = new Map(); // key -> [files]
const dynamic = []; // help={expr} / id={expr} we cannot resolve statically

for (const file of walk(SRC)) {
  const src = readFileSync(file, 'utf8');
  const rel = relative(ROOT, file).replace(/\\/g, '/');

  for (const m of src.matchAll(/\bhelp="([^"]+)"/g)) {
    if (!used.has(m[1])) used.set(m[1], []);
    used.get(m[1]).push(rel);
  }
  for (const m of src.matchAll(/<FieldHint\b[^>]*\bid="([^"]+)"/g)) {
    if (!used.has(m[1])) used.set(m[1], []);
    used.get(m[1]).push(rel);
  }
  // Interpolated keys can't be verified here; surface them so they are a
  // deliberate choice rather than a silent hole in the check. The three ui/
  // form components are exempt: their `id={help}` is the plumbing that makes
  // every `help="…"` call site work.
  if (!rel.startsWith('src/lib/components/ui/')) {
    for (const m of src.matchAll(/<FieldHint\b[^>]*\bid=\{|(?<![\w-])help=\{/g)) {
      dynamic.push(`${rel}  (${m[0]}…)`);
    }
  }
}

// --- coverage ------------------------------------------------------------
// The other direction of completeness: a labelled form control with no `help`
// key is a control nobody wrote help for. Without this, the registry could
// agree perfectly with the markup and still cover a fraction of the UI.
const uncovered = [];
const CONTROL_RE = /<(Input|Select|Textarea|Checkbox)\b((?:[^>"]|"[^"]*")*?)\/?>/g;
for (const file of walk(SRC)) {
  const rel = relative(ROOT, file).replace(/\\/g, '/');
  // Doc pages render prose, not forms; ui/ components are the plumbing.
  if (rel.includes('/routes/docs/') || rel.startsWith('src/lib/components/ui/')) continue;
  const src = readFileSync(file, 'utf8');
  for (const m of src.matchAll(CONTROL_RE)) {
    const attrs = m[2];
    if (!attrs.includes('label') || attrs.includes('help=') || attrs.includes('labelHidden')) continue;
    const lab = /label=(?:"([^"]*)"|\{([^}]*)\})/.exec(attrs);
    const line = src.slice(0, m.index).split('\n').length;
    uncovered.push(`${rel}:${line}  ${(lab?.[1] ?? lab?.[2] ?? '?').trim().slice(0, 50)}`);
  }
}

// --- report --------------------------------------------------------------
const missing = [...used.keys()].filter((k) => !declared.has(k)).sort();
const orphaned = [...declared.keys()].filter((k) => !used.has(k)).sort();
const thin = [...declared.entries()]
  .filter(([, v]) => !v.label.trim() || v.help.trim().length < 25)
  .map(([k]) => k)
  .sort();

console.log(
  `declared: ${declared.size}   used: ${used.size}   ` +
    `labelled controls uncovered: ${uncovered.length}   files scanned: ${walk(SRC).length}`,
);

let failed = false;

if (missing.length) {
  failed = true;
  console.log(`\nUSED BUT NOT DECLARED (${missing.length}) — the ⓘ renders nothing:`);
  for (const k of missing) console.log(`  ${k}\n      ${[...new Set(used.get(k))].join(', ')}`);
}

if (orphaned.length) {
  failed = true;
  console.log(`\nDECLARED BUT NOT USED (${orphaned.length}) — dead copy:`);
  for (const k of orphaned) console.log(`  ${k}`);
}

if (thin.length) {
  failed = true;
  console.log(`\nUNFINISHED (${thin.length}) — empty label or help under 25 chars:`);
  for (const k of thin) console.log(`  ${k}`);
}

if (uncovered.length) {
  failed = true;
  console.log(`\nNO HELP KEY (${uncovered.length}) — labelled control with nothing written for it:`);
  for (const u of uncovered) console.log(`  ${u}`);
}

if (dynamic.length) {
  console.log(`\nNOT STATICALLY CHECKABLE (${dynamic.length}) — interpolated key:`);
  for (const d of dynamic) console.log(`  ${d}`);
}

if (!failed) console.log('\nOK — registry and markup agree.');
process.exit(failed ? 1 : 0);
