#!/usr/bin/env node
// Checks every relative link in the repository's tracked Markdown files.
//
// For each link, image or reference definition that points inside the repository, the target
// must exist with EXACTLY the spelling used in the link. The comparison is done segment by
// segment against the directory listing, so `./Deploy/README.md` fails even on Windows and
// macOS, whose filesystems would resolve it and hide a link that is broken on GitHub and Linux.
//
// External links (http, https, mailto and other schemes) are NOT fetched: a third-party site
// that is briefly down must not block a pull request. Anchors (`#section`) are not checked.
//
// Input:   tracked and untracked, non-ignored Markdown files (`git ls-files --cached --others`), read-only.
// Output:  one line per broken link (`file:line  target  reason`); exit 1 if any, else 0.
// Usage:   node tools/check-doc-links.mjs        (from anywhere inside the repository)
// Platforms: any with Node 18+ and git on PATH.
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, posix } from 'node:path';

const root = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
const files = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard', '--', '*.md'], { cwd: root, encoding: 'utf8' })
  .split('\0')
  .filter(Boolean)
  // A tracked file deleted in a local working tree is not a link source any more.
  .filter((f) => existsSync(join(root, f)));

const listings = new Map();
function entries(dir) {
  if (!listings.has(dir)) {
    listings.set(dir, existsSync(dir) && statSync(dir).isDirectory() ? new Set(readdirSync(dir)) : null);
  }
  return listings.get(dir);
}

// Returns null when the path exists with this exact spelling, otherwise the reason it does not.
function check(relativeToRoot) {
  let dir = root;
  for (const segment of relativeToRoot.split('/').filter((s) => s && s !== '.')) {
    const names = entries(dir);
    if (!names) return `'${posix.relative('', dir.slice(root.length + 1)) || '.'}' is not a directory`;
    if (!names.has(segment)) {
      const lower = segment.toLowerCase();
      const near = [...names].find((n) => n.toLowerCase() === lower);
      return near ? `case mismatch: '${segment}' is '${near}' on disk` : `'${segment}' not found`;
    }
    dir = join(dir, segment);
  }
  return null;
}

const linkPatterns = [
  /!?\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+["'][^)]*["'])?\s*\)/g, // [text](target "title")
  /^\s{0,3}\[[^\]]+\]:\s*<?(\S+?)>?(?:\s|$)/g, //                    [ref]: target
  /\b(?:src|href)\s*=\s*["']([^"']+)["']/g, //                          <img src="target">
];

const problems = [];
for (const file of files) {
  const lines = readFileSync(join(root, file), 'utf8').split(/\r?\n/);
  let fence = null;
  lines.forEach((raw, index) => {
    const marker = raw.match(/^\s*(`{3,}|~{3,})/);
    if (marker) {
      if (!fence) fence = marker[1][0];
      else if (marker[1][0] === fence) fence = null;
      return;
    }
    if (fence) return;
    const line = raw.replace(/`[^`]*`/g, ''); // inline code is example text, not a link

    for (const pattern of linkPatterns) {
      pattern.lastIndex = 0;
      for (const match of line.matchAll(pattern)) {
        const target = match[1];
        if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith('//') || target.startsWith('#')) continue;
        let path = target.split('#')[0].split('?')[0];
        try {
          path = decodeURIComponent(path);
        } catch {
          // keep the raw spelling; the existence check reports it
        }
        if (!path) continue;
        const resolved = path.startsWith('/')
          ? posix.normalize(path.slice(1))
          : posix.normalize(posix.join(dirname(file).replace(/\\/g, '/'), path));
        if (resolved.startsWith('..')) {
          problems.push(`${file}:${index + 1}  ${target}  points outside the repository`);
          continue;
        }
        const reason = check(resolved);
        if (reason) problems.push(`${file}:${index + 1}  ${target}  ${reason}`);
      }
    }
  });
}

if (problems.length > 0) {
  console.error(`check-doc-links: ${problems.length} broken relative link(s) in ${files.length} Markdown files\n`);
  for (const p of problems) console.error(`  ${p}`);
  process.exit(1);
}
console.log(`check-doc-links: ${files.length} Markdown files, no broken relative links.`);
