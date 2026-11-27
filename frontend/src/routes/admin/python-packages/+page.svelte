<script lang="ts">
  // Python packages became the Packages tab of /admin/python, next to the uv
  // environments. This stays so bookmarks, runbooks, docs and links in old chat
  // answers keep working — it lands on that tab.
  //
  // replaceState: the old path should not sit in history for Back to land on and
  // bounce forward again.
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { Spinner } from '$lib/components/ui';

  $effect(() => {
    // Any other query params carry over; only the tab is set.
    const query = new URLSearchParams(page.url.search);
    query.set('tab', 'packages');
    goto(`/admin/python?${query.toString()}`, { replaceState: true });
  });
</script>

<svelte:head><title>Python packages · FlowWeaver</title></svelte:head>

<div class="flex flex-col items-center justify-center gap-3 py-24 text-sm text-surface-600-400">
  <Spinner />
  <p>Python packages now live under Python at <code class="font-mono">/admin/python</code> — taking you there.</p>
</div>
