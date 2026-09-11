<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Info, AlertTriangle, CheckCircle2, Shield, Pin } from 'lucide-svelte';

  type Tone = 'info' | 'warning' | 'success' | 'admin' | 'where';

  interface Props {
    tone?: Tone;
    title?: string;
    children: Snippet;
  }

  let { tone = 'info', title, children }: Props = $props();

  const icon = $derived.by(() => {
    switch (tone) {
      case 'warning': return AlertTriangle;
      case 'success': return CheckCircle2;
      case 'admin': return Shield;
      case 'where': return Pin;
      default: return Info;
    }
  });

  const cls = $derived.by(() => {
    switch (tone) {
      case 'warning':
        return 'border-warning-500/40 bg-warning-500/8 text-warning-200';
      case 'success':
        return 'border-success-500/40 bg-success-500/8 text-success-200';
      case 'admin':
        return 'border-primary-500/40 bg-primary-500/8 text-primary-200';
      case 'where':
        return 'border-surface-300-700 bg-surface-100-900/50 text-surface-700-300';
      default:
        return 'border-primary-500/30 bg-primary-500/5 text-surface-700-300';
    }
  });

  const Icon = $derived(icon);
</script>

<div class="my-3 flex gap-2.5 items-start rounded-md border px-3 py-2.5 text-[13px] {cls}">
  <Icon size={14} class="mt-0.5 shrink-0" />
  <div class="min-w-0 flex-1 space-y-1">
    {#if title}
      <div class="font-semibold text-[13px] leading-tight">{title}</div>
    {/if}
    <div class="leading-relaxed [&_p:last-child]:mb-0 [&_p]:mb-1">
      {@render children()}
    </div>
  </div>
</div>
