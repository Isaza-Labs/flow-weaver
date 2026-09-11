<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { IconComponent } from './icon';

  let {
    icon,
    title,
    description,
    actions,
  }: {
    icon?: IconComponent;
    title: string;
    description?: string;
    actions?: Snippet;
  } = $props();

  const Icon = $derived(icon);
</script>

<div class="flex flex-col items-center justify-center text-center py-12 px-6">
  {#if Icon}
    <div class="w-12 h-12 rounded-full bg-surface-200-800/60 text-surface-500 flex items-center justify-center mb-4">
      <Icon size={20} />
    </div>
  {/if}
  <h3 class="text-sm font-medium text-surface-800-200">{title}</h3>
  {#if description}
    <p class="text-sm text-surface-500 mt-1 max-w-sm">{description}</p>
  {/if}
  {#if actions}
    <div class="mt-4 flex items-center gap-2">
      {@render actions()}
    </div>
  {/if}
</div>
