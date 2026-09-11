<script lang="ts">
  import { toast } from '$lib/components/ui';
  import { onMount } from 'svelte';
  import { skills, errorMessage, type Skill } from '$lib/api/client';
  import {
    PageHeader, Card, DataTable, Badge, EmptyState, Alert, Spinner,
  } from '$lib/components/ui';
  import { Sparkles } from 'lucide-svelte';

  // Legacy read-only view of the `skills` DB table (reusable AI actions).
  // Moved here from /skills when /skills was repurposed for the Sprint 7
  // admin prompt-skills upload UI.
  let skillList = $state<Skill[]>([]);
  let loading = $state(true);
  let error = $state('');

  onMount(async () => {
    loading = true;
    try {
      const res = await skills.list();
      skillList = res.data;
    } catch (e) {
      error = errorMessage(e);
      toast.fromError(e, 'Action failed');
    } finally {
      loading = false;
    }
  });
</script>

<svelte:head><title>Admin · Actions · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
  <PageHeader title="Reusable actions" description="Read-only view of the legacy skills table." />

  {#if error}<Alert tone="error">{error}</Alert>{/if}

  {#if loading}
    <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
  {:else if skillList.length === 0}
    <Card padding="none">
      <EmptyState icon={Sparkles} title="No reusable actions" description="Entries will appear here once defined." />
    </Card>
  {:else}
    <DataTable>
      <thead>
        <tr>
          <th>Name</th>
          <th>Type</th>
          <th>Triggers</th>
          <th>Tags</th>
          <th class="!text-right">Use count</th>
        </tr>
      </thead>
      <tbody>
        {#each skillList as skill (skill.id)}
          <tr>
            <td>
              <div class="font-medium text-surface-900-100">{skill.name}</div>
              {#if skill.description}
                <div class="text-xs text-surface-500 mt-0.5 line-clamp-1">{skill.description}</div>
              {/if}
            </td>
            <td><Badge tone="neutral">{skill.skill_type || '—'}</Badge></td>
            <td>
              {#if skill.triggers?.length}
                <div class="flex flex-wrap gap-1">
                  {#each skill.triggers as trigger}
                    <Badge tone="primary" size="xs">{trigger}</Badge>
                  {/each}
                </div>
              {:else}
                <span class="text-xs text-surface-500">—</span>
              {/if}
            </td>
            <td>
              {#if skill.tags?.length}
                <div class="flex flex-wrap gap-1">
                  {#each skill.tags as tag}
                    <Badge tone="neutral" size="xs">{tag}</Badge>
                  {/each}
                </div>
              {:else}
                <span class="text-xs text-surface-500">—</span>
              {/if}
            </td>
            <td class="text-right font-mono tabular-nums text-surface-700-300">{skill.use_count ?? 0}</td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
    <p class="text-xs text-surface-500">{skillList.length} action{skillList.length === 1 ? '' : 's'}</p>
  {/if}
</div>
