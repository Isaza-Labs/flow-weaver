<script lang="ts">
  import type { StepRun } from '$lib/api/client';
  import { StatusBadge, formatDuration } from '$lib/components/ui';
  import { safeAnimate } from '$lib/anim';

  let {
    step,
    onOpenPlayground,
  }: {
    step: StepRun;
    onOpenPlayground?: (step: StepRun) => void;
  } = $props();

  type Tab = 'output' | 'input' | 'logs' | 'error';
  const TABS: Tab[] = ['output', 'input', 'logs', 'error'];
  let activeTab = $state<Tab>('output');

  const duration = $derived(formatDuration(step.started_at, step.completed_at));

  let contentEl = $state<HTMLDivElement | null>(null);
  $effect(() => {
    activeTab;
    if (!contentEl) return;
    safeAnimate(contentEl, {
      opacity: [0, 1],
      translateY: [4, 0],
      duration: 180,
      ease: 'outQuad',
    });
  });
</script>

<div class="bg-surface-100-900 border border-surface-200-800 rounded-lg overflow-hidden">
  <header class="flex items-center justify-between gap-3 px-4 py-2 border-b border-surface-200-800">
    <div class="flex items-center gap-3 min-w-0">
      <StatusBadge status={step.status} />
      <span class="text-sm font-medium text-surface-900-100 truncate">{step.node_id}</span>
      {#if step.device_id}
        <span class="text-xs text-surface-500 font-mono">device: {step.device_id.slice(0, 8)}</span>
      {/if}
    </div>
    <div class="flex items-center gap-4 text-xs text-surface-500">
      <span>Duration: <span class="font-mono tabular-nums text-surface-700-300">{duration}</span></span>
      {#if step.worker_id}
        <span>Worker: <span class="font-mono text-surface-700-300">{step.worker_id}</span></span>
      {/if}
      {#if onOpenPlayground}
        <button
          type="button"
          onclick={() => onOpenPlayground?.(step)}
          class="text-primary-300 hover:text-primary-200 transition-colors"
        >
          Open in playground
        </button>
      {/if}
    </div>
  </header>

  <div class="flex border-b border-surface-200-800">
    {#each TABS as tab}
      {@const isActive = activeTab === tab}
      {@const hasError = tab === 'error' && step.error}
      <button
        type="button"
        onclick={() => (activeTab = tab)}
        class="px-4 py-2 text-xs font-medium transition-colors capitalize relative
          {isActive ? 'text-primary-300' : hasError ? 'text-error-400' : 'text-surface-500 hover:text-surface-700-300'}"
      >
        {tab}
        {#if hasError}
          <span class="ml-1 inline-block w-1.5 h-1.5 rounded-full bg-error-500"></span>
        {/if}
        {#if isActive}
          <span class="absolute inset-x-0 -bottom-px h-px bg-primary-400"></span>
        {/if}
      </button>
    {/each}
  </div>

  <div bind:this={contentEl} class="p-3 max-h-80 overflow-auto bg-surface-50-950">
    {#if activeTab === 'output'}
      <pre class="font-mono text-xs text-success-300 whitespace-pre-wrap">{JSON.stringify(step.output_payload, null, 2) || '(no output)'}</pre>
    {:else if activeTab === 'input'}
      <pre class="font-mono text-xs text-warning-300 whitespace-pre-wrap">{JSON.stringify(step.input_payload, null, 2) || '(no input)'}</pre>
    {:else if activeTab === 'logs'}
      <pre class="font-mono text-xs text-surface-700-300 whitespace-pre-wrap">{step.logs || '(no logs)'}</pre>
    {:else if activeTab === 'error'}
      <pre class="font-mono text-xs text-error-300 whitespace-pre-wrap">{step.error || '(no error)'}</pre>
    {/if}
  </div>
</div>
