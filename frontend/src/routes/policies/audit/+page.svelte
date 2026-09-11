<script lang="ts">
  import { onMount } from 'svelte';
  import { policies, errorMessage, type PolicyAuditResponse } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Button, Badge, EmptyState, Alert, Spinner,
    Select, formatDateTime, toast,
  } from '$lib/components/ui';
  import { ShieldCheck, RefreshCw } from 'lucide-svelte';

  let data = $state<PolicyAuditResponse | null>(null);
  let loading = $state(true);
  let error = $state('');
  let days = $state(7);

  onMount(load);

  async function load() {
    loading = true;
    error = '';
    try {
      data = await policies.audit(days);
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Couldn’t load audit');
    } finally {
      loading = false;
    }
  }

  // Sorted list for "top offenders" badge row. Preserves insertion
  // order for ties so the same render is stable across reloads.
  const topPolicies = $derived(
    data
      ? Object.entries(data.by_policy).sort((a, b) => b[1] - a[1]).slice(0, 8)
      : [],
  );
</script>

<svelte:head><title>Policy audit · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader
    title="Policy audit"
    description="Every time a policy blocks a workflow create/update/promote, a trace event is emitted. This view rolls up those events so you can see which rules are active, which are noise, and which might be too aggressive."
  >
    {#snippet actions()}
      <div class="flex items-center gap-2">
        <Select bind:value={days} aria-label="Window" help="policies_audit.window" onchange={load}>
          <option value={1}>last 24h</option>
          <option value={7}>last 7 days</option>
          <option value={30}>last 30 days</option>
          <option value={90}>last 90 days</option>
        </Select>
        <Button size="sm" variant="ghost" icon={RefreshCw} onclick={load}>Refresh</Button>
      </div>
    {/snippet}
  </PageHeader>

  {#if error}<Alert tone="error">{error}</Alert>{/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" label="Loading audit…" /></div>
  {:else if data}
    <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
      <Card>
        <div class="text-xs uppercase tracking-wide text-surface-500">Blocks</div>
        <div class="text-2xl font-bold">{data.total}</div>
        <div class="text-xs text-surface-500 mt-1">in the selected window</div>
      </Card>
      <Card>
        <div class="text-xs uppercase tracking-wide text-surface-500">Unique policies firing</div>
        <div class="text-2xl font-bold">{Object.keys(data.by_policy).length}</div>
      </Card>
      <Card>
        <div class="text-xs uppercase tracking-wide text-surface-500">Noisiest rule</div>
        <div class="text-2xl font-bold">{topPolicies[0]?.[0] ?? '—'}</div>
        <div class="text-xs text-surface-500 mt-1">{topPolicies[0]?.[1] ?? 0} block(s)</div>
      </Card>
    </div>

    {#if topPolicies.length > 0}
      <Card>
        <div class="text-xs uppercase tracking-wide text-surface-500 mb-2">Top offenders</div>
        <div class="flex flex-wrap gap-2">
          {#each topPolicies as [name, count]}
            <Badge tone="warning">{name} · {count}</Badge>
          {/each}
        </div>
      </Card>
    {/if}

    {#if data.recent.length === 0}
      <Card padding="none">
        <EmptyState
          icon={ShieldCheck}
          title="No blocks in this window"
          description="Either every operation is compliant or your policies aren't tight enough. Head back to /policies to add or tune a rule."
        />
      </Card>
    {:else}
      <DataTable>
        <thead>
          <tr>
            <th>When</th>
            <th>Policy</th>
            <th>Action</th>
            <th>Reason</th>
            <th>Request</th>
          </tr>
        </thead>
        <tbody>
          {#each data.recent as entry}
            <tr>
              <td class="text-xs font-mono text-surface-500 whitespace-nowrap">{formatDateTime(entry.at)}</td>
              <td class="font-medium">{entry.policy_name ?? '—'}</td>
              <td><Badge tone="neutral">{entry.action ?? '—'}</Badge></td>
              <td class="text-sm text-surface-700-300">{entry.reason ?? '—'}</td>
              <td class="text-[10px] font-mono text-surface-500 truncate max-w-[180px]">{entry.request_id ?? '—'}</td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
      <p class="text-xs text-surface-500">
        Capped at 500 most-recent entries. Widen the window or query <code>/api/policy/audit?days=...</code> directly for longer horizons.
      </p>
    {/if}
  {/if}
</div>
