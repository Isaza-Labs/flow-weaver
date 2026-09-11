<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { Button, Card } from '$lib/components/ui';
  import { Home, RefreshCw, AlertTriangle, CloudOff } from 'lucide-svelte';

  const status = $derived(page.status);
  const isOffline = $derived(status === 503 || status === 504 || status === 502);
  const isNotFound = $derived(status === 404);

  const title = $derived(
    isNotFound
      ? 'Page not found'
      : isOffline
        ? 'Can\u2019t reach the server'
        : 'Something went wrong',
  );

  const description = $derived(
    isNotFound
      ? 'The page you\u2019re trying to open doesn\u2019t exist or has been removed.'
      : isOffline
        ? 'The service isn\u2019t responding. Please try again in a few minutes.'
        : (page.error?.message ?? 'We had a problem processing your request.'),
  );

  const Icon = $derived(isOffline ? CloudOff : AlertTriangle);

  function reload() {
    if (typeof window !== 'undefined') window.location.reload();
  }
</script>

<svelte:head><title>{title} · FlowWeaver</title></svelte:head>

<div class="min-h-screen flex items-center justify-center p-6">
  <Card class="max-w-md w-full" padding="lg">
    <div class="flex flex-col items-center text-center">
      <div class="w-14 h-14 rounded-full bg-error-500/10 ring-1 ring-error-500/30 text-error-300 flex items-center justify-center mb-4">
        <Icon size={24} />
      </div>
      <div class="text-[11px] uppercase tracking-wider text-surface-500 mb-1 font-mono">Error {status}</div>
      <h1 class="text-lg font-semibold tracking-tight text-surface-900-100">{title}</h1>
      <p class="text-sm text-surface-600-400 mt-2">{description}</p>

      <div class="flex items-center gap-2 mt-6">
        <Button variant="primary" icon={RefreshCw} onclick={reload}>Retry</Button>
        <Button variant="ghost" icon={Home} onclick={() => goto('/')}>Go home</Button>
      </div>
    </div>
  </Card>
</div>
