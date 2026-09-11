<script lang="ts">
  import type { Snippet } from 'svelte';

  interface Props {
    title: string;
    lead?: string;
    children: Snippet;
  }

  let { title, lead, children }: Props = $props();
</script>

<article class="docs-article mx-auto max-w-3xl px-8 py-10 text-sm leading-relaxed text-surface-700-300">
  <header class="mb-8 pb-6 border-b border-surface-200-800/60">
    <h1 class="text-2xl font-semibold tracking-tight text-surface-900-100">{title}</h1>
    {#if lead}
      <p class="mt-2 text-base text-surface-500-400">{lead}</p>
    {/if}
  </header>

  <div class="space-y-6">
    {@render children()}
  </div>
</article>

<style>
  .docs-article :global(h2) {
    margin-top: 2rem;
    margin-bottom: 0.75rem;
    font-size: 1.125rem;
    font-weight: 600;
    letter-spacing: -0.01em;
    color: var(--color-surface-900-100);
    padding-bottom: 0.375rem;
    border-bottom: 1px solid color-mix(in oklab, var(--color-surface-300-700) 60%, transparent);
  }
  .docs-article :global(h3) {
    margin-top: 1.5rem;
    margin-bottom: 0.5rem;
    font-size: 0.95rem;
    font-weight: 600;
    color: var(--color-surface-900-100);
  }
  .docs-article :global(h4) {
    margin-top: 1rem;
    margin-bottom: 0.25rem;
    font-size: 0.85rem;
    font-weight: 600;
    color: var(--color-surface-800-200);
  }
  .docs-article :global(p) {
    margin-bottom: 0.75rem;
  }
  .docs-article :global(ul),
  .docs-article :global(ol) {
    margin: 0.5rem 0 0.75rem 1.25rem;
    padding-left: 0.5rem;
  }
  .docs-article :global(ul) {
    list-style: disc outside;
  }
  .docs-article :global(ol) {
    list-style: decimal outside;
  }
  .docs-article :global(li) {
    margin-bottom: 0.25rem;
  }
  .docs-article :global(li > p) {
    margin-bottom: 0.25rem;
  }
  .docs-article :global(a) {
    color: var(--color-primary-300);
    text-decoration: underline;
    text-decoration-color: color-mix(in oklab, var(--color-primary-300) 30%, transparent);
    text-underline-offset: 2px;
    transition: color 150ms, text-decoration-color 150ms;
  }
  .docs-article :global(a:hover) {
    color: var(--color-primary-200);
    text-decoration-color: currentColor;
  }
  .docs-article :global(code) {
    font-family: ui-monospace, SFMono-Regular, monospace;
    font-size: 0.85em;
    padding: 0.1em 0.35em;
    border-radius: 0.25rem;
    background: color-mix(in oklab, var(--color-surface-300-700) 40%, transparent);
    color: var(--color-surface-900-100);
  }
  .docs-article :global(pre) {
    margin: 0.75rem 0;
    padding: 0.75rem 1rem;
    border-radius: 0.5rem;
    background: var(--color-surface-100-900);
    border: 1px solid var(--color-surface-300-700);
    overflow-x: auto;
    font-size: 0.8rem;
    line-height: 1.6;
  }
  .docs-article :global(pre code) {
    background: transparent;
    padding: 0;
  }
  .docs-article :global(strong) {
    color: var(--color-surface-900-100);
    font-weight: 600;
  }
  .docs-article :global(kbd) {
    display: inline-block;
    padding: 0.1rem 0.4rem;
    font-family: ui-monospace, SFMono-Regular, monospace;
    font-size: 0.75em;
    color: var(--color-surface-800-200);
    background: var(--color-surface-100-900);
    border: 1px solid var(--color-surface-300-700);
    border-radius: 0.25rem;
    box-shadow: 0 1px 0 var(--color-surface-300-700);
  }
  .docs-article :global(dl) {
    display: grid;
    grid-template-columns: max-content 1fr;
    column-gap: 1rem;
    row-gap: 0.5rem;
    margin: 0.75rem 0;
    padding: 0.75rem 1rem;
    border-radius: 0.5rem;
    background: color-mix(in oklab, var(--color-surface-100-900) 50%, transparent);
    border: 1px solid var(--color-surface-200-800);
  }
  .docs-article :global(dt) {
    font-family: ui-monospace, SFMono-Regular, monospace;
    font-size: 0.78rem;
    color: var(--color-primary-200);
    white-space: nowrap;
  }
  .docs-article :global(dd) {
    font-size: 0.85rem;
    color: var(--color-surface-700-300);
    margin: 0;
  }
  .docs-article :global(table) {
    width: 100%;
    margin: 0.75rem 0;
    border-collapse: collapse;
    font-size: 0.85rem;
  }
  .docs-article :global(th),
  .docs-article :global(td) {
    padding: 0.5rem 0.75rem;
    text-align: left;
    border-bottom: 1px solid var(--color-surface-200-800);
    vertical-align: top;
  }
  .docs-article :global(th) {
    font-weight: 600;
    color: var(--color-surface-900-100);
    background: color-mix(in oklab, var(--color-surface-100-900) 50%, transparent);
    font-size: 0.78rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }
  .docs-article :global(blockquote) {
    margin: 0.75rem 0;
    padding: 0.5rem 1rem;
    border-left: 3px solid var(--color-primary-500);
    background: color-mix(in oklab, var(--color-primary-500) 8%, transparent);
    color: var(--color-surface-800-200);
    border-radius: 0 0.375rem 0.375rem 0;
  }
  .docs-article :global(hr) {
    border: none;
    border-top: 1px solid var(--color-surface-200-800);
    margin: 1.5rem 0;
  }
</style>
