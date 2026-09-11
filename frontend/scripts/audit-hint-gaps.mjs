// Finds help gaps the main check:hints gate cannot see.
//
// check:hints only knows about <Input|Select|Textarea|Checkbox>, because those
// take a `help` prop. That left three whole classes of option uncovered while
// the gate still reported zero:
//
//   1. raw <input> / <select> / <textarea> with hand-rolled labels
//   2. section headings (<h2>/<h3>) that title a group of options
//   3. action buttons that DO something rather than hold a value
//
// This is a reporting tool, not a gate: proximity to a <FieldHint> is a
// heuristic, so it guides the sweep instead of failing the build.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('..', import.meta.url));
const SRC = join(ROOT, 'src');
const NEAR = 14; // lines to look around a control for an existing hint

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) walk(full, out);
    else if (name.endsWith('.svelte')) out.push(full);
  }
  return out;
}

// Deliberate exclusions, with the reason recorded so the residue is a decision
// rather than something nobody got round to. Anything not listed here is a gap.
const EXCLUDED = [
  // Controls built at runtime from a schema: the label and the description both
  // come from that schema, so a static registry key cannot describe a field
  // that does not exist until the schema is loaded.
  ['src/lib/components/JsonSchemaForm.svelte', 'renders controls from a JSON Schema at runtime'],
  ['src/lib/components/KeyValueEditor.svelte', 'renders one row per user-defined key'],
  ['src/lib/components/ParamsEditor.svelte', 'renders controls from a JSON Schema at runtime'],
  // Sub-fields of a group that already carries the hint, with labels that say
  // the whole thing ("Minute of hour", "Day of week"). A hint per spinner would
  // be the padding the thin-entry rule exists to prevent.
  ['src/lib/components/ScheduleForm.svelte', 'sub-fields of the hinted Repeat picker'],
  // Repeated per-row controls inside a picker that is hinted as a whole.
  ['src/lib/components/DevicePicker.svelte', 'per-row checkboxes inside a hinted picker'],
  ['src/lib/components/admin/FileUploadManager.svelte', 'file input under a hinted label'],
  // Navigation and chat affordances, not configuration.
  ['src/lib/components/CommandPalette.svelte', 'navigation search, not an option'],
  ['src/lib/components/GuideMascot.svelte', 'free-text question box'],
  ['src/routes/login/+page.svelte', 'page title, not an option'],
];

const only = process.argv[2]; // optional path filter, e.g. "integrations"
const rows = [];
const excluded = [];

for (const file of walk(SRC)) {
  const rel = relative(ROOT, file).replace(/\\/g, '/');
  if (rel.includes('/routes/docs/') || rel.startsWith('src/lib/components/ui/')) continue;
  if (only && !rel.includes(only)) continue;

  const skip = EXCLUDED.find(([f]) => f === rel);
  if (skip) {
    excluded.push(`${rel}  — ${skip[1]}`);
    continue;
  }

  const lines = readFileSync(file, 'utf8').split('\n');
  const hinted = (i) =>
    lines.slice(Math.max(0, i - NEAR), i + NEAR).some((l) => l.includes('FieldHint'));

  lines.forEach((line, i) => {
    const t = line.trim();
    // A comment that merely mentions <select> is not a control.
    if (t.startsWith('//') || t.startsWith('*') || t.startsWith('<!--')) return;

    let kind = null;

    if (/<(input|select|textarea)\b/.test(t) && !/type="hidden"/.test(t)) kind = 'raw-control';
    else if (/^<h[23]\b/.test(t) || /<h[23] class=/.test(t)) kind = 'section';

    // A heading whose whole text is interpolated ({month}, {formatted}) is a
    // live label for what is on screen, not a named thing to explain.
    if (kind === 'section' && /">\{/.test(t)) return;

    if (kind && !hinted(i)) {
      rows.push({ rel, line: i + 1, kind, text: t.slice(0, 76) });
    }
  });
}

const byKind = rows.reduce((a, r) => ((a[r.kind] = (a[r.kind] ?? 0) + 1), a), {});
console.log('gaps:', rows.length, byKind);
if (excluded.length) {
  console.log(`\nexcluded by design (${excluded.length}):`);
  for (const e of [...new Set(excluded)]) console.log(`  ${e}`);
}
let last = '';
for (const r of rows) {
  if (r.rel !== last) {
    console.log(`\n${r.rel}`);
    last = r.rel;
  }
  console.log(`  ${String(r.line).padStart(4)}  ${r.kind.padEnd(11)} ${r.text}`);
}
