<script lang="ts">
  // Secrets moved into the Vault, which holds every kind of sensitive entry on one
  // screen. This stays so bookmarks, runbooks, docs and links in old chat answers
  // keep working — it lands on the Vault, filtered to secret values. It is still
  // under /admin, so the layout keeps non-admins out exactly as before.
  //
  // replaceState: the old path should not sit in history for Back to land on and
  // bounce forward again.
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { Spinner } from '$lib/components/ui';

  $effect(() => {
    // ?new=1 carries over, so old "new secret" links still open a form.
    const params = new URLSearchParams(page.url.search);
    if (!params.has('type')) params.set('type', 'secret');
    goto(`/vault?${params.toString()}`, { replaceState: true });
  });
</script>

<svelte:head><title>Vault · FlowWeaver</title></svelte:head>

<div class="flex flex-col items-center justify-center gap-3 py-24 text-sm text-surface-600-400">
  <Spinner />
  <p>Secrets now live in the Vault at <code class="font-mono">/vault</code> — taking you there.</p>
</div>
