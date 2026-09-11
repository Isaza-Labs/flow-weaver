<script lang="ts">
  import { CloudOff, AlertTriangle, RefreshCw } from 'lucide-svelte';
  import Button from './Button.svelte';
  import { ApiError, errorMessage, isOfflineError } from '$lib/api/client';

  let {
    error,
    onRetry,
    title,
    compact = false,
  }: {
    error: unknown;
    onRetry?: () => void;
    title?: string;
    compact?: boolean;
  } = $props();

  const offline = $derived(isOfflineError(error));
  const Icon = $derived(offline ? CloudOff : AlertTriangle);
  const heading = $derived(title ?? (offline ? 'No connection to the server' : 'We couldn\u2019t load this'));
  const message = $derived(errorMessage(error));
  const code = $derived(error instanceof ApiError && error.status > 0 ? error.status : null);
</script>

{#if compact}
  <div class="flex items-start gap-2.5 p-3 rounded-md ring-1 ring-inset ring-surface-300-700 bg-surface-100-900/70">
    <Icon size={16} class="shrink-0 mt-0.5 text-surface-500" />
    <div class="flex-1 min-w-0 text-sm">
      <div class="font-medium text-surface-900-100">{heading}</div>
      <div class="text-surface-600-400 text-xs mt-0.5">{message}</div>
    </div>
    {#if onRetry}
      <Button size="xs" variant="ghost" icon={RefreshCw} onclick={onRetry}>Retry</Button>
    {/if}
  </div>
{:else}
  <div class="flex flex-col items-center justify-center text-center py-12 px-6">
    <div class="w-12 h-12 rounded-full bg-surface-200-800/60 text-surface-500 flex items-center justify-center mb-4">
      <Icon size={20} />
    </div>
    <h3 class="text-sm font-semibold text-surface-900-100">{heading}</h3>
    <p class="text-sm text-surface-600-400 mt-1.5 max-w-sm">{message}</p>
    {#if code}
      <p class="text-[10px] font-mono text-surface-500 mt-2 tabular-nums">code {code}</p>
    {/if}
    {#if onRetry}
      <div class="mt-5">
        <Button variant="primary" icon={RefreshCw} onclick={onRetry}>Retry</Button>
      </div>
    {/if}
  </div>
{/if}
