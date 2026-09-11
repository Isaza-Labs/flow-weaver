<script lang="ts">
  import { toast, type ToastTone } from './toast.svelte';
  import { CheckCircle2, AlertCircle, AlertTriangle, Info, X } from 'lucide-svelte';
  import { fly, fade } from 'svelte/transition';

  const config: Record<ToastTone, { icon: typeof CheckCircle2; ring: string; bg: string; text: string; iconBg: string; live: 'polite' | 'assertive' }> = {
    success: {
      icon: CheckCircle2,
      ring: 'ring-success-500/30',
      bg: 'bg-surface-100-900/95',
      text: 'text-surface-900-100',
      iconBg: 'bg-success-500/15 text-success-300',
      live: 'polite',
    },
    error: {
      icon: AlertCircle,
      ring: 'ring-error-500/40',
      bg: 'bg-surface-100-900/95',
      text: 'text-surface-900-100',
      iconBg: 'bg-error-500/15 text-error-300',
      live: 'assertive',
    },
    warning: {
      icon: AlertTriangle,
      ring: 'ring-warning-500/30',
      bg: 'bg-surface-100-900/95',
      text: 'text-surface-900-100',
      iconBg: 'bg-warning-500/15 text-warning-300',
      live: 'polite',
    },
    info: {
      icon: Info,
      ring: 'ring-primary-500/30',
      bg: 'bg-surface-100-900/95',
      text: 'text-surface-900-100',
      iconBg: 'bg-primary-500/15 text-primary-300',
      live: 'polite',
    },
  };
</script>

<div
  class="fixed bottom-4 right-4 z-[60] flex flex-col gap-2 w-[min(380px,calc(100vw-2rem))] pointer-events-none"
  aria-label="Notifications"
>
  {#each toast.items as t (t.id)}
    {@const c = config[t.tone]}
    <div
      role="status"
      aria-live={c.live}
      class="pointer-events-auto group flex items-start gap-3 p-3.5 rounded-lg backdrop-blur-md ring-1 ring-inset shadow-lg shadow-black/20 {c.bg} {c.ring} {c.text}"
      in:fly={{ x: 20, duration: 200 }}
      out:fade={{ duration: 150 }}
    >
      <div class="w-7 h-7 rounded-md flex items-center justify-center shrink-0 mt-0.5 {c.iconBg}">
        <c.icon size={15} />
      </div>
      <div class="flex-1 min-w-0">
        <div class="text-sm font-medium leading-snug">{t.title}</div>
        {#if t.description}
          <div class="text-xs text-surface-600-400 mt-1 leading-relaxed">{t.description}</div>
        {/if}
        {#if t.action}
          <button
            type="button"
            onclick={() => { t.action!.onClick(); toast.dismiss(t.id); }}
            class="mt-2 inline-flex items-center h-7 px-2.5 rounded-md text-xs font-medium bg-surface-200-800 hover:bg-surface-300-700 text-surface-800-200 ring-1 ring-surface-300-700 transition-colors cursor-pointer"
          >
            {t.action.label}
          </button>
        {/if}
      </div>
      <button
        type="button"
        onclick={() => toast.dismiss(t.id)}
        aria-label="Dismiss"
        class="shrink-0 p-1 -m-1 rounded text-surface-500 hover:text-surface-800-200 hover:bg-surface-200-800/60 transition-colors cursor-pointer"
      >
        <X size={13} />
      </button>
    </div>
  {/each}
</div>
