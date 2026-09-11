// Reactive confirm-dialog state — drop-in replacement for window.confirm().
//
// Usage:
//   import { confirm } from '$lib/components/ui/confirm.svelte';
//   if (await confirm({ title: 'Delete X?', message: '...', tone: 'danger' })) { ... }
//
// Mount <ConfirmHost /> once in the root layout to render the actual dialog.

export interface ConfirmOptions {
  title: string;
  message?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  tone?: 'primary' | 'danger';
}

class ConfirmStore {
  open = $state(false);
  options = $state<ConfirmOptions | null>(null);
  private resolver: ((v: boolean) => void) | null = null;

  ask(opts: ConfirmOptions): Promise<boolean> {
    this.options = opts;
    this.open = true;
    return new Promise((resolve) => {
      this.resolver = resolve;
    });
  }

  resolve(v: boolean) {
    this.open = false;
    const r = this.resolver;
    this.resolver = null;
    queueMicrotask(() => {
      this.options = null;
      r?.(v);
    });
  }
}

export const confirmStore = new ConfirmStore();

export function confirm(opts: ConfirmOptions): Promise<boolean> {
  return confirmStore.ask(opts);
}
