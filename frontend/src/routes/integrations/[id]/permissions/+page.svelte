<script lang="ts">
  import { page } from '$app/stores';
  import ResourcePermissionsPanel from '$lib/components/ResourcePermissionsPanel.svelte';
  import { PageHeader } from '$lib/components/ui';

  let integrationId = $derived($page.params.id ?? '');
</script>

<svelte:head><title>Permissions · Integration · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-4xl mx-auto space-y-5">
  <PageHeader
    title="Integration permissions"
    description="Grant individual users one of owner / editor / runner / viewer on this integration. Admins always pass."
  />
  <ResourcePermissionsPanel resourceType="integration" resourceId={integrationId} title="Per-integration grants" />
</div>
