// Safe markdown rendering for anything produced by the LLM (chat bubbles,
// guide mascot, run diagnostics, future tool result previews). The user
// content is HTML-escaped *before* being fed to marked, so raw <script>
// or <img onerror=…> tags the model may emit can't reach the DOM. Marked
// then renders a whitelisted subset of markdown (GFM tables + lists +
// code + bold/italic + headings). Links survive only when they point at one
// of this app's resource pages — see `LINKABLE_ROUTE` — so a hostile markdown
// link can't turn into an off-site click target or a state-changing one.
//
// Usage:
//   import { renderMarkdownSafe } from '$lib/markdown';
//   <div class="prose">{@html renderMarkdownSafe(message)}</div>

import { marked, type MarkedOptions } from 'marked';

// One renderer instance reused everywhere. Customizing per-call would
// recompile the same rules on every message render.
const renderer = new marked.Renderer();

// Resource pages the agent is allowed to link to. An allowlist rather than a
// same-origin check, for two reasons:
//
//   1. Tool results build links from `Workflow:PublicBaseUrl`, routinely
//      configured to an origin nobody browses (the compose default is
//      localhost:3000 while operators reach the app by LAN IP). Comparing
//      origins would silently drop every one of those links, so we keep the
//      path and discard whatever origin was in the href.
//   2. Keeping the path of an arbitrary URL is only safe if the destination
//      is inert. `[click here](https://evil.example/logout)` would otherwise
//      render as a working link that logs the user out. These routes only
//      display a resource.
//
// Adding a `url` to a tool result means adding its route here, or the link
// renders as plain text.
const LINKABLE_ROUTE =
  /^\/(workflows|subflows|runs|schedules|snippets|devices|device-pools|integrations|vendor-commands|policies)(\/|$)/;

// Reduces an href to an allowlisted path on OUR origin, or null when it is
// anything else. Everything the model writes goes through here, so a rendered
// <a> can never leave the app — the phishing vector that made this renderer
// strip links wholesale in the first place.
//
// Base is a fixed dummy rather than location.origin so this behaves
// identically under SSR, where `window` doesn't exist.
function internalPath(href: string): string | null {
  const raw = href.trim();
  if (!raw) return null;
  try {
    const u = new URL(raw, 'http://markdown.invalid');
    // Blocks javascript:, data:, vbscript:, mailto:, tel: … Only http(s)
    // parses into something we're willing to navigate to.
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return null;
    if (!LINKABLE_ROUTE.test(u.pathname)) return null;
    return u.pathname + u.search + u.hash;
  } catch {
    return null;
  }
}

// `"` and `'` survive escapeHtml (it only handles `&` and `<`, see below),
// and this is the one place we emit an attribute value — so quote them here
// or a crafted href breaks out of the attribute.
function escapeAttr(value: string): string {
  return value.replaceAll('"', '%22').replaceAll("'", '%27');
}

// In-app links render as real anchors that open in a new tab, so reading an
// answer never costs the user the conversation they're in. Anything that
// isn't a navigable same-origin link degrades to plain text, as before.
renderer.link = ({ href, text }) => {
  const path = internalPath(href ?? '');
  if (!path) return text;
  return `<a href="${escapeAttr(path)}" target="_blank" rel="noopener noreferrer">${text}</a>`;
};
// Images are rare in our flows and a data: URI is a viable exfil path
// (history leak via referrer-less fetch). Drop entirely.
renderer.image = ({ text }) => text || '';

const OPTIONS: MarkedOptions = {
  breaks: true,   // treat single newlines as <br> — matches how the LLM writes
  gfm: true,      // GitHub-flavored: tables, strikethrough, task lists
  renderer,
};

function escapeHtml(input: string): string {
  // Escape `&` and `<` only. `>` is deliberately NOT escaped because:
  //   1. It can't open/close an HTML tag without a `<` in front, and `<`
  //      is already neutralized above — so `>` alone is harmless.
  //   2. Markdown uses a leading `>` on a line for blockquotes.
  //      Escaping it turns `> Plan` into `&gt; Plan`, which marked sees
  //      as plain text and renders with a literal `>` instead of the
  //      styled blockquote the LLM intended.
  // `"` and `'` also left unescaped — marked emits text nodes, not
  // attribute values, so quote-based attribute injection isn't possible
  // from LLM-authored content after `<` is gone.
  return input
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;');
}

export function renderMarkdownSafe(text: string | null | undefined): string {
  if (!text) return '';
  try {
    // marked.parse is synchronous in marked v17 when you don't enable
    // async-aware extensions. We call it sync-style and cast to string.
    return marked.parse(escapeHtml(text), OPTIONS) as string;
  } catch {
    // Fall back to the pre-escaped text so a parse error still renders
    // *something* — worst case the user sees their plain text.
    return escapeHtml(text);
  }
}
