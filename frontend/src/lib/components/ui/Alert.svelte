<script lang="ts">
  import type { Snippet, Component } from 'svelte';
  import { AlertCircle, CheckCircle2, AlertTriangle, Info, X } from 'lucide-svelte';

  let {
    tone = 'info',
    title,
    dismissible = false,
    onDismiss,
    children,
  }: {
    tone?: 'info' | 'success' | 'warning' | 'error';
    title?: string;
    dismissible?: boolean;
    onDismiss?: () => void;
    children?: Snippet;
  } = $props();

  const config = $derived({
    info: { icon: Info, ring: 'ring-primary-500/30', bg: 'bg-primary-500/10', text: 'text-primary-300' },
    success: { icon: CheckCircle2, ring: 'ring-success-500/30', bg: 'bg-success-500/10', text: 'text-success-300' },
    warning: { icon: AlertTriangle, ring: 'ring-warning-500/30', bg: 'bg-warning-500/10', text: 'text-warning-300' },
    error: { icon: AlertCircle, ring: 'ring-error-500/30', bg: 'bg-error-500/10', text: 'text-error-300' },
  }[tone]);

  const Icon = $derived(config.icon);
</script>

<aside class="flex items-start gap-2.5 p-3 rounded-md ring-1 ring-inset {config.ring} {config.bg} {config.text}">
  <Icon size={16} class="shrink-0 mt-0.5" />
  <div class="flex-1 min-w-0 text-sm">
    {#if title}<div class="font-medium mb-0.5">{title}</div>{/if}
    {#if children}{@render children()}{/if}
  </div>
  {#if dismissible}
    <button type="button" onclick={onDismiss} aria-label="Dismiss" class="shrink-0 opacity-60 hover:opacity-100 transition-opacity">
      <X size={14} />
    </button>
  {/if}
</aside>
