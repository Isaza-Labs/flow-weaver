<script lang="ts">
  import { page } from '$app/state';
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { messagingLink, errorMessage, type MessagingLinkPreview } from '$lib/api/client';
  import { authStore } from '$lib/stores/auth.svelte';
  import { Button, Alert, Spinner, Card } from '$lib/components/ui';
  import { MessageSquare, CheckCircle2 } from 'lucide-svelte';

  const token = $derived(page.url.searchParams.get('token'));

  let preview = $state<MessagingLinkPreview | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let confirming = $state(false);
  let done = $state(false);

  onMount(async () => {
    // This route is public so the layout doesn't strip ?token= on redirect.
    // Handle auth here, preserving the FULL url (path + token) through login.
    if (!authStore.isAuthenticated) {
      goto(`/login?redirect=${encodeURIComponent(page.url.pathname + page.url.search)}`);
      return;
    }
    if (!token) {
      error = 'No link token provided.';
      loading = false;
      return;
    }
    try {
      preview = await messagingLink.preview(token);
    } catch (e) {
      error = errorMessage(e);
    } finally {
      loading = false;
    }
  });

  async function confirmLink() {
    if (!token || confirming) return;
    error = null;
    confirming = true;
    try {
      await messagingLink.confirm(token);
      done = true;
    } catch (e) {
      error = errorMessage(e);
    } finally {
      confirming = false;
    }
  }
</script>

<svelte:head><title>Link account · Flow Weaver</title></svelte:head>

<div class="min-h-screen flex items-center justify-center p-6 text-surface-900-100">
  <Card>
    <div class="max-w-md w-full space-y-5 p-2">
      <div class="flex items-center gap-3">
        <span class="inline-flex items-center justify-center w-10 h-10 rounded-lg bg-primary-500/10 text-primary-300">
          <MessageSquare size={20} />
        </span>
        <h1 class="text-xl font-semibold">Link your messaging account</h1>
      </div>

      {#if loading}
        <Spinner label="Loading…" />
      {:else if done}
        <div class="flex items-start gap-3">
          <span class="text-success-300 mt-0.5"><CheckCircle2 size={20} /></span>
          <div>
            <p class="font-medium">Account linked.</p>
            <p class="text-sm text-surface-500">You can return to your chat and message the assistant.</p>
          </div>
        </div>
        <Button variant="primary" onclick={() => goto('/')}>Go to dashboard</Button>
      {:else if error}
        <Alert tone="error">{error}</Alert>
        <Button variant="ghost" onclick={() => goto('/')}>Back to app</Button>
      {:else if preview}
        <p class="text-sm text-surface-500">
          You're signed in as <span class="font-medium text-surface-900-100">{authStore.session?.username}</span>.
          Confirm to bind this external identity to your account — the assistant will then act with your role.
        </p>
        <dl class="text-sm bg-surface-200-800/30 rounded-lg p-3 space-y-1">
          <div class="flex justify-between"><dt class="text-surface-500">Provider</dt><dd class="font-medium capitalize">{preview.provider}</dd></div>
          <div class="flex justify-between"><dt class="text-surface-500">Channel</dt><dd class="font-medium">{preview.channel_name}</dd></div>
          <div class="flex justify-between"><dt class="text-surface-500">External user</dt><dd class="font-mono text-xs">{preview.external_user_id}</dd></div>
        </dl>
        <div class="flex gap-2">
          <Button variant="ghost" onclick={() => goto('/')}>Cancel</Button>
          <Button variant="primary" onclick={confirmLink} loading={confirming}>Confirm link</Button>
        </div>
      {/if}
    </div>
  </Card>
</div>
