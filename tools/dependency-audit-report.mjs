#!/usr/bin/env node
// Turns `npm audit --json` and `dotnet list package --vulnerable --format json` output into one
// Markdown report, and fails when an advisory at or above a severity threshold is present.
//
// Input:   --npm <file>     output of `npm audit --json` (run in frontend/)
//          --nuget <file>   output of `dotnet list <project> package --vulnerable --include-transitive --format json`
//                           (repeatable, one per project)
//          --fail-on <low|moderate|high|critical>   default: high
//          --out <file>     where to write the Markdown report (default: stdout only)
// Output:  the report on stdout (and in --out); exit 1 when the threshold is met, 2 on bad input.
// Read-only; no network access of its own. Platforms: any with Node 18+.
import { readFileSync, writeFileSync } from 'node:fs';

const order = ['info', 'low', 'moderate', 'high', 'critical'];
const args = process.argv.slice(2);
const opt = { npm: null, nuget: [], failOn: 'high', out: null };
for (let i = 0; i < args.length; i++) {
  const a = args[i];
  if (a === '--npm') opt.npm = args[++i];
  else if (a === '--nuget') opt.nuget.push(args[++i]);
  else if (a === '--fail-on') opt.failOn = args[++i];
  else if (a === '--out') opt.out = args[++i];
  else {
    console.error(`unknown argument: ${a}`);
    process.exit(2);
  }
}
if (!order.includes(opt.failOn)) {
  console.error(`--fail-on must be one of ${order.join(', ')}`);
  process.exit(2);
}

const rows = []; // { ecosystem, name, version, severity, origin, advisory }

if (opt.npm) {
  const audit = JSON.parse(readFileSync(opt.npm, 'utf8'));
  for (const [name, v] of Object.entries(audit.vulnerabilities ?? {})) {
    const via = v.via.filter((x) => typeof x === 'object');
    const parents = v.via.filter((x) => typeof x === 'string');
    const origin = v.isDirect ? 'direct' : `via ${(v.effects?.length ? v.effects : parents).join(', ') || 'transitive'}`;
    if (via.length === 0) {
      rows.push({ ecosystem: 'npm', name, version: v.range, severity: v.severity, origin, advisory: `through ${parents.join(', ')}` });
    }
    for (const adv of via) {
      rows.push({ ecosystem: 'npm', name, version: v.range, severity: adv.severity ?? v.severity, origin, advisory: `[${adv.url.split('/').pop()}](${adv.url}) ${adv.title}` });
    }
  }
}

for (const file of opt.nuget) {
  const report = JSON.parse(readFileSync(file, 'utf8'));
  for (const project of report.projects ?? []) {
    for (const fw of project.frameworks ?? []) {
      for (const [kind, list] of [['direct', fw.topLevelPackages], ['transitive', fw.transitivePackages]]) {
        for (const pkg of list ?? []) {
          for (const vuln of pkg.vulnerabilities ?? []) {
            rows.push({
              ecosystem: 'nuget',
              name: pkg.id,
              version: pkg.resolvedVersion,
              severity: vuln.severity.toLowerCase(),
              origin: `${kind} in ${project.path.split(/[\\/]/).pop()}`,
              advisory: `[${vuln.advisoryurl.split('/').pop()}](${vuln.advisoryurl})`,
            });
          }
        }
      }
    }
  }
}

rows.sort((a, b) => order.indexOf(b.severity) - order.indexOf(a.severity) || a.name.localeCompare(b.name));
const counts = Object.fromEntries(order.map((s) => [s, rows.filter((r) => r.severity === s).length]));
const failing = rows.filter((r) => order.indexOf(r.severity) >= order.indexOf(opt.failOn));

const lines = [
  '## Dependency audit',
  '',
  `Advisory entries: ${rows.length} (critical ${counts.critical}, high ${counts.high}, moderate ${counts.moderate}, low ${counts.low}). Threshold: ${opt.failOn}; ${failing.length} at or above it.`,
  '',
];
if (rows.length > 0) {
  const cell = (text) => String(text).replace(/\|/g, '\\|');
  lines.push('| Ecosystem | Package | Version (npm: affected range) | Severity | Origin | Advisory |', '|---|---|---|---|---|---|');
  for (const r of rows) lines.push(`| ${r.ecosystem} | ${cell(r.name)} | ${cell(r.version)} | ${r.severity} | ${cell(r.origin)} | ${cell(r.advisory)} |`);
  lines.push('');
}
lines.push('Handling: see docs/repository/CI_GATES.md, "Dependency advisories".');
const md = lines.join('\n') + '\n';

process.stdout.write(md);
if (opt.out) writeFileSync(opt.out, md);
process.exit(failing.length > 0 ? 1 : 0);
