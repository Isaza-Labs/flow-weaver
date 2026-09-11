<script lang="ts">
  import { page } from '$app/stores';
  import ResourcePermissionsPanel from '$lib/components/ResourcePermissionsPanel.svelte';
  import { PageHeader } from '$lib/components/ui';

  let workflowId = $derived($page.params.id ?? '');
</script>

<svelte:head><title>Permissions · Workflow · Flow Weaver</title></svelte:head>

<div class="p-6 max-w-4xl mx-auto space-y-5">
  <PageHeader
    title="Workflow permissions"
    description="Grant individual users one of owner / editor / runner / viewer on this workflow. Admins always pass."
  />
  <ResourcePermissionsPanel resourceType="workflow" resourceId={workflowId} title="Per-workflow grants" />
</div>
