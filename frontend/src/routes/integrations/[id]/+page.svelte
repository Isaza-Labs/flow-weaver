<script lang="ts">
  // Deep-link helper. The integration detail view doesn't exist as a
  // standalone page — every integration is edited inline on the list at
  // /integrations, with one row expanded at a time. Several backend
  // surfaces (error messages, audit descriptions) reference
  // /integrations/<id> assuming a detail page lived here; that URL used
  // to 404. We now redirect to the list with an `expand` query param
  // and the list scrolls + auto-expands the requested row.
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';

  onMount(() => {
    const id = page.params.id ?? '';
    if (!id) {
      void goto('/integrations', { replaceState: true });
      return;
    }
    void goto(`/integrations?expand=${encodeURIComponent(id)}`, { replaceState: true });
  });
</script>

<svelte:head><title>Integration · FlowWeaver</title></svelte:head>

<div class="p-6 text-sm text-surface-500">Opening integration…</div>
