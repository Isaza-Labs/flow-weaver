// Catches a <label> that silently binds to the wrong control.
//
// A <label> without `for` binds to its FIRST *labelable* descendant in tree
// order (HTML spec, "the label element"). Labelable includes <button> — not
// just form fields. So this markup:
//
//     <label>
//       Upload .md <FieldHint id="…" />        <!-- renders <button> -->
//       <input type="file" … />
//     </label>
//
// binds the label to FieldHint's info button, NOT the file input. Clicking
// "Upload .md" toggles the tooltip and the file picker never opens — which is
// exactly how it shipped on /integrations and /ai/specs, where it read as "the
// upload button does absolutely nothing".
//
// The failure is invisible to svelte-check and to type checking: the markup is
// valid, the handler is wired, and nothing throws. Only the association is
// wrong. Hence a static guard.
//
// The fix is an explicit `for`/`id` pair, which pins the labeled control
// regardless of DOM order. A bare reorder also "works" but is one careless edit
// away from breaking again.
//
// Run: npm run check:labels
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(fileURLToPath(new URL('.', import.meta.url)), '..');
const SRC = join(ROOT, 'src');

// Ratchet for pre-existing occurrences. Empty: the backlog this guard found
// when it was written has been cleared, so any finding is now a new one and
// fails the build. Add an entry only to quarantine something you cannot fix in
// the same change — never to silence a fresh mistake.
const KNOWN_PENDING = new Set([]);

// Components in this codebase that render a <button> as their root element.
// Extend when another one appears.
const BUTTON_COMPONENTS = ['FieldHint', 'IconButton'];

// The controls a label is normally meant to be for.
const CONTROL_TAGS = ['<input', '<select', '<textarea'];

function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (p.endsWith('.svelte')) out.push(p);
  }
  return out;
}

const problems = [];   // new occurrences — these fail the build
const pending = [];    // pre-existing, listed in KNOWN_PENDING

// <script> and <style> contents are not markup. Prose in a comment that
// happens to mention <label> and <button> — including the comments explaining
// this very rule — would otherwise register as a finding. Blanked rather than
// removed so reported line numbers still match the file.
function stripNonMarkup(src) {
  return src.replace(
    /<(script|style)\b[^>]*>[\s\S]*?<\/\1>/gi,
    (block) => block.replace(/[^\n]/g, ' '),
  );
}

for (const file of walk(SRC)) {
  const raw = readFileSync(file, 'utf8');
  const src = stripNonMarkup(raw);

  // Only for-less labels: an explicit `for` makes DOM order irrelevant.
  const labels = src.matchAll(/<label\b(?![^>]*\bfor=)[^>]*>(.*?)<\/label>/gs);

  for (const match of labels) {
    const body = match[1];

    const controlAt = CONTROL_TAGS
      .map((t) => body.indexOf(t))
      .filter((i) => i >= 0);
    if (controlAt.length === 0) continue;          // decorative label, no control
    const firstControl = Math.min(...controlAt);

    // `type="hidden"` inputs are not labelable, so they can't steal the
    // binding — but nothing else here is worth the extra parsing.
    const hijackers = [...BUTTON_COMPONENTS.map((c) => `<${c}`), '<button']
      .map((needle) => ({ needle, at: body.indexOf(needle) }))
      .filter(({ at }) => at >= 0 && at < firstControl);

    if (hijackers.length === 0) continue;

    const rel = relative(ROOT, file).split('\\').join('/');
    const line = src.slice(0, match.index).split('\n').length;
    const names = hijackers.map((h) => h.needle.slice(1)).join(', ');
    const entry =
      `${rel}:${line}\n`
      + `    ${names} appears before the control inside a <label> with no \`for\`.\n`
      + '    That element becomes the labeled control, so clicking the label does\n'
      + '    nothing useful. Add matching for="…" / id="…" to bind it explicitly.';

    (KNOWN_PENDING.has(rel) ? pending : problems).push(entry);
  }
}

if (pending.length > 0) {
  console.warn(
    `check-labels: ${pending.length} known-pending occurrence(s) in `
    + `${KNOWN_PENDING.size} file(s) — see KNOWN_PENDING in this script.`,
  );
}

if (problems.length > 0) {
  console.error(
    `\nMis-bound <label> elements, not previously known (${problems.length}):\n\n`
    + `${problems.join('\n\n')}\n`,
  );
  process.exit(1);
}

console.log('check-labels: no new mis-bound <label> elements.');
