<script lang="ts">
  // Thin wrapper around the `mermaid` library that renders a diagram
  // from a source string into SVG. Two-layer representation means every
  // python_snippet / transform task carries its own Mermaid source —
  // this component is what surfaces it both in the snippet edit page
  // (Logic Diagram tab) and in the workflow canvas side panel.
  //
  // Responsibilities kept here:
  //   - Lazy-load mermaid only when the component mounts (keeps the
  //     snippet-list route fast; mermaid is ~400 KB minified).
  //   - Regenerate the SVG whenever `source` changes (debounced).
  //   - Show a friendly error when the source doesn't parse — mermaid
  //     throws rich errors, we surface them in-place so the author sees
  //     exactly where the diagram breaks.
  //   - Pick a unique id per render so two instances on the same page
  //     (snippet edit + workflow side panel) don't collide.
  import { onMount, onDestroy } from 'svelte';

  let {
    source,
    class: cls = '',
  }: { source: string | null | undefined; class?: string } = $props();

  let el: HTMLDivElement | undefined = $state();
  let errorMessage = $state<string | null>(null);
  let mermaidApi: typeof import('mermaid').default | null = null;
  let renderCounter = 0;
  let renderToken = 0;

  onMount(async () => {
    // Dynamic import — keeps mermaid out of the main bundle and lets
    // the rest of the app load without waiting on its ~400 KB tree.
    const mod = await import('mermaid');
    mermaidApi = mod.default;
    mermaidApi.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      theme: 'dark',
      flowchart: { useMaxWidth: true, htmlLabels: true, curve: 'basis' },
      sequence: { useMaxWidth: true },
    });
    await render();
  });

  onDestroy(() => {
    // Invalidate any in-flight render so it doesn't write to a stale
    // DOM node when the component unmounts (e.g. dialog closes mid-
    // render on slow diagrams).
    renderToken++;
  });

  // React to source changes. Debounce with a microtask so rapid typing
  // in the textarea doesn't kick off a render per keystroke — we let
  // Svelte batch and then render once with the final value.
  $effect(() => {
    void source; // dep
    if (!mermaidApi || !el) return;
    const token = ++renderToken;
    queueMicrotask(() => {
      if (token !== renderToken) return;
      render();
    });
  });

  async function render() {
    if (!mermaidApi || !el) return;
    const src = (source ?? '').trim();
    if (!src) {
      errorMessage = null;
      el.innerHTML = '<div class="text-xs text-surface-500 italic p-4">No diagram</div>';
      return;
    }
    const thisToken = renderToken;
    const id = `mermaid-${++renderCounter}-${Math.floor(Math.random() * 1e6)}`;
    try {
      const { svg } = await mermaidApi.render(id, src);
      if (thisToken !== renderToken) return; // superseded
      errorMessage = null;
      if (el) el.innerHTML = svg;
    } catch (e) {
      if (thisToken !== renderToken) return;
      errorMessage = (e as Error).message ?? String(e);
      // Render-level errors leave a partial SVG behind sometimes;
      // clear the container so the user sees the error, not a
      // half-drawn diagram that looks valid.
      if (el) el.innerHTML = '';
    }
  }
</script>

<div class="w-full {cls}">
  {#if errorMessage}
    <div class="text-xs text-error-400 bg-error-500/10 border border-error-500/40 rounded-md px-3 py-2 mb-2 whitespace-pre-wrap font-mono">
      {errorMessage}
    </div>
  {/if}
  <div bind:this={el} class="mermaid-container w-full overflow-auto"></div>
</div>

<style>
  .mermaid-container :global(svg) {
    max-width: 100%;
    height: auto;
    display: block;
  }
</style>
