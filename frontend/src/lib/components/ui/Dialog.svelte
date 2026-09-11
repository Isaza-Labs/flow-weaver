<script lang="ts">
  import { onMount } from 'svelte';
  import type { Snippet } from 'svelte';
  import { X } from 'lucide-svelte';
  import { animate, prefersReducedMotion } from '$lib/anim';

  let {
    open = $bindable(false),
    title,
    description,
    size = 'md',
    closable = true,
    onClose,
    children,
    footer,
  }: {
    open?: boolean;
    title?: string;
    description?: string;
    size?: 'sm' | 'md' | 'lg' | 'xl' | 'full';
    closable?: boolean;
    onClose?: () => void;
    children: Snippet;
    footer?: Snippet;
  } = $props();

  let dialogEl = $state<HTMLDivElement | null>(null);
  let prevFocus = $state<HTMLElement | null>(null);

  const sizeClass = $derived({
    sm: 'max-w-md',
    md: 'max-w-lg',
    lg: 'max-w-2xl',
    xl: 'max-w-4xl',
    full: 'max-w-[95vw]',
  }[size]);

  function close() {
    if (!closable) return;
    open = false;
    onClose?.();
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      close();
    } else if (e.key === 'Tab' && dialogEl) {
      const focusable = dialogEl.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
      );
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    }
  }

  $effect(() => {
    if (open) {
      prevFocus = document.activeElement as HTMLElement;
      // Focus the dialog after render
      queueMicrotask(() => {
        const focusable = dialogEl?.querySelector<HTMLElement>(
          'input:not([disabled]), button:not([disabled]), textarea:not([disabled]), select:not([disabled])'
        );
        focusable?.focus();
        if (dialogEl && !prefersReducedMotion()) {
          animate(dialogEl, {
            opacity: [0, 1],
            scale: [0.95, 1],
            translateY: [8, 0],
            duration: 220,
            ease: 'outCubic',
          });
        }
      });
      document.body.style.overflow = 'hidden';
    } else {
      document.body.style.overflow = '';
      prevFocus?.focus();
    }
    return () => {
      document.body.style.overflow = '';
    };
  });
</script>

{#if open}
  <div
    class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-surface-950/70 backdrop-blur-sm animate-in fade-in"
    onclick={(e) => { if (e.target === e.currentTarget) close(); }}
    onkeydown={handleKeydown}
    role="presentation"
  >
    <div
      bind:this={dialogEl}
      class="w-full {sizeClass} max-h-[90vh] flex flex-col bg-surface-100-900 border border-surface-300-700 rounded-xl shadow-2xl"
      role="dialog"
      aria-modal="true"
      aria-labelledby={title ? 'dialog-title' : undefined}
      tabindex="-1"
    >
      {#if title || closable}
        <header class="flex items-start justify-between gap-3 px-5 py-4 border-b border-surface-200-800">
          <div class="min-w-0 flex-1">
            {#if title}
              <h2 id="dialog-title" class="text-base font-semibold tracking-tight text-surface-900-100">{title}</h2>
            {/if}
            {#if description}
              <p class="text-sm text-surface-600-400 mt-1">{description}</p>
            {/if}
          </div>
          {#if closable}
            <button
              type="button"
              onclick={close}
              class="p-1 rounded text-surface-500 hover:text-surface-800-200 hover:bg-surface-200-800 transition-colors"
              aria-label="Close dialog"
            >
              <X size={16} />
            </button>
          {/if}
        </header>
      {/if}

      <div class="flex-1 overflow-y-auto px-5 py-4">
        {@render children()}
      </div>

      {#if footer}
        <footer class="flex items-center justify-end gap-2 px-5 py-3 border-t border-surface-200-800">
          {@render footer()}
        </footer>
      {/if}
    </div>
  </div>
{/if}
