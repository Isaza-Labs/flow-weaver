<script lang="ts">
  import { onMount } from 'svelte';
  import { qaLab, errorMessage, type QaDashboardResponse } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, Badge, EmptyState, Alert, Spinner,
    StatusBadge, toast, formatDateTime,
  } from '$lib/components/ui';
  import { FlaskConical, CheckCircle2, Clock, RefreshCw } from 'lucide-svelte';

  let data = $state<QaDashboardResponse | null>(null);
  let loading = $state(true);
  let error = $state('');

  onMount(load);

  async function load() {
    loading = true;
    error = '';
    try {
      data = await qaLab.dashboard();
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load QA lab');
    } finally {
      loading = false;
    }
  }
</script>

<svelte:head><title>QA lab · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="QA lab"
    description="Workflows promoted to the qa environment and their readiness for promotion to production. Promotion requires a completed run in the last 48 hours."
  >
    {#snippet actions()}
      <Button size="sm" variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
    {/snippet}
  </PageHeader>

  {#if error}
    <Alert tone="error">{error}</Alert>
  {/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading QA lab…" /></div>
  {:else if data}
    <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
      <Card>
        <div class="text-xs uppercase tracking-wide text-surface-500">Flagged devices</div>
        <div class="text-2xl font-bold">{data.qa_device_count}</div>
      </Card>
      <Card>
        <div class="text-xs uppercase tracking-wide text-surface-500">Flagged pools</div>
        <div class="text-2xl font-bold">{data.qa_pool_count}</div>
      </Card>
      <Card>
        <div class="text-xs uppercase tracking-wide text-surface-500">QA workflows</div>
        <div class="text-2xl font-bold">{data.workflows.length}</div>
      </Card>
    </div>

    {#if data.workflows.length === 0}
      <Card padding="none">
        <EmptyState
          icon={FlaskConical}
          title="No workflows in QA yet"
          description="Promote a draft workflow to qa to see it here. Runs against qa targets validate the workflow before production."
        />
      </Card>
    {:else}
      <DataTable>
        <thead>
          <tr>
            <th>Workflow</th>
            <th>Version</th>
            <th>Last run</th>
            <th>Completed</th>
            <th>Promotion</th>
            <th class="!text-right">Open</th>
          </tr>
        </thead>
        <tbody>
          {#each data.workflows as row (row.workflow_id)}
            <tr>
              <td class="font-medium text-surface-900-100">{row.name}</td>
              <td class="text-xs font-mono text-surface-500">v{row.version}</td>
              <td>
                {#if row.last_run_status}
                  <StatusBadge status={row.last_run_status} label={row.last_run_status} />
                {:else}
                  <span class="text-xs text-surface-500">never run</span>
                {/if}
              </td>
              <td class="text-xs text-surface-500">
                {row.last_run_completed_at ? formatDateTime(row.last_run_completed_at) : '—'}
              </td>
              <td>
                {#if row.promotion_ready}
                  <Badge tone="success"><CheckCircle2 size={12} class="inline mr-1" /> Ready</Badge>
                {:else}
                  <Badge tone="neutral"><Clock size={12} class="inline mr-1" /> Needs qa run (&lt; 48h)</Badge>
                {/if}
              </td>
              <td class="text-right">
                <Button size="sm" variant="ghost" href="/workflows/{row.workflow_id}">Open</Button>
              </td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
    {/if}
  {/if}
</div>
