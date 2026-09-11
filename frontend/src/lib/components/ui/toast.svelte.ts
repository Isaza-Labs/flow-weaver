// Global toast notification store. Mount <Toaster /> once at the root.
// Usage: import { toast } from '$lib/components/ui'; toast.success('Saved');

import { errorMessage } from '$lib/api/client';

export type ToastTone = 'success' | 'error' | 'warning' | 'info';

export interface Toast {
  id: number;
  tone: ToastTone;
  title: string;
  description?: string;
  /** Auto-dismiss timeout in ms. 0 = sticky. */
  duration: number;
  /** Optional action button (e.g. "Retry"). */
  action?: { label: string; onClick: () => void };
}

export interface ToastInput {
  title: string;
  description?: string;
  duration?: number;
  action?: { label: string; onClick: () => void };
}

const DEFAULT_DURATION: Record<ToastTone, number> = {
  success: 4000,
  info: 4000,
  warning: 6000,
  error: 8000,
};

class ToastStore {
  items = $state<Toast[]>([]);
  private nextId = 1;
  private timers = new Map<number, ReturnType<typeof setTimeout>>();

  private push(tone: ToastTone, input: ToastInput): number {
    const id = this.nextId++;
    const duration = input.duration ?? DEFAULT_DURATION[tone];
    this.items = [...this.items, {
      id, tone,
      title: input.title,
      description: input.description,
      duration,
      action: input.action,
    }];
    if (duration > 0) {
      this.timers.set(id, setTimeout(() => this.dismiss(id), duration));
    }
    return id;
  }

  success(title: string, opts: Omit<ToastInput, 'title'> = {}): number {
    return this.push('success', { title, ...opts });
  }

  error(title: string, opts: Omit<ToastInput, 'title'> = {}): number {
    return this.push('error', { title, ...opts });
  }

  warning(title: string, opts: Omit<ToastInput, 'title'> = {}): number {
    return this.push('warning', { title, ...opts });
  }

  info(title: string, opts: Omit<ToastInput, 'title'> = {}): number {
    return this.push('info', { title, ...opts });
  }

  /** Show an error toast derived from a thrown value (uses ApiError.userMessage when available). */
  fromError(err: unknown, fallbackTitle = 'Action failed', opts: { action?: ToastInput['action'] } = {}): number {
    return this.error(fallbackTitle, { description: errorMessage(err), action: opts.action });
  }

  /** Success toast with an "Undo" action button. Used for destructive operations. */
  undoable(title: string, onUndo: () => void, opts: Omit<ToastInput, 'title' | 'action'> = {}): number {
    return this.push('success', {
      title,
      duration: 6000,
      ...opts,
      action: { label: 'Undo', onClick: onUndo },
    });
  }

  /** Error toast wired with a "Retry" action button. */
  retryable(err: unknown, onRetry: () => void, fallbackTitle = 'Action failed'): number {
    return this.error(fallbackTitle, {
      description: errorMessage(err),
      action: { label: 'Retry', onClick: onRetry },
    });
  }

  dismiss(id: number) {
    const t = this.timers.get(id);
    if (t) {
      clearTimeout(t);
      this.timers.delete(id);
    }
    this.items = this.items.filter((x) => x.id !== id);
  }

  clear() {
    for (const t of this.timers.values()) clearTimeout(t);
    this.timers.clear();
    this.items = [];
  }
}

export const toast = new ToastStore();
