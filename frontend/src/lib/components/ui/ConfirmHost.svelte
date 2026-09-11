<script lang="ts">
  import Dialog from './Dialog.svelte';
  import Button from './Button.svelte';
  import { confirmStore } from './confirm.svelte';
</script>

{#if confirmStore.options}
  {@const opts = confirmStore.options}
  <Dialog
    bind:open={confirmStore.open}
    title={opts.title}
    size="sm"
    onClose={() => confirmStore.resolve(false)}
  >
    {#if opts.message}
      <p class="text-sm text-surface-700-300 leading-relaxed">{opts.message}</p>
    {/if}
    {#snippet footer()}
      <Button variant="ghost" onclick={() => confirmStore.resolve(false)}>{opts.cancelLabel ?? 'Cancel'}</Button>
      <Button
        variant={opts.tone === 'danger' ? 'danger' : 'primary'}
        onclick={() => confirmStore.resolve(true)}
      >{opts.confirmLabel ?? 'Confirm'}</Button>
    {/snippet}
  </Dialog>
{/if}
