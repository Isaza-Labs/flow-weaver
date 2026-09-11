<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { workflows, errorMessage, type Workflow } from '$lib/api/client';
  import ScheduleForm from '$lib/components/ScheduleForm.svelte';
  import { effectiveInputSchema } from '$lib/workflow/deriveInputs';
  import { PageHeader, Alert, Spinner } from '$lib/components/ui';

  let workflow = $state<Workflow | null>(null);
  let loadError = $state('');

  onMount(async () => {
    try {
      const wfId = page.params.id;
      if (!wfId) { loadError = 'No workflow id'; return; }
      workflow = await workflows.get(wfId);
    } catch (e) {
      loadError = errorMessage(e);
          toast.fromError(e, 'Action failed');
    }
  });

  function backToList() {
    goto(`/workflows/${page.params.id}/schedules`);
  }
</script>

<svelte:head><title>New schedule · {workflow?.name ?? 'Workflow'} · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-3xl mx-auto space-y-5">
  <PageHeader
    title="New schedule"
    breadcrumbs={[
      { label: 'Workflows', href: '/workflows' },
      ...(workflow ? [{ label: workflow.name, href: `/workflows/${page.params.id}` }] : []),
      { label: 'Schedules', href: `/workflows/${page.params.id}/schedules` },
      { label: 'New' },
    ]}
  />

  {#if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if !workflow}
    <div class="flex justify-center py-8"><Spinner size="lg" /></div>
  {:else if page.params.id}
    <ScheduleForm
      workflowId={page.params.id}
      inputSchema={effectiveInputSchema(workflow)}
      environment={workflow.environment}
      onSaved={backToList}
      onCancel={backToList}
    />
  {/if}
</div>
