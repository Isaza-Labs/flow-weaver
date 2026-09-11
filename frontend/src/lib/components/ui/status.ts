export type StatusTone = 'success' | 'error' | 'warning' | 'info' | 'neutral' | 'primary';

const STATUS_MAP: Record<string, StatusTone> = {
  success: 'success',
  completed: 'success',
  healthy: 'success',
  enabled: 'success',
  active: 'success',
  up: 'success',

  failed: 'error',
  failure: 'error',
  error: 'error',
  unhealthy: 'error',
  down: 'error',

  pending: 'warning',
  queued: 'warning',
  warning: 'warning',
  disabled: 'warning',
  // Integration health: reachable but the credentials were never validated
  // by any probed endpoint (see IntegrationHealthChecker).
  degraded: 'warning',

  running: 'info',
  in_progress: 'info',

  cancelled: 'neutral',
  skipped: 'neutral',
  unknown: 'neutral',
};

export function statusTone(status: string | null | undefined): StatusTone {
  if (!status) return 'neutral';
  return STATUS_MAP[status.toLowerCase()] ?? 'neutral';
}

const TONE_BADGE: Record<StatusTone, string> = {
  success: 'bg-success-500/15 text-success-300 ring-1 ring-success-500/30',
  error: 'bg-error-500/15 text-error-300 ring-1 ring-error-500/30',
  warning: 'bg-warning-500/15 text-warning-300 ring-1 ring-warning-500/30',
  info: 'bg-primary-500/15 text-primary-300 ring-1 ring-primary-500/30',
  primary: 'bg-primary-500/15 text-primary-300 ring-1 ring-primary-500/30',
  neutral: 'bg-surface-500/15 text-surface-300 ring-1 ring-surface-500/30',
};

export function toneBadgeClass(tone: StatusTone): string {
  return TONE_BADGE[tone];
}

const TONE_DOT: Record<StatusTone, string> = {
  success: 'bg-success-500',
  error: 'bg-error-500',
  warning: 'bg-warning-500',
  info: 'bg-primary-500',
  primary: 'bg-primary-500',
  neutral: 'bg-surface-500',
};

export function toneDotClass(tone: StatusTone): string {
  return TONE_DOT[tone];
}
