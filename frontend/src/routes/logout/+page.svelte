<script lang="ts">
  import { goto } from '$app/navigation';
  import { onMount } from 'svelte';
  import { auth } from '$lib/api/client';
  import { Spinner } from '$lib/components/ui';

  onMount(async () => {
    // `auth.logout()` always clears the local store, even if the server
    // call fails (offline, etc). Then we send the user back to login.
    await auth.logout();
    goto('/login');
  });
</script>

<svelte:head>
  <title>Signing out…</title>
</svelte:head>

<div class="min-h-screen flex items-center justify-center">
  <div class="flex flex-col items-center gap-3 text-surface-500">
    <Spinner />
    <span class="text-sm">Signing out…</span>
  </div>
</div>
