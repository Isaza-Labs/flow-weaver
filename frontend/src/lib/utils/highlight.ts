// Lightweight code highlighter wrapping highlight.js's core build with a
// curated set of languages — enough to cover what shows up in registered
// Git repos (network configs, IaC, scripts, app code) without pulling in
// hljs's full ~1MB language bundle.

import hljs from 'highlight.js/lib/core';
import bash from 'highlight.js/lib/languages/bash';
import csharp from 'highlight.js/lib/languages/csharp';
import css from 'highlight.js/lib/languages/css';
import diff from 'highlight.js/lib/languages/diff';
import dockerfile from 'highlight.js/lib/languages/dockerfile';
import go from 'highlight.js/lib/languages/go';
import ini from 'highlight.js/lib/languages/ini';
import java from 'highlight.js/lib/languages/java';
import javascript from 'highlight.js/lib/languages/javascript';
import json from 'highlight.js/lib/languages/json';
import markdown from 'highlight.js/lib/languages/markdown';
import nginx from 'highlight.js/lib/languages/nginx';
import plaintext from 'highlight.js/lib/languages/plaintext';
import powershell from 'highlight.js/lib/languages/powershell';
import python from 'highlight.js/lib/languages/python';
import ruby from 'highlight.js/lib/languages/ruby';
import rust from 'highlight.js/lib/languages/rust';
import shell from 'highlight.js/lib/languages/shell';
import sql from 'highlight.js/lib/languages/sql';
import typescript from 'highlight.js/lib/languages/typescript';
import xml from 'highlight.js/lib/languages/xml';
import yaml from 'highlight.js/lib/languages/yaml';

const REGISTRATIONS: Record<string, unknown> = {
  bash, csharp, css, diff, dockerfile, go, ini, java, javascript, json,
  markdown, nginx, plaintext, powershell, python, ruby, rust, shell, sql,
  typescript, xml, yaml,
};

let registered = false;
function ensureRegistered() {
  if (registered) return;
  for (const [name, lang] of Object.entries(REGISTRATIONS)) {
    // hljs typings expect LanguageFn; the imports are language modules and
    // hljs accepts them at runtime — cast here to silence the strict type.
    hljs.registerLanguage(name, lang as never);
  }
  // Useful aliases not preregistered by hljs itself.
  hljs.registerAliases(['yml'], { languageName: 'yaml' });
  hljs.registerAliases(['hcl', 'tf', 'terraform'], { languageName: 'ini' });
  hljs.registerAliases(['toml'], { languageName: 'ini' });
  hljs.registerAliases(['ps1'], { languageName: 'powershell' });
  hljs.registerAliases(['rb'], { languageName: 'ruby' });
  hljs.registerAliases(['py'], { languageName: 'python' });
  hljs.registerAliases(['rs'], { languageName: 'rust' });
  hljs.registerAliases(['ts'], { languageName: 'typescript' });
  hljs.registerAliases(['js', 'mjs', 'cjs'], { languageName: 'javascript' });
  hljs.registerAliases(['html', 'htm', 'svg', 'xhtml'], { languageName: 'xml' });
  hljs.registerAliases(['md', 'mdx'], { languageName: 'markdown' });
  hljs.registerAliases(['sh', 'zsh'], { languageName: 'bash' });
  hljs.registerAliases(['cs'], { languageName: 'csharp' });
  registered = true;
}

// Maps file extensions and well-known basenames to the registered language
// id. Extensions are lowercase, no leading dot.
const EXT_LANG: Record<string, string> = {
  // structured
  json: 'json', yaml: 'yaml', yml: 'yaml', xml: 'xml', html: 'xml',
  htm: 'xml', svg: 'xml', toml: 'ini', ini: 'ini', conf: 'ini',
  cfg: 'ini', env: 'ini', tf: 'ini', tfvars: 'ini', hcl: 'ini',
  // scripts / shells
  sh: 'bash', bash: 'bash', zsh: 'bash', ps1: 'powershell',
  // app code
  py: 'python', rb: 'ruby', go: 'go', rs: 'rust', java: 'java',
  cs: 'csharp', js: 'javascript', mjs: 'javascript', cjs: 'javascript',
  ts: 'typescript', tsx: 'typescript', jsx: 'javascript',
  // text-ish
  md: 'markdown', mdx: 'markdown',
  sql: 'sql', diff: 'diff', patch: 'diff',
  css: 'css', scss: 'css', less: 'css',
};

const NAME_LANG: Record<string, string> = {
  dockerfile: 'dockerfile',
  containerfile: 'dockerfile',
  makefile: 'bash',
  'nginx.conf': 'nginx',
};

export function languageFor(path: string): string {
  const base = path.split(/[\\/]/).pop()?.toLowerCase() ?? '';
  if (NAME_LANG[base]) return NAME_LANG[base];
  const dot = base.lastIndexOf('.');
  if (dot >= 0) {
    const ext = base.slice(dot + 1);
    if (EXT_LANG[ext]) return EXT_LANG[ext];
  }
  return 'plaintext';
}

// Returns a sanitized HTML string with hljs span markup. Falls back to
// plaintext if the requested language isn't registered (e.g. for a file
// type we don't ship a grammar for).
export function highlightCode(code: string, language: string): string {
  ensureRegistered();
  const lang = hljs.getLanguage(language) ? language : 'plaintext';
  try {
    return hljs.highlight(code, { language: lang, ignoreIllegals: true }).value;
  } catch {
    return escapeHtml(code);
  }
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}
