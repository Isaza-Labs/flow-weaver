<script lang="ts">
  import type { Snippet } from 'svelte';
  import { page } from '$app/state';
  import Hint from './Hint.svelte';
  import { findGuideForRoute, hasGuideForRoute, type GuideEntry } from '$lib/guides';

  let {
    title,
    description,
    breadcrumbs,
    actions,
    hint,
  }: {
    title: string;
    description?: string;
    breadcrumbs?: { label: string; href?: string }[];
    actions?: Snippet;
    // Every page that renders a PageHeader gets its ⓘ hint for free,
    // resolved from the central $lib/guides registry by route — no per-page
    // wiring, and no second place for the text to drift out of sync.
    // Pass an entry explicitly only for a surface the route can't identify
    // (a dialog, a sub-panel); pass null to suppress the hint entirely.
    hint?: GuideEntry | null;
  } = $props();

  // Suppress on routes with no specific entry rather than showing the
  // generic fallback — an ⓘ that always says the same thing trains people
  // to ignore it.
  const resolvedHint = $derived.by(() => {
    if (hint !== undefined) return hint;
    const pathname = page?.url?.pathname;
    if (!pathname || !hasGuideForRoute(pathname)) return null;
    return findGuideForRoute(pathname);
  });
</script>

<header class="flex items-start justify-between gap-4 flex-wrap">
  <div class="min-w-0 flex-1">
    {#if breadcrumbs && breadcrumbs.length > 0}
      <nav class="flex items-center gap-1.5 text-xs text-surface-500 mb-2">
        {#each breadcrumbs as crumb, i}
          {#if crumb.href}
            <a href={crumb.href} class="hover:text-surface-700-300 transition-colors">{crumb.label}</a>
          {:else}
            <span>{crumb.label}</span>
          {/if}
          {#if i < breadcrumbs.length - 1}
            <span class="text-surface-400-600">/</span>
          {/if}
        {/each}
      </nav>
    {/if}
    <div class="flex items-center gap-1.5 min-w-0">
      <h1 class="text-xl font-semibold tracking-tight text-surface-900-100 truncate">{title}</h1>
      {#if resolvedHint}
        <Hint entry={resolvedHint} label={`About ${title}`} />
      {/if}
    </div>
    {#if description}
      <p class="text-sm text-surface-600-400 mt-1">{description}</p>
    {/if}
  </div>
  {#if actions}
    <!-- `flex-wrap` lets a long row of actions break onto a second line on
         narrow viewports instead of overflowing horizontally. The outer
         <header> already wraps the title block + actions block as a whole;
         this adds intra-block wrapping for pages with many buttons. -->
    <div class="flex items-center gap-2 flex-wrap justify-end">
      {@render actions()}
    </div>
  {/if}
</header>
