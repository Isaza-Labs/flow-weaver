<script lang="ts">
  // Renders the LLM's markdown output with chat-appropriate styling.
  // The HTML itself comes from renderMarkdownSafe (escape → marked →
  // whitelisted renderer), so {@html} here is safe against prompt
  // injection or model-emitted <script> tags.
  //
  // Styles are scoped via a single container class so we don't inherit
  // the app's prose plugin (which has its own margins/colors meant for
  // editorial content, not chat bubbles).
  import { renderMarkdownSafe } from '$lib/markdown';

  let { content = '' }: { content?: string | null } = $props();
  const html = $derived(renderMarkdownSafe(content));
</script>

<div class="fw-md">{@html html}</div>

<style>
  .fw-md { line-height: 1.55; color: inherit; word-break: break-word; }

  /* Tight heading scale — chat bubbles live in a narrow column. */
  .fw-md :global(h1) { font-size: 1rem; font-weight: 600; margin: 0.6em 0 0.2em; }
  .fw-md :global(h2) { font-size: 0.95rem; font-weight: 600; margin: 0.6em 0 0.2em; }
  .fw-md :global(h3),
  .fw-md :global(h4) { font-size: 0.88rem; font-weight: 600; margin: 0.5em 0 0.15em; opacity: 0.9; }

  /* Paragraphs and lists: minimal vertical rhythm so a reply with five
     short lines doesn't look like a long document. */
  .fw-md :global(p) { margin: 0.25em 0; }
  .fw-md :global(ul),
  .fw-md :global(ol) { margin: 0.35em 0; padding-left: 1.25em; }
  .fw-md :global(li) { margin: 0.1em 0; }
  .fw-md :global(ul ul),
  .fw-md :global(ol ol),
  .fw-md :global(ul ol),
  .fw-md :global(ol ul) { margin: 0.1em 0; }

  /* GFM task list: default marker looks dated; use a simple square. */
  .fw-md :global(input[type='checkbox']) {
    vertical-align: middle; margin-right: 0.4em;
    accent-color: var(--color-primary-500);
  }

  /* Emphasis. */
  .fw-md :global(strong) { font-weight: 600; color: var(--color-surface-900-100); }
  .fw-md :global(em) { font-style: italic; }
  .fw-md :global(del) { opacity: 0.6; }

  /* Links. Only same-origin ones survive the renderer, and they all open in
     a new tab — so style them as a clear affordance rather than leaving the
     browser default blue against the theme. */
  .fw-md :global(a) {
    color: var(--color-primary-400);
    text-decoration: underline;
    text-underline-offset: 2px;
    text-decoration-color: color-mix(in oklab, var(--color-primary-500) 45%, transparent);
  }
  .fw-md :global(a:hover) {
    color: var(--color-primary-300);
    text-decoration-color: currentColor;
  }

  /* Inline code. */
  .fw-md :global(code) {
    font-family: var(--font-mono);
    font-size: 0.85em;
    padding: 0.08em 0.35em;
    border-radius: 4px;
    background: color-mix(in oklab, var(--color-surface-500) 16%, transparent);
    color: var(--color-primary-300);
  }

  /* Fenced code blocks. */
  .fw-md :global(pre) {
    margin: 0.5em 0;
    padding: 0.6em 0.8em;
    border-radius: 6px;
    background: color-mix(in oklab, var(--color-surface-500) 12%, transparent);
    overflow-x: auto;
    font-family: var(--font-mono);
    font-size: 0.8rem;
    line-height: 1.45;
  }
  .fw-md :global(pre code) {
    padding: 0; background: transparent; color: inherit;
  }

  /* Block quotes — used rarely but when models emit them they should
     read as "callouts". */
  .fw-md :global(blockquote) {
    margin: 0.4em 0;
    padding: 0.1em 0.9em;
    border-left: 2px solid color-mix(in oklab, var(--color-primary-500) 60%, transparent);
    color: var(--color-surface-700-300);
    font-style: italic;
  }

  /* Tables — GFM pipes become real tables. Keep them compact and
     horizontally scroll if they blow out the bubble width. */
  .fw-md :global(table) {
    display: block; max-width: 100%; overflow-x: auto;
    border-collapse: collapse; margin: 0.5em 0; font-size: 0.82rem;
  }
  .fw-md :global(thead) {
    background: color-mix(in oklab, var(--color-surface-500) 10%, transparent);
  }
  .fw-md :global(th),
  .fw-md :global(td) {
    padding: 0.35em 0.6em; text-align: left;
    border: 1px solid color-mix(in oklab, var(--color-surface-500) 25%, transparent);
  }
  .fw-md :global(th) { font-weight: 600; }

  .fw-md :global(hr) {
    margin: 0.8em 0; border: 0;
    border-top: 1px solid color-mix(in oklab, var(--color-surface-500) 20%, transparent);
  }

  /* First/last child get no outer margin so the bubble padding wins. */
  .fw-md :global(> :first-child) { margin-top: 0; }
  .fw-md :global(> :last-child) { margin-bottom: 0; }
</style>
