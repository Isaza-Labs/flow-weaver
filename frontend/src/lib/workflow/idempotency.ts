// Shared rollback-policy helpers.
//
// The backend's `WorkflowRollbackAnalyzer` (Services/Promotion) computes
// the effective IdempotencyKind for every node by:
//   1. starting from the handler's `DefaultIdempotency` (per snippet type),
//   2. accepting a per-snippet override IF it doesn't soften below the
//      handler floor.
//
// The frontend mirrors that logic here so the snippet listing, the
// workflow canvas, and any future surface can render the *effective*
// policy without round-tripping to the analyzer endpoint. Drift
// between this map and the C# defaults would be cosmetic — the
// backend remains the source of truth at promote/rollback time.
//
// Keep this map in sync with the C# `DefaultIdempotency` declarations
// under `flow_weaver_backend/Services/Worker/Handlers/*.cs`.
export type IdempotencyKind = 'idempotent' | 'requires_compensation' | 'non_reversible';

export const HANDLER_FLOOR: Record<string, IdempotencyKind> = {
  ssh: 'non_reversible',
  ansible_playbook: 'non_reversible',
  // A delivered email cannot be recalled — same class as slack_message.
  email_send: 'non_reversible',
  slack_message: 'non_reversible',
  git: 'requires_compensation',
  netconf: 'requires_compensation',
  integration_action: 'requires_compensation',
  mcp_call: 'requires_compensation',
  rest_call: 'requires_compensation',
  python_snippet: 'requires_compensation',
  ping: 'idempotent',
  transform: 'idempotent',
  snmp_v3: 'idempotent',
  report: 'idempotent',
};

const RANK: Record<IdempotencyKind, number> = {
  idempotent: 0,
  requires_compensation: 1,
  non_reversible: 2,
};

export function effectiveIdempotency(
  handlerType: string | null | undefined,
  override: IdempotencyKind | string | null | undefined,
): IdempotencyKind {
  const floor = HANDLER_FLOOR[handlerType ?? ''] ?? 'requires_compensation';
  if (!override) return floor;
  const ov = override as IdempotencyKind;
  if (!(ov in RANK)) return floor;
  // Floor wins when stricter. Override can only RAISE risk, never lower
  // below the handler's declared minimum.
  return RANK[ov] >= RANK[floor] ? ov : floor;
}

export function idempotencyTone(value: IdempotencyKind): 'success' | 'warning' | 'error' {
  if (value === 'idempotent') return 'success';
  if (value === 'requires_compensation') return 'warning';
  return 'error';
}

export function idempotencyShort(value: IdempotencyKind): string {
  if (value === 'idempotent') return 'idempotent';
  if (value === 'requires_compensation') return 'compensable';
  return 'non-reversible';
}
