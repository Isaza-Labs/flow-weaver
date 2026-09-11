<script lang="ts">
  import { WifiOff } from 'lucide-svelte';
  import { connectionStore } from '$lib/stores/connection.svelte';
</script>

{#if connectionStore.offline}
  <div
    class="fixed top-0 inset-x-0 z-[60] bg-error-500/95 text-white text-center text-xs font-medium py-1.5 px-4 flex items-center justify-center gap-2 backdrop-blur-sm"
    role="alert"
    aria-live="assertive"
  >
    <WifiOff size={14} />
    <span>Unable to reach the server. Retrying automatically…</span>
  </div>
{/if}
