<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { workflows, workflowTriggers, errorMessage, type WorkflowTrigger, type Workflow } from '$lib/api/client';
  import ScheduleForm from '$lib/components/ScheduleForm.svelte';
  import { effectiveInputSchema } from '$lib/workflow/deriveInputs';
  import { PageHeader, Alert, Spinner } from '$lib/components/ui';

  let workflow = $state<Workflow | null>(null);
  let trigger = $state<WorkflowTrigger | null>(null);
  let loading = $state(true);
  let loadError = $state('');

  onMount(async () => {
    loading = true;
    try {
      const wfId = page.params.id;
      const triggerId = page.params.triggerId;
      if (!wfId || !triggerId) { loadError = 'Missing workflow or trigger id'; return; }
      const [wf, t] = await Promise.all([workflows.get(wfId), workflowTriggers.get(triggerId)]);
      workflow = wf;
      trigger = t;
    } catch (e) {
      loadError = errorMessage(e);
          toast.fromError(e, 'Action failed');
    } finally {
      loading = false;
    }
  });

  function backToList() { goto(`/workflows/${page.params.id}/schedules`); }
</script>

<svelte:head><title>Edit schedule · {workflow?.name ?? 'Workflow'} · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-3xl mx-auto space-y-5">
  <PageHeader
    title="Edit schedule"
    breadcrumbs={[
      { label: 'Workflows', href: '/workflows' },
      ...(workflow ? [{ label: workflow.name, href: `/workflows/${page.params.id}` }] : []),
      { label: 'Schedules', href: `/workflows/${page.params.id}/schedules` },
      { label: 'Edit' },
    ]}
  />

  {#if loadError}
    <Alert tone="error">{loadError}</Alert>
  {:else if loading}
    <div class="flex justify-center py-8"><Spinner size="lg" /></div>
  {:else if trigger && page.params.id}
    <ScheduleForm
      workflowId={page.params.id}
      existingTrigger={trigger}
      inputSchema={effectiveInputSchema(workflow)}
      environment={workflow?.environment ?? ''}
      onSaved={backToList}
      onCancel={backToList}
    />
  {/if}
</div>
