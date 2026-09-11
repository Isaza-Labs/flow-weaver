// .NET backend is mounted at /api (no version prefix). In dev the Vite proxy
// in vite.config.ts forwards /api → http://localhost:8080. In prod the
// frontend is served from the same host as the backend so the relative
// path works unchanged.
const BASE = '/api';

import { authStore, sessionFromResponse, type LoginResponseShape } from '$lib/stores/auth.svelte';
import { connectionStore } from '$lib/stores/connection.svelte';
import { goto } from '$app/navigation';
import { browser } from '$app/environment';

export interface ListResponse<T> {
  data: T[];
  total: number;
  limit: number;
  offset: number;
}

// ─────────────────────────────────────────────────────────────────────
//  Anti-corruption layer
//  The backend uses snake_case DTOs where primary keys are named after
//  the entity (device_id, workflow_id, …) and Device.name is exposed
//  as device_name. The UI was written against {id, name}, so we keep
//  the UI shapes below and translate both ways right here.
// ─────────────────────────────────────────────────────────────────────

type FieldMap = Record<string, string>; // client key → backend key

/**
 * Convert a single record coming from the backend into the UI shape:
 * copies backend keys to their client aliases (e.g. `device_id` → `id`,
 * `device_name` → `name`) without deleting the originals.
 */
function fromBackend<T>(data: unknown, map: FieldMap): T {
  if (data === null || typeof data !== 'object') return data as T;
  if (Array.isArray(data)) return data as unknown as T;
  const rec = data as Record<string, unknown>;
  const out: Record<string, unknown> = { ...rec };
  for (const [clientKey, backendKey] of Object.entries(map)) {
    if (backendKey in rec && !(clientKey in rec)) {
      out[clientKey] = rec[backendKey];
    }
  }
  return out as T;
}

/**
 * Convert a UI payload into the backend shape: maps client keys back to
 * their backend names and strips the synthesized `id` (the id lives in
 * the URL path, never in the body for updates).
 */
function toBackend(data: unknown, map: FieldMap): unknown {
  if (data === null || typeof data !== 'object' || Array.isArray(data)) return data;
  const rec = data as Record<string, unknown>;
  const out: Record<string, unknown> = { ...rec };
  for (const [clientKey, backendKey] of Object.entries(map)) {
    if (clientKey in rec && clientKey !== backendKey) {
      out[backendKey] = rec[clientKey];
      delete out[clientKey];
    }
  }
  delete out.id;
  return out;
}

/**
 * Backend list endpoints return the ListResponse envelope
 * `{ data, total, limit, offset }` (Sprint 1.5). Map each item through
 * fromBackend so the UI keeps seeing `.id` / `.name`; pass the envelope
 * metadata through unchanged.
 *
 * Also tolerates the legacy shape (bare array) for backward compatibility
 * during the rollout — if it ever appears we wrap it synthetically.
 */
function listFromBackend<T>(data: unknown, map: FieldMap, limit: number, offset: number): ListResponse<T> {
  if (Array.isArray(data)) {
    const mapped = data.map((item) => fromBackend<T>(item, map));
    return { data: mapped, total: mapped.length, limit, offset };
  }
  if (data && typeof data === 'object') {
    const envelope = data as { data?: unknown[]; total?: number; limit?: number; offset?: number };
    const items = Array.isArray(envelope.data) ? envelope.data : [];
    return {
      data: items.map((item) => fromBackend<T>(item, map)),
      total: envelope.total ?? items.length,
      limit: envelope.limit ?? limit,
      offset: envelope.offset ?? offset,
    };
  }
  return { data: [], total: 0, limit, offset };
}

// ─────────────────────────────────────────────────────────────────────
//  Entity interfaces  (UI shape: keeps `id` and `name` for compatibility)
// ─────────────────────────────────────────────────────────────────────

export interface Device {
  id: string;
  name: string;
  ip_address: string | null;
  platform: string | null;
  vendor: string | null;
  model: string | null;
  os_version: string | null;
  site: string | null;
  role: string | null;
  source_id?: string | null;
  external_id?: string | null;
  status?: string | null;
  last_sync_at?: string | null;
  properties: Record<string, unknown>;
  credential_id: string | null;
  // Which workflow environments may dispatch to this device. A run drops
  // every target that doesn't allow its environment, and refuses outright
  // when that leaves nothing. Any combination is valid, including none
  // (parked) and all three (reachable from anywhere).
  allow_draft?: boolean;
  allow_qa?: boolean;
  allow_production?: boolean;
  // (device) Pinned SSH host key, `SHA256:<base64>`. When set, an ssh step refuses to
  // connect if the device presents a different key. Null = unpinned; the first
  // successful connect pins whatever it sees (trust-on-first-use). Send '' to
  // clear the pin — e.g. after a legitimate key rotation.
  expected_ssh_host_key_fingerprint?: string | null;
  created_at: string;
  updated_at: string;
}

export interface InventorySource {
  id: string;
  name: string;
  type: string;
  description: string | null;
  config: Record<string, unknown>;
  mapping: Record<string, unknown>;
  sync_mode: string;
  enabled: boolean;
  last_sync_at: string | null;
  last_sync_status: string | null;
  created_at: string;
  updated_at: string;
}

export interface Snippet {
  id: string;
  name: string;
  type: string;
  description: string | null;
  code: string | null;
  script_language: string | null;
  input_schema: Record<string, unknown>;
  output_schema: Record<string, unknown>;
  target_mode: string;
  max_parallel: number;
  timeout_seconds: number;
  retry_policy: Record<string, unknown> | null;
  // Mermaid source describing the task's internal logic. Required by
  // the backend for python_snippet / transform types; null for built-in
  // handlers whose behavior is self-evident from `type`.
  logic_diagram_mermaid: string | null;
  // S13.6: per-snippet override of the handler's idempotency default.
  // Null means "fall back to the handler's DefaultIdempotency".
  idempotency: 'idempotent' | 'requires_compensation' | 'non_reversible' | null;
  // Whether a step running this snippet CHANGES anything — a different question
  // from idempotency, which asks whether it could be undone. Nothing in this UI
  // edits it yet; it is carried so a DUPLICATE keeps it. Dropping it on a copy
  // would look harmless and then fail every step that used it: for a
  // python_snippet, null means "the author has not said" and the step refuses to
  // guess (the node's `config_overrides.changes` is the other place to say it).
  changes_state?: boolean | null;
  // python_snippet only: runs in the relaxed (network-enabled) sandbox with
  // netmiko/paramiko for interactive SSH. Admin-gated on the backend.
  network_enabled?: boolean;
  verified?: boolean;
  // How many times a step using this snippet reached `completed`, and when it
  // last did. Derived from step_runs on read — 0 means nobody has ever got this
  // snippet to finish, so the editor palette files it under "Unproven" rather
  // than mixing a draft in with the reusable blocks. Absent (undefined) on the
  // response to a create/update, where the count would be meaningless.
  completed_run_count?: number;
  last_completed_run_at?: string | null;
  created_by?: string | null;
  created_at: string;
}

export interface Workflow {
  id: string;
  name: string;
  description: string | null;
  version: number;
  schema_version?: number;
  input_schema: Record<string, unknown>;
  nodes: WorkflowNode[];
  edges: WorkflowEdge[];
  metadata: Record<string, unknown>;
  // Non-blocking advisory issues the backend ships so the UI can
  // render fix suggestions next to the affected nodes. Shape is
  // [{ node_id?, severity, code, message, hint? }].
  warnings?: Array<Record<string, unknown>>;
  created_at: string;
  updated_at: string;
  environment: string;
  promoted_from?: string;
  change_summary?: string;
  promoted_at?: string;
  conversation_id?: string;
}

export interface WorkflowTrigger {
  id: string;
  workflow_id: string;
  name: string;
  type: string;
  description: string | null;
  route: string | null;
  // Webhook triggers (type === 'webhook'): relative ingest path, whether a
  // signing secret is set, and the unsigned-allowed opt-in. `webhook_secret` is
  // the plaintext returned ONCE by create / rotate-secret; null on reads.
  webhook_path?: string | null;
  has_webhook_secret?: boolean;
  allow_unsigned?: boolean;
  webhook_secret?: string | null;
  input_schema: Record<string, unknown>;
  input_defaults: Record<string, unknown>;
  enabled: boolean;
  cron_expression?: string | null;
  timezone?: string;
  next_run_at?: string | null;
  last_run_at?: string | null;
  last_run_status?: string | null;
  notification_webhook_url?: string | null;
  notify_on?: string[];
  target_devices?: string[];
  // Webhook triggers only: may the delivery body pick targets via
  // `target_devices` / `target_pools`? Default false — the body is otherwise
  // ignored for targeting and the run uses `target_devices` above. Even when
  // true, a scoped trigger only lets the body NARROW that list.
  allow_target_override?: boolean;
  created_at: string;
  updated_at: string;
}

export interface WorkflowPlan {
  id: string;
  conversation_id?: string | null;
  intent: string;
  description: string | null;
  steps: Record<string, unknown>;
  services_to_create: Record<string, unknown>;
  services_to_reuse: Record<string, unknown>;
  target_devices: string[];
  target_pools: string[];
  risks: Record<string, unknown>;
  status: 'draft' | 'awaiting_approval' | 'approved' | 'rejected' | 'building' | 'built' | 'failed';
  rejection_reason: string | null;
  approved_by: string | null;
  approved_at: string | null;
  executed_at: string | null;
  workflow_id: string | null;
  created_at: string;
  updated_at: string;
}

export interface WorkflowVersion {
  id: string;
  workflow_id: string;
  version: number;
  nodes: WorkflowNode[];
  edges: WorkflowEdge[];
  services: unknown[];
  change_summary: string;
  promoted_at: string;
  promoted_by: string;
  test_run_id?: string;
  created_at: string;
}

export interface DiffResult {
  nodes_added: unknown[];
  nodes_removed: unknown[];
  nodes_changed: { node_id: string; before: unknown; after: unknown }[];
  edges_added: unknown[];
  edges_removed: unknown[];
  has_changes: boolean;
}

export interface WorkflowNode {
  id: string;
  snippet_id: string;
  x: number;
  y: number;
  config_overrides?: Record<string, unknown>;
}

export interface WorkflowEdge {
  source: string;
  target: string;
  type: 'success' | 'failure' | 'always' | 'conditional';
  condition?: string | null;
  // Visual-only — identifies which handle on each node the edge attaches
  // to (e.g. 's-right', 't-left'). The engine ignores these; the frontend
  // uses them to render the edge from the side the user chose.
  source_handle?: string | null;
  target_handle?: string | null;
}

export interface WorkflowRun {
  id: string;
  workflow_id: string;
  status: string;
  input_payload: Record<string, unknown>;
  target_devices: string[];
  target_pools: string[];
  trigger: string;
  // User UUID who enqueued the run, or "00000000-…" for non-user
  // triggers (webhook, schedule). Stored as a string by the backend
  // since some legacy rows carry a sentinel like "scheduler".
  created_by?: string | null;
  started_at: string | null;
  completed_at: string | null;
  // Why the run failed when NO step can say — orchestration failing before or
  // between steps (an unresolvable snippet_id, a malformed DAG, an empty target
  // set). Null on success and on ordinary step failures, where the failing
  // step's own error is the better answer.
  error?: string | null;
  created_at: string;
}

export interface StepRun {
  id: string;
  workflow_run_id: string;
  node_id: string;
  device_id: string | null;
  status: string;
  input_payload?: Record<string, unknown>;
  output_payload: Record<string, unknown>;
  logs: string;
  error: string;
  started_at: string | null;
  completed_at: string | null;
  worker_id: string | null;
}

export interface Skill {
  id: string;
  name: string;
  triggers: string[];
  description: string | null;
  skill_type: string;
  action_config: Record<string, unknown>;
  tags: string[];
  use_count: number;
  created_at: string;
}

export interface AIProvider {
  id: string;
  name: string;
  type: string;
  base_url: string | null;
  default_model: string;
  config: Record<string, unknown>;
  enabled: boolean;
  created_at: string;
}

export interface AIAgent {
  id: string;
  name: string;
  role: string;
  description: string | null;
  provider_id: string | null;
  model_override: string | null;
  system_prompt: string;
  tools: string[];
  max_iterations: number;
  temperature: number;
  enabled: boolean;
  created_at: string;
}

// One entry of the server's runtime tool registry (GET /aiagent/tools) — feeds
// the agent form's allowlist picker so users pick real tool names instead of
// typing them. domain/tier come from the permission matrix; null = unclassified.
export interface AiToolInfo {
  name: string;
  description: string;
  domain: string | null;
  tier: string | null;
}

export interface ChatResponse {
  conversation_id: string;
  response: string;
  agent_name: string;
  tokens_used: number;
}

export interface ApiOperation {
  operation_id: string;
  api: string;
  method: string;
  path: string;
  summary: string;
  description: string;
  tags: string[];
}

// ─── Per-entity field maps (client key → backend key) ──────────────────

const DEVICE_MAP: FieldMap = { id: 'device_id', name: 'device_name' };
const INVENTORY_SOURCE_MAP: FieldMap = { id: 'inventory_source_id' };
const SNIPPET_MAP: FieldMap = { id: 'snippet_id' };
const WORKFLOW_MAP: FieldMap = { id: 'workflow_id' };
const WORKFLOW_TRIGGER_MAP: FieldMap = { id: 'workflow_trigger_id' };
const WORKFLOW_PLAN_MAP: FieldMap = { id: 'workflow_plan_id' };
const SKILL_MAP: FieldMap = { id: 'skill_id' };
const INTEGRATION_MAP: FieldMap = { id: 'integration_id' };
const INTEGRATION_ACTION_MAP: FieldMap = { id: 'integration_action_id' };
const AI_PROVIDER_MAP: FieldMap = { id: 'ai_provider_id' };
const AI_AGENT_MAP: FieldMap = { id: 'ai_agent_id' };

// ─────────────────────────────────────────────────────────────────────
//  Error handling (user-safe messages)
// ─────────────────────────────────────────────────────────────────────

export type ApiErrorKind =
  | 'network'
  | 'timeout'
  | 'cancelled'
  | 'unauthorized'
  | 'forbidden'
  | 'not_found'
  | 'conflict'
  | 'validation'
  | 'rate_limited'
  | 'server'
  | 'unknown';

export class ApiError extends Error {
  kind: ApiErrorKind;
  status: number;
  body: unknown;
  userMessage: string;

  constructor(opts: { kind: ApiErrorKind; status: number; message: string; userMessage: string; body?: unknown }) {
    super(opts.message);
    this.name = 'ApiError';
    this.kind = opts.kind;
    this.status = opts.status;
    this.body = opts.body;
    this.userMessage = opts.userMessage;
  }
}

function userMessageFor(kind: ApiErrorKind, status: number): string {
  switch (kind) {
    case 'network':
      return 'Could not reach the server. Check your connection or try again in a few minutes.';
    case 'timeout':
      return 'The request took too long. Please try again.';
    case 'cancelled':
      return 'Request cancelled.';
    case 'unauthorized':
      return 'Your session has expired. Please sign in again.';
    case 'forbidden':
      return 'You don\u2019t have permission to perform this action.';
    case 'not_found':
      return 'We couldn\u2019t find what you\u2019re looking for. It may have been deleted.';
    case 'conflict':
      return 'There\u2019s a conflict with the current state. Refresh the page and try again.';
    case 'validation':
      return 'The submitted data is invalid. Please review the form.';
    case 'rate_limited':
      return 'Too many requests. Wait a few seconds and try again.';
    case 'server':
      return 'The server had a problem processing your request. Please try again in a few minutes.';
    default:
      return `Something went wrong (code ${status || 'unknown'}). Please try again.`;
  }
}

function kindForStatus(status: number): ApiErrorKind {
  if (status === 401) return 'unauthorized';
  if (status === 403) return 'forbidden';
  if (status === 404) return 'not_found';
  if (status === 409) return 'conflict';
  if (status === 400 || status === 422) return 'validation';
  if (status === 429) return 'rate_limited';
  if (status >= 500 && status <= 599) return 'server';
  return 'unknown';
}

const DEFAULT_TIMEOUT_MS = 30_000;

interface ApiOptions extends Omit<RequestInit, 'signal'> {
  signal?: AbortSignal;
  timeoutMs?: number;
}

// Endpoints that must bypass the auth interceptor: hitting them with a
// stale token and then trying to refresh inside would either loop or leak
// the refresh token into the failing request.
const UNAUTHENTICATED_PATHS = [
  '/auth/login',
  '/auth/refresh',
  '/auth/bootstrap',
];

function isUnauthenticatedPath(path: string): boolean {
  return UNAUTHENTICATED_PATHS.some((p) => path.startsWith(p));
}

// Single-flight refresh WITHIN a tab (also the fallback when the Web Locks API
// is unavailable): concurrent 401s share one round-trip.
let refreshInFlight: Promise<boolean> | null = null;

// An access token with more than this much life left is "fresh enough" — the
// same margin isExpiringSoon uses. Inside the refresh lock we treat a token
// this fresh as "another tab already rotated it", so we don't refresh again.
const REFRESH_FRESH_MARGIN_MS = 30_000;

export async function tryRefresh(): Promise<boolean> {
  // Cross-tab single-flight. All tabs share ONE rotating refresh token; if two
  // refresh at once the loser presents a just-rotated token, the backend flags
  // it as reuse and revokes the whole chain — logging every tab out. The Web
  // Locks API serializes the refresh to one tab at a time; when it's
  // unavailable we fall back to the per-tab single-flight below.
  if (browser && typeof navigator !== 'undefined' && navigator.locks) {
    return navigator.locks.request('flowweaver:auth-refresh', () => refreshCore());
  }
  return refreshCore();
}

function refreshCore(): Promise<boolean> {
  refreshInFlight ??= (async () => {
    try {
      // We may have waited on the cross-tab lock while another tab rotated the
      // token. Re-read the persisted session; if its access token is still
      // fresh, adopt it and skip the network refresh — refreshing here would
      // rotate again and revoke the token the other tab just obtained.
      const persisted = authStore.syncFromStorage();
      if (persisted && persisted.expiresAt - Date.now() > REFRESH_FRESH_MARGIN_MS) {
        return true;
      }

      const token = persisted?.refreshToken;
      if (!token) return false;

      const res = await fetch(`${BASE}/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refresh_token: token }),
      });
      if (!res.ok) return false;
      const body = (await res.json()) as LoginResponseShape;
      authStore.set(sessionFromResponse(body));
      return true;
    } catch {
      return false;
    } finally {
      // Reset so the next 401 can refresh again if the access token
      // expires while the user keeps using the app.
      refreshInFlight = null;
    }
  })();
  return refreshInFlight;
}

async function doFetch(path: string, options: ApiOptions, signal: AbortSignal): Promise<Response> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(options.headers as Record<string, string> | undefined),
  };
  // Only attach the bearer when we have one AND the endpoint is not an
  // explicitly-anonymous auth route.
  const token = authStore.accessToken;
  if (token && !isUnauthenticatedPath(path)) {
    headers['Authorization'] = `Bearer ${token}`;
  }
  return fetch(`${BASE}${path}`, {
    ...options,
    headers,
    signal,
  });
}

/**
 * `fetch` with the bearer attached, and the same 401 policy `api()` has:
 * refresh once, replay once, and if the refresh fails, end the session.
 *
 * It exists because a few endpoints cannot go through `api()` — they send a raw
 * body (an uploaded file) or read a stream, and `api()` sets
 * `Content-Type: application/json` and JSON-parses the response. Those callers
 * each hand-rolled `fetch`, and each one dropped the refresh with it.
 *
 * That is not a cosmetic difference. The access token lives 15 minutes
 * (`Jwt:AccessTokenMinutes`), so on a tab open longer than that the first action
 * the user takes decides what they see: through `api()` it refreshes and works,
 * through a hand-rolled fetch it returns a bare 401 that reads like a
 * permissions problem. The import wizard's Analyze button is the one people hit,
 * because uploading a file is often the first thing done after reading a page
 * for a while.
 *
 * The body must be replayable — a string, a Blob or a TypedArray. A
 * ReadableStream request body cannot be sent twice, and no caller here uses one.
 */
export async function authedFetch(url: string, init: RequestInit = {}): Promise<Response> {
  const send = () => {
    // Re-read the token on every attempt: the replay has to carry the REFRESHED
    // one, and capturing it once outside this closure is precisely the bug.
    const token = authStore.accessToken;
    const headers: Record<string, string> = {
      ...(init.headers as Record<string, string> | undefined),
    };
    if (token) headers['Authorization'] = `Bearer ${token}`;
    return fetch(url, { ...init, headers });
  };

  const res = await send();
  if (res.status !== 401 || !authStore.refreshToken) return res;

  if (await tryRefresh()) return send();

  // The refresh failed, so the session is over — not just this request. Handled
  // the way `api()` handles it, so an expired session ends in one place instead
  // of surfacing as an unexplained 401 on whichever screen the user was on.
  if (browser) {
    authStore.clear();
    const current = window.location.pathname + window.location.search;
    if (!current.startsWith('/login')) {
      void goto(`/login?redirect=${encodeURIComponent(current)}`);
    }
  }
  return res;
}

async function api<T>(path: string, options?: ApiOptions): Promise<T> {
  const { signal: userSignal, timeoutMs = DEFAULT_TIMEOUT_MS, ...rest } = options ?? {};

  const controller = new AbortController();
  const timeoutHandle = setTimeout(
    () => controller.abort(new DOMException('Timeout', 'TimeoutError')),
    timeoutMs,
  );
  if (userSignal) {
    if (userSignal.aborted) controller.abort(userSignal.reason);
    else userSignal.addEventListener('abort', () => controller.abort(userSignal.reason), { once: true });
  }

  let res: Response;
  try {
    res = await doFetch(path, rest, controller.signal);

    // One retry on 401: refresh token, then replay the original request.
    // Skip for unauthenticated paths — they legitimately return 401 on
    // bad creds and there's nothing to refresh.
    if (res.status === 401 && !isUnauthenticatedPath(path) && authStore.refreshToken) {
      const refreshed = await tryRefresh();
      if (refreshed) {
        res = await doFetch(path, rest, controller.signal);
      } else if (browser) {
        // Refresh failed — session is dead. Clear + kick to login.
        authStore.clear();
        const current = window.location.pathname + window.location.search;
        if (!current.startsWith('/login')) {
          void goto(`/login?redirect=${encodeURIComponent(current)}`);
        }
      }
    }
  } catch (err) {
    if (err instanceof DOMException) {
      if (err.name === 'TimeoutError') {
        connectionStore.reportFailure();
        throw new ApiError({ kind: 'timeout', status: 0, message: 'Request timed out', userMessage: userMessageFor('timeout', 0) });
      }
      if (err.name === 'AbortError') {
        const cancelledByUs = controller.signal.aborted && !userSignal?.aborted;
        const kind: ApiErrorKind = cancelledByUs ? 'timeout' : 'cancelled';
        if (kind === 'timeout') connectionStore.reportFailure();
        throw new ApiError({ kind, status: 0, message: err.message || 'Aborted', userMessage: userMessageFor(kind, 0) });
      }
    }
    connectionStore.reportFailure();
    throw new ApiError({
      kind: 'network',
      status: 0,
      message: err instanceof Error ? err.message : 'Network error',
      userMessage: userMessageFor('network', 0),
    });
  } finally {
    clearTimeout(timeoutHandle);
  }

  // We got a response from the backend — even an error status counts as
  // "reachable" for the connection banner.
  connectionStore.reportSuccess();

  if (!res.ok) {
    const body = await res.json().catch(() => ({ error: res.statusText }));
    const serverMessage = (body as { error?: string }).error;
    const kind = kindForStatus(res.status);
    // Prefer the server's `error` string whenever it's set, for any 4xx —
    // the backend deliberately includes actionable text there (e.g. a
    // 409 saying "integration X still in needs_config" or a 403 with a
    // specific permission name). Only fall back to the generic kind-based
    // message for 5xx (which often surface internals we don't want to
    // leak verbatim) and for the network/timeout/cancelled buckets that
    // never hit this branch.
    const passThroughServerMessage =
      typeof serverMessage === 'string'
      && serverMessage.length > 0
      && res.status >= 400 && res.status < 500;
    throw new ApiError({
      kind,
      status: res.status,
      message: serverMessage || res.statusText || `HTTP ${res.status}`,
      userMessage: passThroughServerMessage
        ? serverMessage
        : userMessageFor(kind, res.status),
      body,
    });
  }
  if (res.status === 204) return {} as T;
  return res.json();
}

export function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.userMessage;
  if (err instanceof Error) return 'Something went wrong. Please try again.';
  return 'Something went wrong. Please try again.';
}

export function isOfflineError(err: unknown): boolean {
  return err instanceof ApiError && (err.kind === 'network' || err.kind === 'timeout');
}

// ─────────────────────────────────────────────────────────────────────
//  Entity clients — paths match the singular .NET controllers
// ─────────────────────────────────────────────────────────────────────

// Devices — /api/device
export const devices = {
  list: async (limit = 50, offset = 0) =>
    listFromBackend<Device>(await api<unknown>(`/device?limit=${limit}&offset=${offset}`), DEVICE_MAP, limit, offset),
  get: async (id: string) => fromBackend<Device>(await api<unknown>(`/device/${id}`), DEVICE_MAP),
  create: async (data: Partial<Device>) =>
    fromBackend<Device>(
      await api<unknown>('/device', { method: 'POST', body: JSON.stringify(toBackend(data, DEVICE_MAP)) }),
      DEVICE_MAP,
    ),
  update: async (id: string, data: Partial<Device>) =>
    fromBackend<Device>(
      await api<unknown>(`/device/${id}`, { method: 'PUT', body: JSON.stringify(toBackend(data, DEVICE_MAP)) }),
      DEVICE_MAP,
    ),
  delete: (id: string) => api<void>(`/device/${id}`, { method: 'DELETE' }),
};

// InventorySources — /api/inventorysource
export const inventorySources = {
  list: async (limit = 50, offset = 0) =>
    listFromBackend<InventorySource>(await api<unknown>(`/inventorysource?limit=${limit}&offset=${offset}`), INVENTORY_SOURCE_MAP, limit, offset),
  get: async (id: string) =>
    fromBackend<InventorySource>(await api<unknown>(`/inventorysource/${id}`), INVENTORY_SOURCE_MAP),
  create: async (data: Partial<InventorySource>) =>
    fromBackend<InventorySource>(
      await api<unknown>('/inventorysource', {
        method: 'POST',
        body: JSON.stringify(toBackend(data, INVENTORY_SOURCE_MAP)),
      }),
      INVENTORY_SOURCE_MAP,
    ),
  update: async (id: string, data: Partial<InventorySource>) =>
    fromBackend<InventorySource>(
      await api<unknown>(`/inventorysource/${id}`, {
        method: 'PUT',
        body: JSON.stringify(toBackend(data, INVENTORY_SOURCE_MAP)),
      }),
      INVENTORY_SOURCE_MAP,
    ),
  delete: (id: string) => api<void>(`/inventorysource/${id}`, { method: 'DELETE' }),
};

// Snippets — /api/snippet
export interface SnippetTestRequest {
  input?: Record<string, unknown>;
  target_devices?: string[];
  target_pools?: string[];
  timeout_seconds?: number;
}

export interface SnippetTestStep {
  step_run_id: string;
  device_id?: string | null;
  status: string;
  input_payload?: Record<string, unknown>;
  output?: Record<string, unknown>;
  logs?: string;
  error?: string;
}

export interface SnippetTestResponse {
  status: 'success' | 'failure' | 'timeout' | string;
  duration_ms: number;
  run_id: string;
  workflow_id: string;
  steps: SnippetTestStep[];
  error?: string;
}

export const snippets = {
  list: async (limit = 50, offset = 0) =>
    listFromBackend<Snippet>(await api<unknown>(`/snippet?limit=${limit}&offset=${offset}`), SNIPPET_MAP, limit, offset),
  get: async (id: string) =>
    fromBackend<Snippet>(await api<unknown>(`/snippet/${id}`), SNIPPET_MAP),
  create: async (data: Partial<Snippet>) =>
    fromBackend<Snippet>(
      await api<unknown>('/snippet', { method: 'POST', body: JSON.stringify(toBackend(data, SNIPPET_MAP)) }),
      SNIPPET_MAP,
    ),
  update: async (id: string, data: Partial<Snippet>) =>
    fromBackend<Snippet>(
      await api<unknown>(`/snippet/${id}`, { method: 'PUT', body: JSON.stringify(toBackend(data, SNIPPET_MAP)) }),
      SNIPPET_MAP,
    ),
  // Copies a snippet into a new row.
  //
  // This is the only way to start from a body that already WORKS. The type picker
  // on /snippets/new lists handler types — `python_snippet`, `ssh`, `ping` — and
  // loads one generic starter for whichever is chosen; a seeded baseline like the
  // paramiko SSH primitive is a python_snippet with a particular body, not a type,
  // so it can never appear in that list. Without this it is reachable only by
  // retyping it, which is how the details that matter get lost.
  //
  // `overrides` is applied last, so a caller can rename, re-describe, or ask for an
  // inert copy (`network_enabled: false`) when turning it on is admin-gated.
  duplicate: async (id: string, overrides: Partial<Snippet> & { name: string }) => {
    const source = await snippets.get(id);
    return snippets.create({
      // `name` is not defaulted here: `overrides` carries it and is spread last,
      // so setting it twice would just be overwritten.
      type: source.type,
      description: source.description,
      code: source.code,
      script_language: source.script_language,
      input_schema: source.input_schema,
      output_schema: source.output_schema,
      target_mode: source.target_mode,
      max_parallel: source.max_parallel,
      timeout_seconds: source.timeout_seconds,
      retry_policy: source.retry_policy,
      logic_diagram_mermaid: source.logic_diagram_mermaid,
      idempotency: source.idempotency,
      // Carried deliberately: for a python_snippet, null means "the author has not
      // said" and every step using the copy FAILS until a node declares it.
      changes_state: source.changes_state,
      network_enabled: source.network_enabled,
      ...overrides,
    });
  },
  delete: (id: string) => api<void>(`/snippet/${id}`, { method: 'DELETE' }),
  // TODO(backend): /snippet/{id}/test does not exist yet — depends on the engine (Phase 3).
  test: (id: string, req: SnippetTestRequest) =>
    api<SnippetTestResponse>(`/snippet/${id}/test`, { method: 'POST', body: JSON.stringify(req) }),
};

// Workflows — /api/workflow
export const workflows = {
  list: async (limit = 50, offset = 0, environment?: string, search?: string) => {
    // limit + offset are honored by the back (Sprint 1.5). environment and
    // search are still TODO(backend) — the back ignores them for now.
    const qs = new URLSearchParams();
    qs.set('limit', String(limit));
    qs.set('offset', String(offset));
    if (environment) qs.set('environment', environment);
    if (search) qs.set('search', search);
    return listFromBackend<Workflow>(await api<unknown>(`/workflow?${qs.toString()}`), WORKFLOW_MAP, limit, offset);
  },
  get: async (id: string) => fromBackend<Workflow>(await api<unknown>(`/workflow/${id}`), WORKFLOW_MAP),
  create: async (data: Partial<Workflow>) =>
    fromBackend<Workflow>(
      await api<unknown>('/workflow', { method: 'POST', body: JSON.stringify(toBackend(data, WORKFLOW_MAP)) }),
      WORKFLOW_MAP,
    ),
  update: async (id: string, data: Partial<Workflow>) =>
    fromBackend<Workflow>(
      await api<unknown>(`/workflow/${id}`, { method: 'PUT', body: JSON.stringify(toBackend(data, WORKFLOW_MAP)) }),
      WORKFLOW_MAP,
    ),
  delete: (id: string) => api<void>(`/workflow/${id}`, { method: 'DELETE' }),

  // S14.1: workflows tagged metadata.is_subflow=true. Used by /subflows
  // and the editor's subflow-node picker.
  subflows: async (environment?: string) => {
    const qs = environment ? `?environment=${encodeURIComponent(environment)}` : '';
    const raw = await api<unknown[]>(`/workflow/subflows${qs}`);
    return raw.map((w) => fromBackend<Workflow>(w, WORKFLOW_MAP));
  },

  // S13.6: rollback risk surface — read-only view of nodes that block
  // a clean rollback. Returned shape matches WorkflowController.RollbackRisk.
  rollbackRisk: (id: string) => api<{
    non_reversible: Array<{ snippet_id: string; snippet_name: string; snippet_type: string; kind: string }>;
    requires_compensation: Array<{ snippet_id: string; snippet_name: string; snippet_type: string; kind: string }>;
    compensated: Array<{ snippet_id: string; snippet_name: string; snippet_type: string; kind: string }>;
  }>(`/workflow/${id}/rollback-risk`),

  // Nested create: /api/workflow/{workflowId}/triggers
  createTrigger: async (id: string, data: Partial<WorkflowTrigger>) =>
    fromBackend<WorkflowTrigger>(
      await api<unknown>(`/workflow/${id}/triggers`, {
        method: 'POST',
        body: JSON.stringify(toBackend(data, WORKFLOW_TRIGGER_MAP)),
      }),
      WORKFLOW_TRIGGER_MAP,
    ),

  // TODO(backend): execution / versioning / promotion / clone / diff
  // endpoints depend on the workflow engine (Phase 3) and do not exist yet.
  // They stay here so the UI keeps its typings; each call will 404 until
  // the backend port lands.
  run: (id: string, data: { target_devices?: string[]; target_pools?: string[]; input?: Record<string, unknown> }) =>
    api<WorkflowRun>(`/workflow/${id}/run`, { method: 'POST', body: JSON.stringify(data) }),
  promote: (
    id: string,
    data: {
      target_environment: 'qa' | 'production';
      change_summary: string;
      promoted_by?: string;
      approved_by?: string;
    },
  ) =>
    api<{ production_workflow: Workflow; version: number; diff: DiffResult }>(`/workflow/${id}/promote`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),
  rollback: (id: string, version: number) =>
    api<{ workflow: Workflow; rolled_back_to: number; new_version: number }>(
      `/workflow/${id}/rollback/${version}`,
      { method: 'POST' },
    ),
  versions: (id: string) => api<WorkflowVersion[]>(`/workflow/${id}/versions`),
  diff: (id: string) => api<DiffResult>(`/workflow/${id}/diff`),
  clone: (id: string) => api<Workflow>(`/workflow/${id}/clone`, { method: 'POST' }),
  triggers: async (id: string) =>
    (listFromBackend<WorkflowTrigger>(
      await api<unknown>(`/workflow/${id}/triggers`),
      WORKFLOW_TRIGGER_MAP,
      500,
      0,
    )).data,
};

// Workflow triggers — /api/workflowtrigger
export const workflowTriggers = {
  list: async (limit = 50, offset = 0) =>
    listFromBackend<WorkflowTrigger>(await api<unknown>(`/workflowtrigger?limit=${limit}&offset=${offset}`), WORKFLOW_TRIGGER_MAP, limit, offset),
  get: async (id: string) =>
    fromBackend<WorkflowTrigger>(await api<unknown>(`/workflowtrigger/${id}`), WORKFLOW_TRIGGER_MAP),
  update: async (id: string, data: Partial<WorkflowTrigger>) =>
    fromBackend<WorkflowTrigger>(
      await api<unknown>(`/workflowtrigger/${id}`, {
        method: 'PUT',
        body: JSON.stringify(toBackend(data, WORKFLOW_TRIGGER_MAP)),
      }),
      WORKFLOW_TRIGGER_MAP,
    ),
  delete: (id: string) => api<void>(`/workflowtrigger/${id}`, { method: 'DELETE' }),
  // Rotate a webhook trigger's signing secret. Response carries the new
  // plaintext once in `webhook_secret`.
  rotateSecret: async (id: string) =>
    fromBackend<WorkflowTrigger>(
      await api<unknown>(`/workflowtrigger/${id}/rotate-secret`, { method: 'POST' }),
      WORKFLOW_TRIGGER_MAP,
    ),
};

// Workflow plans (governance) — /api/workflowplan
/** @deprecated No frontend consumer as of the May-2026 cleanup. The
 *  governance flow now goes through the agent's create_workflow_plan
 *  tool. Remove this block when the unused-import sweep next runs. */
export const workflowPlans = {
  list: async (limit = 50, offset = 0) =>
    listFromBackend<WorkflowPlan>(await api<unknown>(`/workflowplan?limit=${limit}&offset=${offset}`), WORKFLOW_PLAN_MAP, limit, offset),
  get: async (id: string) =>
    fromBackend<WorkflowPlan>(await api<unknown>(`/workflowplan/${id}`), WORKFLOW_PLAN_MAP),
  create: async (data: Partial<WorkflowPlan>) =>
    fromBackend<WorkflowPlan>(
      await api<unknown>('/workflowplan', { method: 'POST', body: JSON.stringify(toBackend(data, WORKFLOW_PLAN_MAP)) }),
      WORKFLOW_PLAN_MAP,
    ),
  update: async (id: string, data: Partial<WorkflowPlan>) =>
    fromBackend<WorkflowPlan>(
      await api<unknown>(`/workflowplan/${id}`, { method: 'PUT', body: JSON.stringify(toBackend(data, WORKFLOW_PLAN_MAP)) }),
      WORKFLOW_PLAN_MAP,
    ),
  delete: (id: string) => api<void>(`/workflowplan/${id}`, { method: 'DELETE' }),

  // Governance transitions
  submit: async (id: string) =>
    fromBackend<WorkflowPlan>(await api<unknown>(`/workflowplan/${id}/submit`, { method: 'POST' }), WORKFLOW_PLAN_MAP),
  approve: async (id: string, approvedBy: string) =>
    fromBackend<WorkflowPlan>(
      await api<unknown>(`/workflowplan/${id}/approve`, {
        method: 'POST',
        body: JSON.stringify({ approved_by: approvedBy }),
      }),
      WORKFLOW_PLAN_MAP,
    ),
  reject: async (id: string, approvedBy: string, reason: string) =>
    fromBackend<WorkflowPlan>(
      await api<unknown>(`/workflowplan/${id}/reject`, {
        method: 'POST',
        body: JSON.stringify({ approved_by: approvedBy, reason }),
      }),
      WORKFLOW_PLAN_MAP,
    ),
  build: async (id: string) =>
    fromBackend<WorkflowPlan>(await api<unknown>(`/workflowplan/${id}/build`, { method: 'POST' }), WORKFLOW_PLAN_MAP),
};

// Runs — /api/run (workflow run history + nested step-run listing).
// Writes are engine-internal (Sprint 2.4+); the HTTP surface is read-only.
// WebSocket /runs/{id}/stream still pending — arrives with worker in 2.2.
export const runs = {
  list: (limit = 50, offset = 0) =>
    api<ListResponse<WorkflowRun>>(`/run?limit=${limit}&offset=${offset}`),
  get: (id: string) => api<WorkflowRun>(`/run/${id}`),
  steps: (id: string, limit = 50, offset = 0) =>
    api<ListResponse<StepRun>>(`/run/${id}/steps?limit=${limit}&offset=${offset}`),
  // Operator-initiated stop. Returns 409 if the run already reached a
  // terminal status (the UI should refetch and hide the button in that case).
  cancel: (id: string) => api<WorkflowRun>(`/run/${id}/cancel`, { method: 'POST' }),
  // Admin-only soft-deletes. The engine is still the writer during execution;
  // these only prune the history row (and cascade to step_runs) for the UI.
  delete: (id: string) => api<void>(`/run/${id}`, { method: 'DELETE' }),
  deleteAll: () =>
    api<{ deleted: number; steps_deleted: number }>(`/run`, { method: 'DELETE' }),
};

// Step runs — /api/steprun. Deep-link lookup for a single step_run row.
// Listing is always done via runs.steps(runId) so the parent-run scope is
// carried explicitly.
/** @deprecated No frontend consumer. The runs detail page reads steps
 *  via runs.steps(runId), and individual step lookups are done through
 *  there. Kept for one cycle in case a tool needs it. */
export const stepRuns = {
  get: (id: string) => api<StepRun>(`/steprun/${id}`),
};

// Workflow versions — /api/workflowversion. Listing requires workflow_id
// because unrestricted listing mixes snapshots across workflows.
/** @deprecated No frontend consumer. Workflow detail page reads version
 *  history through workflows.diff(...). Remove next cycle if no
 *  consumer appears. */
export const workflowVersions = {
  listByWorkflow: (workflowId: string, limit = 50, offset = 0) =>
    api<ListResponse<WorkflowVersion>>(
      `/workflowversion?workflow_id=${encodeURIComponent(workflowId)}&limit=${limit}&offset=${offset}`,
    ),
  get: (id: string) => api<WorkflowVersion>(`/workflowversion/${id}`),
};

export interface Job {
  id: string;
  type: string;
  payload: Record<string, unknown>;
  tag: string;
  priority: number;
  status: string;
  claimed_by: string | null;
  created_at: string;
  claimed_at: string | null;
  completed_at: string | null;
}

// Jobs — /api/job. Admin-only; exposes the internal queue (payload, tag,
// priority, worker assignment) plus aggregated counts for the dashboard.
// Queue stats always return the four canonical keys (pending/claimed/
// completed/failed) so the UI can render without null-guarding buckets.
export const jobs = {
  list: (limit = 50, offset = 0) =>
    api<ListResponse<Job>>(`/job?limit=${limit}&offset=${offset}`),
  get: (id: string) => api<Job>(`/job/${id}`),
  queueStats: () => api<Record<string, number>>('/job/queue/stats'),
};

// Users — /api/users. Admin-only; the backend scopes the query to the
// caller, so an admin can only ever see/manage their own users. The list
// endpoint returns a bare array (no pagination envelope) —
// admin UIs list at most a few hundred users, not millions.
export type UserRole = 'admin' | 'operator' | 'viewer';

export interface User {
  user_id: string;
  username: string;
  email: string;
  role: UserRole;
  is_active: boolean;
  locked: boolean;
  password_changed_at: string;
  created_at: string;
  updated_at: string;
}

export interface CreateUserBody {
  username: string;
  email: string;
  password: string;
  role?: UserRole;
}

export interface UpdateUserBody {
  email?: string;
  role?: UserRole;
  is_active?: boolean;
}

export const users = {
  list: () => api<User[]>('/users'),
  get: (id: string) => api<User>(`/users/${id}`),
  create: (body: CreateUserBody) =>
    api<User>('/users', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: string, body: UpdateUserBody) =>
    api<User>(`/users/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) => api<User>(`/users/${id}`, { method: 'DELETE' }),
};

// Audit + auth event log and admin metrics. Admin-only endpoints.
//
// Domain audit events cover mutations to scoped entities — the
// `IAuditLogger` writes before/after snapshots so the UI can diff them.
// Auth events cover sign-in activity, lockouts, and token lifecycle.
export interface AuditEvent {
  audit_event_id: string;
  user_id: string | null;
  // Username for a signed-in request; the automation identity
  // ("git-webhook", "workflow-runner", "messaging-send", "workflow-webhook")
  // when user_id is null, which it always is for unattended changes.
  actor: string | null;
  entity_type: string;
  entity_id: string | null;
  action: string;
  before_json: unknown;
  after_json: unknown;
  ip: string | null;
  user_agent: string | null;
  at: string;
  request_id: string | null;
}

export interface AuthEvent {
  auth_event_id: string;
  user_id: string | null;
  event: string;
  ip: string | null;
  user_agent: string | null;
  at: string;
  metadata: unknown;
}

export interface AuditQuery {
  entity_type?: string;
  // "Everything that happened to this one workflow / credential / user."
  entity_id?: string;
  user_id?: string;
  // Automation has no user_id, so this is the only way to filter it.
  actor?: string;
  action?: string;
  // Server-side LIKE prefix on Action — useful for category filters such as
  // `allow_private_network` which spans `.enabled`, `.disabled`, `.reminder`.
  action_prefix?: string;
  from?: string;
  to?: string;
  limit?: number;
  offset?: number;
}

export interface AuthEventQuery {
  user_id?: string;
  from?: string;
  to?: string;
  limit?: number;
}

export interface DailyBucket {
  date: string;
  counts: Record<string, number>;
}

export interface TopFailingWorkflow {
  workflow_id: string;
  workflow_name: string;
  failed_count: number;
}

export interface RunMetrics {
  days: number;
  from: string;
  series: DailyBucket[];
  top_failing: TopFailingWorkflow[];
}

export interface AuthMetrics {
  days: number;
  from: string;
  series: DailyBucket[];
}

function qs(params: Record<string, unknown>): string {
  const pairs = Object.entries(params).filter(([, v]) => v !== undefined && v !== '' && v !== null);
  return pairs.length === 0 ? '' : '?' + pairs.map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&');
}

export const audit = {
  events: (q: AuditQuery = {}) => api<AuditEvent[]>(`/audit/events${qs(q as Record<string, unknown>)}`),
};

// ─── Resource permissions (S13.5) ──────────────────────────────────────
// Per-resource RBAC overlay: grant a user one of owner/editor/runner/viewer
// on a single workflow or integration. Global admin is always a floor.

export type ResourceRole = 'owner' | 'editor' | 'runner' | 'viewer';
export type ResourceType = 'workflow' | 'integration';

export interface ResourcePermission {
  resource_permission_id: string;
  resource_type: ResourceType;
  resource_id: string;
  subject_type: 'user' | 'group';
  subject_id: string;
  subject_username: string | null;
  role: ResourceRole;
  granted_by: string | null;
  granted_by_username: string | null;
  granted_at: string;
}

export interface GrantResourcePermissionRequest {
  subject_type?: 'user';
  subject_id: string;
  role: ResourceRole;
}

export const permissions = {
  list: (type: ResourceType, id: string) =>
    api<ResourcePermission[]>(`/permissions/${type}/${id}`),
  grant: (type: ResourceType, id: string, body: GrantResourcePermissionRequest) =>
    api<ResourcePermission>(`/permissions/${type}/${id}`, { method: 'POST', body: JSON.stringify(body) }),
  revoke: (type: ResourceType, id: string, permissionId: string) =>
    api<void>(`/permissions/${type}/${id}/${permissionId}`, { method: 'DELETE' }),
};

export const authEvents = {
  list: (q: AuthEventQuery = {}) => api<AuthEvent[]>(`/auth/events${qs(q as Record<string, unknown>)}`),
};

export interface SloEntry {
  key: string;
  label: string;
  unit: string;
  target: number;
  value: number | null;
  better: 'lower' | 'higher';
}

export interface SloResponse {
  days: number;
  from: string;
  slos: SloEntry[];
}

export const adminMetrics = {
  runs: (days = 7) => api<RunMetrics>(`/admin/metrics/runs?days=${days}`),
  auth: (days = 7) => api<AuthMetrics>(`/admin/metrics/auth?days=${days}`),
  slo: (days = 7) => api<SloResponse>(`/admin/metrics/slo?days=${days}`),
};

// ─── Reports (Sprint 11) ──────────────────────────────────────────────
// The list view shows metadata only; the detail view decrypts the
// agent prompt and leaves a `report.read_prompt` audit row behind, so
// we keep those as distinct calls rather than one mega-query.

export interface ReportArtifactSummary {
  report_artifact_id: string;
  title: string;
  filename: string;
  format: 'html' | 'csv' | 'xlsx' | 'pdf';
  content_type: string;
  size_bytes: number;
  source: 'agent' | 'api' | 'workflow' | string;
  user_id: string | null;
  username: string | null;
  agent_conversation_id: string | null;
  workflow_run_id: string | null;
  /** How the producing workflow run was triggered (manual | schedule |
   *  webhook | subflow). Null for non-workflow artifacts or purged runs. */
  run_trigger: string | null;
  agent_prompt_redacted: boolean;
  has_agent_prompt: boolean;
  expires_at: string;
  created_at: string;
}

export interface ReportArtifactDetail extends ReportArtifactSummary {
  sha256: string;
  base64: string;
  agent_run_id: string | null;
  agent_name: string | null;
  agent_prompt: string | null;
  workflow_id: string | null;
  request_id: string | null;
}

export interface ReportListResponse {
  data: ReportArtifactSummary[];
  total: number;
  limit: number;
  offset: number;
}

export interface AdminReportsQuery {
  user_id?: string;
  format?: string;
  source?: string;
  conversation_id?: string;
  workflow_run_id?: string;
  search?: string;
  from?: string;
  to?: string;
  limit?: number;
  offset?: number;
}

export const adminReports = {
  list: (q: AdminReportsQuery = {}) =>
    api<ReportListResponse>(`/admin/reports${qs(q as Record<string, unknown>)}`),
  get: (id: string) => api<ReportArtifactDetail>(`/admin/reports/${id}`),
  /** @deprecated Use `downloadReport(...)` — a direct href from the browser
   *  skips the JWT and hits 401. Kept only so we don't break existing calls
   *  during a transition; should go away once callers migrate. */
  downloadUrl: (id: string) => `/api/admin/reports/${id}/download`,
  delete: (id: string) => api<void>(`/admin/reports/${id}`, { method: 'DELETE' }),
};

/**
 * Pulls a report file with the current JWT attached and triggers a browser
 * download. Works for admin-scoped URLs (/api/admin/reports/{id}/download)
 * and owner-scoped URLs (/api/reports/{id}/download) alike — the caller
 * picks the path, we just do the authenticated fetch + blob + anchor trick.
 *
 * Browser-direct navigation would hit 401: our auth lives in an
 * Authorization header + access token in memory, not a cookie, so no
 * standard `<a href>` can authenticate. This helper closes that gap.
 */
export async function downloadReport(path: string, filenameFallback?: string): Promise<void> {
  // `api()` wraps JSON endpoints and isn't reusable for a raw file stream (it
  // sets Content-Type: application/json and JSON-parses the response body), so
  // this goes through `authedFetch` — which attaches the bearer AND refreshes
  // on 401, the part that attaching it by hand used to miss.
  const normalized = path.startsWith('/api') ? path : `${BASE}${path}`;
  const res = await authedFetch(normalized);
  if (!res.ok) {
    throw new Error(`download failed: HTTP ${res.status}`);
  }

  // Prefer the filename the server advertised (Content-Disposition) over
  // whatever the caller guessed — the server knows the canonical one.
  let filename = filenameFallback ?? 'report';
  const disp = res.headers.get('content-disposition');
  if (disp) {
    const m = /filename\*?=(?:UTF-8''|")?([^";]+)/i.exec(disp);
    if (m) filename = decodeURIComponent(m[1].replace(/"/g, ''));
  }

  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  try {
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
  } finally {
    // Revoke on next tick so the browser has the URL long enough to
    // actually start the download in Firefox/Safari.
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}

// Shape of a report document — kept small because the builder logic
// lives in the backend / agent, not here. Only used when the UI
// constructs a manual POST /api/reports call.
export const reports = {
  generate: (body: unknown) =>
    fetch('/api/reports', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify(body),
    }),
};

// Global feature flags exposed at /admin/settings. Reads are cached
// server-side for 60s.
export interface AdminSettingsBody {
  permissions_granular_gating_enabled: boolean;
  // Which RBAC engine authorizes requests. 'legacy' = the flat
  // Admin/Operator/Viewer tiers (plus the per-resource overlay above);
  // 'granular' = the capability-based permission grants managed under
  // /permissions. Switching to 'granular' makes those grants the source
  // of truth for capability checks.
  rbac_mode: 'legacy' | 'granular';
  // Import wizard fuzzy-match calibration. Threshold is the minimum
  // score a candidate must hit to auto-resolve; Gap is the lead it
  // must have over the runner-up. Backend clamps server-side to safe
  // ranges (threshold 0.5–0.99, gap 0.0–0.5).
  import_fuzzy_match_threshold: number;
  import_fuzzy_match_gap: number;
}

export const adminSettings = {
  get: () => api<AdminSettingsBody>('/admin/settings'),
  update: (body: AdminSettingsBody) =>
    api<AdminSettingsBody>('/admin/settings', { method: 'PUT', body: JSON.stringify(body) }),
};

// Sprint 8: conversations + secrets for the new /ai UI.
export interface AiConversationSummary {
  conversation_id: string;
  agent_id: string | null;
  user_id: string | null;
  status: string;
  title: string;
  message_count: number;
  created_at: string;
  updated_at: string;
}

export interface AiConversationDetail extends AiConversationSummary {
  messages: unknown;
  // "web" for the chat UI; "telegram" / "slack" / "whatsapp" / "teams" for
  // external channels. Never null — the API coalesces legacy rows to "web".
  source: string;
  token_usage: unknown;
}

export const aiConversations = {
  // Returns the `{data, total, limit, offset}` envelope. `total` is the
  // caller's whole scoped history, not the page — read it for any count you
  // display, or the number shown is just the page size you asked for.
  list: async (limit = 50, offset = 0) => {
    const res = await api<{
      data: AiConversationSummary[]; total?: number; limit?: number; offset?: number;
    }>(`/ai/conversations?limit=${limit}&offset=${offset}`);
    return { data: res.data, total: res.total ?? res.data.length, limit, offset };
  },
  get: (id: string) => api<AiConversationDetail>(`/ai/conversations/${id}`),
  delete: (id: string) =>
    api<void>(`/ai/conversations/${id}`, { method: 'DELETE' }),
  deleteAll: () =>
    api<{ deleted: number }>(`/ai/conversations`, { method: 'DELETE' }),
};

// Device credentials — auth material attached to devices so the SSH /
// NETCONF / SNMP handlers can log in. Distinct from /admin/secrets
// which feeds the AI agent's OpenAPI executor. Plaintext values are
// never returned; the response only exposes metadata plus
// `has_private_key` so the UI can show an indicator.
export interface Credential {
  credential_id: string;
  name: string;
  type: string;
  username: string | null;
  auth_method: 'password' | 'key';
  has_private_key: boolean;
  extra: Record<string, unknown>;
  created_at: string;
  updated_at: string;
}

export interface CreateCredentialBody {
  name: string;
  type: string;
  username?: string;
  auth_method?: 'password' | 'key';
  password?: string;
  private_key?: string;
  key_passphrase?: string;
  extra?: Record<string, unknown>;
}

export interface UpdateCredentialBody {
  name?: string;
  type?: string;
  username?: string;
  auth_method?: 'password' | 'key';
  password?: string;
  private_key?: string;
  key_passphrase?: string;
  extra?: Record<string, unknown>;
}

export const credentials = {
  list: async (limit = 100, offset = 0) => {
    const res = await api<{ data: Credential[]; total?: number; limit?: number; offset?: number } | Credential[]>(
      `/credential?limit=${limit}&offset=${offset}`,
    );
    // Backend returns either a ListResponse wrapper or a bare array
    // depending on the controller flavor; normalize to the wrapper.
    if (Array.isArray(res)) return { data: res, total: res.length, limit, offset };
    return { data: res.data, total: res.total ?? res.data.length, limit, offset };
  },
  get: (id: string) => api<Credential>(`/credential/${id}`),
  create: (body: CreateCredentialBody) =>
    api<Credential>('/credential', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: string, body: UpdateCredentialBody) =>
    api<Credential>(`/credential/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) =>
    api<void>(`/credential/${id}`, { method: 'DELETE' }),
};

export interface Secret {
  secret_id: string;
  name: string;
  description: string | null;
  has_value: boolean;
  created_by: string | null;
  created_at: string;
  updated_at: string;
}

export interface CreateSecretBody {
  name: string;
  description?: string;
  value: string;
}

export interface UpdateSecretBody {
  description?: string;
  value?: string;
}

export const secrets = {
  list: () => api<Secret[]>('/secrets'),
  get: (id: string) => api<Secret>(`/secrets/${id}`),
  create: (body: CreateSecretBody) =>
    api<Secret>('/secrets', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: string, body: UpdateSecretBody) =>
    api<Secret>(`/secrets/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) =>
    api<void>(`/secrets/${id}`, { method: 'DELETE' }),
};

// Sprint 9 trace events — application-level action trail. Written by
// ITraceLogger on every critical path, read from /admin/traces.
export interface TraceEvent {
  trace_event_id: string;
  user_id: string | null;
  request_id: string | null;
  action: string;
  category: string;
  status: string;
  duration_ms: number | null;
  error_message: string | null;
  metadata: unknown;
  at: string;
}

export interface TraceQuery {
  category?: string;
  /**
   * One free-text term over every text a trace carries — action, category, error
   * message, request id and the metadata blob. Substring, not equality: people arrive
   * holding a fragment they saw in a log, not a whole dotted action name.
   */
  search?: string;
  status?: string;
  /** Who fired it: user id, or part of a username or email. */
  user?: string;
  /** Exact. The deep link that reconstructs one request end to end. */
  request_id?: string;
  /** Only rows that finished and took at least this long. Sorts slowest-first. */
  min_duration_ms?: number;
  from?: string;
  to?: string;
  limit?: number;
  offset?: number;
}

export interface TraceSummary {
  window_hours: number;
  from: string;
  by_category: Array<{ category: string; count: number }>;
  by_status: Array<{ status: string; count: number }>;
}

export const traces = {
  list: (q: TraceQuery = {}) =>
    api<TraceEvent[]>(`/admin/traces${qs(q as Record<string, unknown>)}`),
  get: (id: string) => api<TraceEvent>(`/admin/traces/${id}`),
  summary: (hours = 24) => api<TraceSummary>(`/admin/traces/summary?hours=${hours}`),
};

// Skills — /api/skill
export const skills = {
  list: async (limit = 50, offset = 0) =>
    listFromBackend<Skill>(await api<unknown>(`/skill?limit=${limit}&offset=${offset}`), SKILL_MAP, limit, offset),
  get: async (id: string) => fromBackend<Skill>(await api<unknown>(`/skill/${id}`), SKILL_MAP),
  create: async (data: Partial<Skill>) =>
    fromBackend<Skill>(
      await api<unknown>('/skill', { method: 'POST', body: JSON.stringify(toBackend(data, SKILL_MAP)) }),
      SKILL_MAP,
    ),
  update: async (id: string, data: Partial<Skill>) =>
    fromBackend<Skill>(
      await api<unknown>(`/skill/${id}`, { method: 'PUT', body: JSON.stringify(toBackend(data, SKILL_MAP)) }),
      SKILL_MAP,
    ),
  delete: (id: string) => api<void>(`/skill/${id}`, { method: 'DELETE' }),
};

// AI Providers — /api/aiprovider
export const aiProviders = {
  list: async (limit = 50, offset = 0) =>
    listFromBackend<AIProvider>(await api<unknown>(`/aiprovider?limit=${limit}&offset=${offset}`), AI_PROVIDER_MAP, limit, offset),
  get: async (id: string) => fromBackend<AIProvider>(await api<unknown>(`/aiprovider/${id}`), AI_PROVIDER_MAP),
  create: async (data: Partial<AIProvider> & { api_key?: string }) =>
    fromBackend<AIProvider>(
      await api<unknown>('/aiprovider', { method: 'POST', body: JSON.stringify(toBackend(data, AI_PROVIDER_MAP)) }),
      AI_PROVIDER_MAP,
    ),
  update: async (id: string, data: Partial<AIProvider> & { api_key?: string }) =>
    fromBackend<AIProvider>(
      await api<unknown>(`/aiprovider/${id}`, { method: 'PUT', body: JSON.stringify(toBackend(data, AI_PROVIDER_MAP)) }),
      AI_PROVIDER_MAP,
    ),
  delete: (id: string) => api<void>(`/aiprovider/${id}`, { method: 'DELETE' }),
  // TODO(backend): /aiprovider/{id}/test — part of the AI subsystem (Phase 7).
  test: (id: string) =>
    api<{ success: boolean; response: string; model: string }>(`/aiprovider/${id}/test`, { method: 'POST' }),
};

// AI Agents — /api/aiagent
export const aiAgents = {
  list: async (limit = 50, offset = 0) =>
    listFromBackend<AIAgent>(await api<unknown>(`/aiagent?limit=${limit}&offset=${offset}`), AI_AGENT_MAP, limit, offset),
  get: async (id: string) => fromBackend<AIAgent>(await api<unknown>(`/aiagent/${id}`), AI_AGENT_MAP),
  create: async (data: Partial<AIAgent>) =>
    fromBackend<AIAgent>(
      await api<unknown>('/aiagent', { method: 'POST', body: JSON.stringify(toBackend(data, AI_AGENT_MAP)) }),
      AI_AGENT_MAP,
    ),
  update: async (id: string, data: Partial<AIAgent>) =>
    fromBackend<AIAgent>(
      await api<unknown>(`/aiagent/${id}`, { method: 'PUT', body: JSON.stringify(toBackend(data, AI_AGENT_MAP)) }),
      AI_AGENT_MAP,
    ),
  delete: (id: string) => api<void>(`/aiagent/${id}`, { method: 'DELETE' }),
  // Runtime tool catalog (names as registered server-side) for the allowlist picker.
  tools: () => api<AiToolInfo[]>('/aiagent/tools'),
  // TODO(backend): chat endpoint — AI subsystem (Phase 7).
  chat: (id: string, message: string) =>
    api<ChatResponse>(`/aiagent/${id}/chat`, { method: 'POST', body: JSON.stringify({ message }) }),
};

// AI Chat (legacy single-turn) — TODO(backend): Phase 7.
/** @deprecated Superseded by aiConversations + the streaming endpoint
 *  consumed via $lib/api/ai-stream. No frontend consumer. */
export const aiChat = {
  send: (message: string, agentId?: string) => {
    const body: Record<string, unknown> = { message };
    if (agentId) body.agent_id = agentId;
    return api<ChatResponse>('/ai/chat', { method: 'POST', body: JSON.stringify(body) });
  },
};

// AI Catalog (skills/specs file-backed) — /api/ai/catalog  [NUEVO]
export const aiCatalog = {
  /** Render the agent system prompt (concatenation of /Skills/*.md). */
  prompt: () => api<{ prompt: string }>('/ai/catalog/prompt'),
  /** List or search operations indexed from /Specs/*.yaml. */
  operations: (filter: { keyword?: string; api?: string; method?: string } = {}) => {
    const qs = new URLSearchParams();
    if (filter.keyword) qs.set('keyword', filter.keyword);
    if (filter.api) qs.set('api', filter.api);
    if (filter.method) qs.set('method', filter.method);
    const suffix = qs.toString() ? `?${qs.toString()}` : '';
    return api<ApiOperation[]>(`/ai/catalog/operations${suffix}`);
  },
  /** Drill into one operation by its operationId. */
  operation: (operationId: string) =>
    api<ApiOperation>(`/ai/catalog/operations/${encodeURIComponent(operationId)}`),
  /** Force a re-read of the prompt-skills / api-specs catalogs. */
  reload: () => api<{ reloaded: boolean; operation_count: number }>('/ai/catalog/reload', { method: 'POST' }),
};

// ─────────────────────────────────────────────────────────────────────
//  Admin: prompt skills + api specs CRUD  (admin-only)
//
//  Replaces the old /Skills/*.md and /Specs/*.yaml file storage with
//  DB rows editable from the UI. The list endpoints intentionally do
//  NOT include `content` so large catalogs stay cheap to render; the
//  frontend fetches the body on demand when the user opens an editor.
// ─────────────────────────────────────────────────────────────────────

export interface PromptSkillSummary {
  ai_prompt_skill_id: string;
  name: string;
  content: string;
  sort_order: number;
  size_bytes: number;
  is_active: boolean;
  created_by: string | null;
  // Null when the skill is a global catalog entry; set when scoped to an
  // Integration (the agent pairs its content with that integration's base
  // URL and credentials).
  integration_id: string | null;
  created_at: string;
  updated_at: string;
}

export interface CreatePromptSkillBody {
  name: string;
  content: string;
  sort_order?: number;
  // Link to an Integration; null / omitted = global catalog entry.
  integration_id?: string | null;
}

export interface UpdatePromptSkillBody {
  name?: string;
  content?: string;
  sort_order?: number;
  is_active?: boolean;
  // Authoritative on update — send the current value; null unlinks.
  integration_id?: string | null;
}

export interface ReseedResult {
  imported: number;
  updated: number;
  detail?: string | null;
}

export const promptSkills = {
  list: (limit = 50, offset = 0) =>
    api<ListResponse<PromptSkillSummary>>(`/admin/prompt-skills?limit=${limit}&offset=${offset}`),
  get: (id: string) => api<PromptSkillSummary>(`/admin/prompt-skills/${id}`),
  create: (body: CreatePromptSkillBody) =>
    api<PromptSkillSummary>('/admin/prompt-skills', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: string, body: UpdatePromptSkillBody) =>
    api<PromptSkillSummary>(`/admin/prompt-skills/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) =>
    api<PromptSkillSummary>(`/admin/prompt-skills/${id}`, { method: 'DELETE' }),
  reseed: () => api<ReseedResult>('/admin/prompt-skills/reseed', { method: 'POST' }),
};

export interface ApiSpecSummary {
  ai_api_spec_id: string;
  api: string;
  content: string;
  operation_count: number;
  size_bytes: number;
  is_active: boolean;
  created_by: string | null;
  // Null when the spec is a global catalog entry; set when scoped to an
  // Integration so the agent knows which base URL + credentials to use
  // for the operations it describes.
  integration_id: string | null;
  created_at: string;
  updated_at: string;
}

export interface CreateApiSpecBody {
  api: string;
  content: string;
  // Link to an Integration; null / omitted = global catalog entry.
  integration_id?: string | null;
}

export interface UpdateApiSpecBody {
  api?: string;
  content?: string;
  is_active?: boolean;
  // Authoritative on update — send the current value; null unlinks.
  integration_id?: string | null;
}

export const apiSpecs = {
  list: (limit = 50, offset = 0) =>
    api<ListResponse<ApiSpecSummary>>(`/admin/api-specs?limit=${limit}&offset=${offset}`),
  get: (id: string) => api<ApiSpecSummary>(`/admin/api-specs/${id}`),
  create: (body: CreateApiSpecBody) =>
    api<ApiSpecSummary>('/admin/api-specs', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: string, body: UpdateApiSpecBody) =>
    api<ApiSpecSummary>(`/admin/api-specs/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) =>
    api<ApiSpecSummary>(`/admin/api-specs/${id}`, { method: 'DELETE' }),
  reseed: () => api<ReseedResult>('/admin/api-specs/reseed', { method: 'POST' }),
};

// Integrations — /api/integration
export interface Integration {
  id: string;
  name: string;
  type: string;
  description: string | null;
  base_url: string;
  auth_config: Record<string, unknown>;
  headers: Record<string, unknown>;
  tls_skip_verify: boolean;
  allow_private_network: boolean;
  // S13.3: write-only justification accompanying a toggle of
  // allow_private_network. Backend stores it inside the audit row;
  // never returned by GET (so it's null when reading).
  allow_private_network_reason?: string | null;
  health_check: Record<string, unknown>;
  status: string;
  last_checked_at: string | null;
  enabled: boolean;
  created_at: string;
  updated_at: string;
}

export interface IntegrationAction {
  id: string;
  integration_id: string;
  name: string;
  description: string | null;
  method: string;
  path: string;
  path_params: unknown[];
  query_params: unknown[];
  request_body: Record<string, unknown>;
  response_schema: Record<string, unknown>;
  category: string;
  enabled: boolean;
  created_at: string;
}

export interface IntegrationActionWithIntegration extends IntegrationAction {
  integration_name: string;
  integration_type: string;
  base_url: string;
  service_type: string;
}

// Payload for POST /api/integration/bundle — transactional create of an
// integration plus any number of scoped skills and specs. See the backend
// CreateIntegrationBundle DTO for the canonical shape.
export interface CreateIntegrationBundlePayload {
  integration: Partial<Integration>;
  skills: Array<{ name: string; content: string; sort_order?: number }>;
  specs: Array<{ api: string; content: string }>;
}

export interface CreateIntegrationBundleResult {
  integration: Integration;
  skills_created: number;
  specs_created: number;
}

export const integrations = {
  // Flat palette projection for the workflow editor — joins each action
  // with its parent integration so the UI can show `name / action` without
  // a second round-trip.
  listAllActions: () =>
    api<IntegrationActionWithIntegration[]>('/integrationaction/actions/all'),

  list: async (limit = 50, offset = 0) =>
    listFromBackend<Integration>(
      await api<unknown>(`/integration?limit=${limit}&offset=${offset}`),
      INTEGRATION_MAP,
      limit,
      offset,
    ),
  get: async (id: string) =>
    fromBackend<Integration>(await api<unknown>(`/integration/${id}`), INTEGRATION_MAP),
  create: async (data: Partial<Integration>) =>
    fromBackend<Integration>(
      await api<unknown>('/integration', {
        method: 'POST',
        body: JSON.stringify(toBackend(data, INTEGRATION_MAP)),
      }),
      INTEGRATION_MAP,
    ),
  update: async (id: string, data: Partial<Integration>) =>
    fromBackend<Integration>(
      await api<unknown>(`/integration/${id}`, {
        method: 'PUT',
        body: JSON.stringify(toBackend(data, INTEGRATION_MAP)),
      }),
      INTEGRATION_MAP,
    ),
  delete: (id: string) => api<void>(`/integration/${id}`, { method: 'DELETE' }),
  healthCheck: (id: string) =>
    api<{ status: string; status_code?: number; error?: string; auth_verified?: boolean }>(
      `/integration/${id}/health`,
      { method: 'POST' },
    ),

  // Transactional create — integration + skills + specs in one shot.
  // Server rolls back on any failure, so a 4xx means nothing was persisted.
  createBundle: (body: CreateIntegrationBundlePayload) =>
    api<CreateIntegrationBundleResult>('/integration/bundle', {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  // The scoped skills + specs currently linked to an integration.
  getBundle: (id: string) =>
    api<{ skills: PromptSkillSummary[]; specs: ApiSpecSummary[] }>(`/integration/${id}/bundle`),
  // Attach/replace a spec on an existing integration; re-materializes its
  // actions. `actions_upserted` = how many actions were added/refreshed.
  attachSpec: (id: string, body: { api: string; content: string }) =>
    api<{ spec: ApiSpecSummary; actions_upserted: number; spec_unparsed: boolean }>(
      `/integration/${id}/specs`,
      { method: 'POST', body: JSON.stringify(body) },
    ),
  // Attach/replace a prompt skill on an existing integration.
  attachSkill: (id: string, body: { name: string; content: string; sort_order?: number }) =>
    api<PromptSkillSummary>(`/integration/${id}/skills`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  // Actions. listActions filters the global list client-side because the
  // nested GET /api/integration/{id}/actions route does not exist.
  // createAction uses the nested POST route. deleteAction goes to
  // /integrationaction/{id}.
  listActions: async (id: string) => {
    const envelope = await api<unknown>('/integrationaction?limit=200&offset=0');
    const rawItems = Array.isArray(envelope)
      ? envelope
      : (envelope as { data?: unknown[] })?.data ?? [];
    const mapped = rawItems.map((a) =>
      fromBackend<IntegrationAction>(a, INTEGRATION_ACTION_MAP),
    );
    return mapped.filter((a) => a.integration_id === id);
  },
  createAction: async (id: string, data: Partial<IntegrationAction>) =>
    fromBackend<IntegrationAction>(
      await api<unknown>(`/integration/${id}/actions`, {
        method: 'POST',
        body: JSON.stringify(toBackend(data, INTEGRATION_ACTION_MAP)),
      }),
      INTEGRATION_ACTION_MAP,
    ),
  // Goes against the flat `/integrationaction/{id}` PUT — the action's
  // owning integration is implicit in the row itself.
  updateAction: async (actionId: string, data: Partial<IntegrationAction>) =>
    fromBackend<IntegrationAction>(
      await api<unknown>(`/integrationaction/${actionId}`, {
        method: 'PUT',
        body: JSON.stringify(toBackend(data, INTEGRATION_ACTION_MAP)),
      }),
      INTEGRATION_ACTION_MAP,
    ),
  deleteAction: (_integrationId: string, actionId: string) =>
    api<void>(`/integrationaction/${actionId}`, { method: 'DELETE' }),
};

// ─────────────────────────────────────────────────────────────────────
//  Policies (FR-023) — corporate guardrails. Admin-only for writes;
//  everyone can read so operators know why a create/run got blocked.
// ─────────────────────────────────────────────────────────────────────

export interface Policy {
  policy_id: string;
  name: string;
  description: string | null;
  rule: Record<string, unknown>;
  enabled: boolean;
  created_at: string;
  updated_at: string;
}

export interface PolicyAuditEntry {
  at: string;
  policy_name: string | null;
  action: string | null;
  reason: string | null;
  request_id: string | null;
  user_id: string | null;
}

export interface PolicyAuditResponse {
  total: number;
  by_policy: Record<string, number>;
  recent: PolicyAuditEntry[];
}

// ─────────────────────────────────────────────────────────────────────
//  Vendor commands catalog (drives the SSH command validator
//  and the agent's validate_ssh_commands tool).
// ─────────────────────────────────────────────────────────────────────

export interface VendorCommand {
  vendor_command_id: string;
  device_type: string;
  vendor_family: string;
  // 'exact' = literal command, 'pattern' = regex. The backend stores
  // exact values normalised (lowercased, whitespace-collapsed); patterns
  // are stored as authored.
  kind: 'exact' | 'pattern';
  value: string;
  notes: string | null;
  // 'seed' rows came from DefaultVendorCommandsSeedService; 'user' rows
  // are admin-authored. Editing a seed row promotes it to 'user' so a
  // re-seed never clobbers the change.
  source: 'seed' | 'user';
  created_at: string;
  updated_at: string;
}

export const vendorCommands = {
  list: async (
    deviceType: string | null,
    limit = 200,
    offset = 0,
  ): Promise<{ data: VendorCommand[]; total: number }> => {
    const params = new URLSearchParams();
    params.set('limit', String(limit));
    params.set('offset', String(offset));
    if (deviceType) params.set('device_type', deviceType);
    const res = await api<{ data: VendorCommand[]; total: number }>(
      `/vendorcommand?${params.toString()}`,
    );
    return { data: res.data ?? [], total: res.total ?? 0 };
  },
  get: (id: string) => api<VendorCommand>(`/vendorcommand/${id}`),
  create: (body: {
    device_type: string;
    vendor_family?: string;
    kind: 'exact' | 'pattern';
    value: string;
    notes?: string | null;
  }) =>
    api<VendorCommand>('/vendorcommand', {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  update: (
    id: string,
    body: Partial<{
      device_type: string;
      vendor_family: string;
      kind: 'exact' | 'pattern';
      value: string;
      notes: string | null;
    }>,
  ) =>
    api<VendorCommand>(`/vendorcommand/${id}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
  delete: (id: string) => api<void>(`/vendorcommand/${id}`, { method: 'DELETE' }),
};

export const policies = {
  list: async (limit = 100, offset = 0): Promise<{ data: Policy[]; total: number }> => {
    const res = await api<{ data: Policy[]; total: number }>(
      `/policy?limit=${limit}&offset=${offset}`,
    );
    return { data: res.data ?? [], total: res.total ?? 0 };
  },
  get: (id: string) => api<Policy>(`/policy/${id}`),
  create: (body: {
    name: string;
    description?: string | null;
    rule: Record<string, unknown>;
    enabled: boolean;
  }) => api<Policy>('/policy', { method: 'POST', body: JSON.stringify(body) }),
  update: (
    id: string,
    body: Partial<{
      name: string;
      description: string | null;
      rule: Record<string, unknown>;
      enabled: boolean;
    }>,
  ) => api<Policy>(`/policy/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) => api<void>(`/policy/${id}`, { method: 'DELETE' }),
  audit: (days = 7) => api<PolicyAuditResponse>(`/policy/audit?days=${days}`),
};

// ─────────────────────────────────────────────────────────────────────
//  Permission grants (granular RBAC / ABAC) — admin-only. A grant binds
//  a set of capabilities to a set of subjects (users), scoped by an
//  optional `conditions` object (environment / device / resource
//  matchers). An empty conditions object means "any context". Built-in
//  grants are seeded and immutable; the backend rejects mutations on them
//  (is_builtin=true), so the UI renders those read-only.
// ─────────────────────────────────────────────────────────────────────

export interface PermissionGrant {
  permission_grant_id: string;
  name: string;
  description: string | null;
  enabled: boolean;
  is_builtin: boolean;
  capabilities: string[];
  subject_ids: string[];
  conditions: Record<string, unknown>;
  created_at: string;
  updated_at: string;
}

export interface CapabilityInfo {
  key: string;
  domain: string;
  description: string;
  // Which condition dimensions this capability actually consults — a
  // subset of ["environment","device","resource"]. Drives the UI hint
  // that tells admins which condition fields matter for a selection.
  conditionable: string[];
}

export const permissionGrants = {
  list: async (
    limit = 100,
    offset = 0,
  ): Promise<{ data: PermissionGrant[]; total: number; limit: number; offset: number }> => {
    const res = await api<{ data: PermissionGrant[]; total: number; limit: number; offset: number }>(
      `/permission-grants?limit=${limit}&offset=${offset}`,
    );
    return {
      data: res.data ?? [],
      total: res.total ?? 0,
      limit: res.limit ?? limit,
      offset: res.offset ?? offset,
    };
  },
  get: (id: string) => api<PermissionGrant>(`/permission-grants/${id}`),
  create: (body: {
    name: string;
    description?: string | null;
    enabled: boolean;
    capabilities: string[];
    subject_ids: string[];
    conditions: Record<string, unknown>;
  }) => api<PermissionGrant>('/permission-grants', { method: 'POST', body: JSON.stringify(body) }),
  update: (
    id: string,
    body: Partial<{
      name: string;
      description: string | null;
      enabled: boolean;
      capabilities: string[];
      subject_ids: string[];
      conditions: Record<string, unknown>;
    }>,
  ) => api<PermissionGrant>(`/permission-grants/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  remove: (id: string) => api<void>(`/permission-grants/${id}`, { method: 'DELETE' }),
  // Subject membership is managed through dedicated endpoints so add/remove
  // is a single round-trip and never races a full-grant PUT.
  addSubject: (id: string, userId: string) =>
    api<PermissionGrant>(`/permission-grants/${id}/subjects/${userId}`, { method: 'POST' }),
  removeSubject: (id: string, userId: string) =>
    api<void>(`/permission-grants/${id}/subjects/${userId}`, { method: 'DELETE' }),
  capabilities: () => api<CapabilityInfo[]>('/permission-grants/capabilities'),
};

// ─────────────────────────────────────────────────────────────────────
//  Git integration. Repos are registrations of remote
//  Git URLs the backend is allowed to clone/pull/push into. Auth is
//  delegated to the credentials catalog (Type='git_token' rows).
// ─────────────────────────────────────────────────────────────────────

export interface GitRepository {
  git_repository_id: string;
  name: string;
  url: string;
  default_branch: string;
  auth_credential_id: string | null;
  description: string | null;
  local_path: string | null;
  last_fetched_at: string | null;
  created_at: string;
  updated_at: string;
}

export interface GitFileEntry {
  path: string;
  type: 'tree' | 'blob';
  size: number;
}

export interface GitListFilesResponse {
  ref: string;
  path: string;
  entries: GitFileEntry[];
}

export interface GitReadFileResponse {
  path: string;
  ref: string;
  content: string;
  size: number;
  is_binary: boolean;
}

export interface GitOpResult {
  ok: boolean;
  message: string;
  commit_sha: string | null;
  branch: string | null;
}

export interface GitBranchesResponse {
  current: string;
  branches: string[];
}

export interface GitDiffResponse {
  from: string;
  to: string;
  path: string | null;
  patch: string;
}

export const git = {
  list: async (limit = 100, offset = 0) => {
    const res = await api<{ data: GitRepository[]; total: number; limit: number; offset: number }>(
      `/git/repositories?limit=${limit}&offset=${offset}`,
    );
    return { data: res.data ?? [], total: res.total ?? 0 };
  },
  get: (id: string) => api<GitRepository>(`/git/repositories/${id}`),
  create: (body: {
    name: string;
    url: string;
    default_branch?: string;
    auth_credential_id?: string | null;
    description?: string | null;
  }) => api<GitRepository>('/git/repositories', { method: 'POST', body: JSON.stringify(body) }),
  update: (
    id: string,
    body: Partial<{
      name: string;
      url: string;
      default_branch: string;
      auth_credential_id: string | null;
      description: string | null;
    }>,
  ) =>
    api<GitRepository>(`/git/repositories/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) => api<void>(`/git/repositories/${id}`, { method: 'DELETE' }),
  pull: (id: string, branch?: string) =>
    api<GitOpResult>(
      `/git/repositories/${id}/pull${branch ? `?branch=${encodeURIComponent(branch)}` : ''}`,
      { method: 'POST' },
    ),
  push: (id: string, branch?: string) =>
    api<GitOpResult>(
      `/git/repositories/${id}/push${branch ? `?branch=${encodeURIComponent(branch)}` : ''}`,
      { method: 'POST' },
    ),
  branches: (id: string) => api<GitBranchesResponse>(`/git/repositories/${id}/branches`),
  checkout: (id: string, branch: string) =>
    api<GitOpResult>(
      `/git/repositories/${id}/checkout?branch=${encodeURIComponent(branch)}`,
      { method: 'POST' },
    ),
  files: (id: string, opts: { path?: string; ref?: string } = {}) => {
    const qs = new URLSearchParams();
    if (opts.path) qs.set('path', opts.path);
    if (opts.ref) qs.set('ref', opts.ref);
    const suffix = qs.toString() ? `?${qs}` : '';
    return api<GitListFilesResponse>(`/git/repositories/${id}/files${suffix}`);
  },
  readFile: (id: string, path: string, ref?: string) => {
    const qs = new URLSearchParams({ path });
    if (ref) qs.set('ref', ref);
    return api<GitReadFileResponse>(`/git/repositories/${id}/file?${qs}`);
  },
  writeFile: (
    id: string,
    body: {
      path: string;
      content: string;
      commit_message: string;
      branch?: string | null;
      push?: boolean;
      author_name?: string | null;
      author_email?: string | null;
    },
  ) =>
    api<GitOpResult>(`/git/repositories/${id}/file`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
  commit: (
    id: string,
    body: {
      commit_message: string;
      paths?: string[];
      push?: boolean;
      author_name?: string | null;
      author_email?: string | null;
    },
  ) =>
    api<GitOpResult>(`/git/repositories/${id}/commit`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  diff: (id: string, opts: { from?: string; to?: string; path?: string } = {}) => {
    const qs = new URLSearchParams();
    if (opts.from) qs.set('from', opts.from);
    if (opts.to) qs.set('to', opts.to);
    if (opts.path) qs.set('path', opts.path);
    const suffix = qs.toString() ? `?${qs}` : '';
    return api<GitDiffResponse>(`/git/repositories/${id}/diff${suffix}`);
  },
};

export interface GitWebhook {
  git_webhook_id: string;
  git_repository_id: string;
  name: string;
  provider: 'github' | 'gitlab' | 'generic';
  has_secret: boolean;
  on_push_workflow_id: string | null;
  on_push_branches: string[];
  auto_pull: boolean;
  enabled: boolean;
  allow_unsigned: boolean;
  last_delivery_at: string | null;
  last_delivery_status: string | null;
  ingestion_url: string | null;
  created_at: string;
  updated_at: string;
}

export interface GitWebhookDelivery {
  git_webhook_delivery_id: string;
  at: string;
  status: string;
  event: string | null;
  branch: string | null;
  commit_sha: string | null;
  workflow_run_id: string | null;
  error: string | null;
}

export const gitWebhooks = {
  list: async (repoId: string) => {
    const res = await api<{ data: GitWebhook[]; total: number }>(
      `/git/repositories/${repoId}/webhooks`,
    );
    return { data: res.data ?? [], total: res.total ?? 0 };
  },
  get: (repoId: string, id: string) =>
    api<GitWebhook>(`/git/repositories/${repoId}/webhooks/${id}`),
  create: (
    repoId: string,
    body: {
      name: string;
      provider: 'github' | 'gitlab' | 'generic';
      secret?: string | null;
      on_push_workflow_id?: string | null;
      on_push_branches?: string[];
      auto_pull?: boolean;
      enabled?: boolean;
      allow_unsigned?: boolean;
    },
  ) =>
    api<GitWebhook>(`/git/repositories/${repoId}/webhooks`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  update: (
    repoId: string,
    id: string,
    body: Partial<{
      name: string;
      provider: 'github' | 'gitlab' | 'generic';
      secret: string | null;
      on_push_workflow_id: string | null;
      on_push_branches: string[];
      auto_pull: boolean;
      enabled: boolean;
      allow_unsigned: boolean;
    }>,
  ) =>
    api<GitWebhook>(`/git/repositories/${repoId}/webhooks/${id}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
  delete: (repoId: string, id: string) =>
    api<void>(`/git/repositories/${repoId}/webhooks/${id}`, { method: 'DELETE' }),
  deliveries: async (repoId: string, id: string, limit = 50) => {
    const res = await api<{ data: GitWebhookDelivery[]; total: number }>(
      `/git/repositories/${repoId}/webhooks/${id}/deliveries?limit=${limit}`,
    );
    return { data: res.data ?? [], total: res.total ?? 0 };
  },
};

// ─────────────────────────────────────────────────────────────────────
//  Workflow simulation (FR-022) + integration action test bench
//  (FR-015/016). Both return raw payloads — the UI picks what to show.
// ─────────────────────────────────────────────────────────────────────

export const workflowSimulation = {
  simulate: (workflowId: string) =>
    api<{
      workflow_id: string;
      environment: string;
      version: number;
      node_count: number;
      ok: boolean;
      issue_count: number;
      warning_count: number;
      issues: Array<Record<string, unknown>>;
      warnings: Array<Record<string, unknown>>;
      notes: string[];
    }>(`/workflow/${workflowId}/simulate`, { method: 'POST' }),
};

// ─────────────────────────────────────────────────────────────────────
//  Device pools (co-managed with Devices) — CRUD + environment flags.
//  Pools are a grouping primitive; workflows target a pool and the
//  executor fans out to its static_members (or resolves filter_rules).
// ─────────────────────────────────────────────────────────────────────

export interface DevicePool {
  device_pool_id: string;
  name: string;
  description: string | null;
  filter_rules: Record<string, unknown>;
  static_members: string[];
  // Applied on top of the per-device flags: a run must be allowed by the
  // pool AND by the member it fans out to.
  allow_draft: boolean;
  allow_qa: boolean;
  allow_production: boolean;
  created_at: string;
  updated_at: string;
}

export const devicePools = {
  list: async (limit = 100, offset = 0): Promise<{ data: DevicePool[]; total: number }> => {
    const res = await api<{ data: DevicePool[]; total: number }>(
      `/devicepool?limit=${limit}&offset=${offset}`,
    );
    return { data: res.data ?? [], total: res.total ?? 0 };
  },
  get: (id: string) => api<DevicePool>(`/devicepool/${id}`),
  create: (body: {
    name: string;
    description?: string | null;
    static_members?: string[];
    allow_draft?: boolean;
    allow_qa?: boolean;
    allow_production?: boolean;
  }) => api<DevicePool>('/devicepool', { method: 'POST', body: JSON.stringify(body) }),
  update: (
    id: string,
    body: Partial<{
      name: string;
      description: string | null;
      static_members: string[];
      allow_draft: boolean;
      allow_qa: boolean;
      allow_production: boolean;
    }>,
  ) => api<DevicePool>(`/devicepool/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) => api<void>(`/devicepool/${id}`, { method: 'DELETE' }),
};

export interface QaDashboardRow {
  workflow_id: string;
  name: string;
  version: number;
  updated_at: string;
  last_run_id: string | null;
  last_run_status: string | null;
  last_run_completed_at: string | null;
  promotion_ready: boolean;
}

export interface QaDashboardResponse {
  qa_device_count: number;
  qa_pool_count: number;
  workflows: QaDashboardRow[];
}

export const qaLab = {
  dashboard: () => api<QaDashboardResponse>('/qa/dashboard'),
};

export const integrationActionTest = {
  run: (
    actionId: string,
    body: {
      body?: Record<string, unknown>;
      params?: Record<string, string>;
      query?: Record<string, string>;
    },
  ) =>
    api<{
      integration: string;
      action: string;
      method: string;
      resolved_url: string;
      applied_headers: Record<string, string>;
      transport_error?: string;
      response?: {
        status_code: number;
        headers: Record<string, string>;
        body: string;
      };
    }>(`/integration-test/action/${actionId}`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
};

// Workflow export/import. The export endpoint requires a Bearer token,
// so we cannot use `window.open` (no auth header on a fresh tab) — go
// through downloadReport, which streams the blob via fetch.
// `bundle` is the portable one: it carries every referenced snippet's full
// definition plus a stable slug for each integration/action, so another
// FlowWeaver instance can bind the workflow to ITS OWN copies of those. The
// others carry per-instance GUIDs and only round-trip on the instance that
// produced them.
export type WorkflowExportFormat = 'bundle' | 'yaml' | 'json' | 'python' | 'ansible';

const EXPORT_EXTENSION: Record<WorkflowExportFormat, string> = {
  bundle: 'bundle.json', yaml: 'yaml', json: 'json', python: 'py', ansible: 'yml',
};

export const exportWorkflow = {
  download: (id: string, format: WorkflowExportFormat) =>
    downloadReport(
      `/workflow/${id}/export?format=${format}`,
      `workflow-${id}.${EXPORT_EXTENSION[format]}`),
};

export const importWorkflow = {
  // `format` matches what the user uploaded; the server parses YAML or
  // JSON and creates a fresh draft. Returns the new WorkflowResponse.
  // Kept for CI / scripted reimports of our own native v1 exports.
  upload: async (text: string, format: 'yaml' | 'json'): Promise<Workflow> => {
    const res = await authedFetch(`${BASE}/workflow/import?format=${format}`, {
      method: 'POST',
      headers: {
        'Content-Type': format === 'json' ? 'application/json' : 'application/x-yaml',
      },
      body: text,
    });
    if (!res.ok) {
      const body = await res.json().catch(() => ({ error: res.statusText }));
      throw new ApiError({
        kind: kindForStatus(res.status),
        status: res.status,
        message: (body as { error?: string }).error ?? res.statusText,
        userMessage: (body as { error?: string }).error ?? `Import failed (HTTP ${res.status})`,
        body,
      });
    }
    return res.json();
  },
};

// ─── S15: cross-system import wizard ─────────────────────────────────

export type ImportStatus = 'pending' | 'analyzing' | 'ready' | 'failed' | 'committed';

export interface MappingCandidate {
  id: string;
  name: string;
  type: string;
  similarity_score: number;
  // 'snippet' when `id` is a SnippetId (default for backward compat);
  // 'integration' when `id` is an IntegrationId — used by the wizard
  // for integration_action stubs so the user can map to an existing
  // integration (Mail, Slack, ...) and have an IntegrationAction
  // materialised under it on commit.
  kind?: 'snippet' | 'integration';
}

export interface MissingSnippet {
  id_in_import: string;
  inferred_type: string;
  actions_available: string[];
  candidates_for_mapping: MappingCandidate[];
  // Phase 2: backend pre-drafts a snippet body for python_snippet
  // missing entries when an AI provider is configured. The wizard
  // pre-selects action: "generated" and binds this object so the
  // user reviews instead of clicking "Generate with AI" manually.
  pregenerated_snippet?: Record<string, unknown> | null;
}

export interface MissingIntegration {
  id_in_import: string;
  inferred_base_url: string | null;
  inferred_type: string | null;
  actions_available: string[];
  candidates_for_mapping: MappingCandidate[];
}

export interface MissingVendorCommand {
  device_type: string;
  command: string;
}

export interface NameCollision {
  matching_workflow_id: string;
  matching_workflow_environment: string;
  matching_workflow_version: number;
}

export interface StructuralDuplicate {
  matching_workflow_id: string;
  matching_workflow_name: string;
  matching_workflow_environment: string;
  match_score: number;
  fingerprint: string;
  diff_summary: string;
}

export interface RollbackRiskItem { snippet_name: string; snippet_type: string }

export interface AnalysisReport {
  format_detected: string;
  confidence: number;
  proposed_workflow: unknown;
  translation_notes: string[];
  missing_dependencies: {
    snippets: MissingSnippet[];
    integrations: MissingIntegration[];
    vendor_commands: MissingVendorCommand[];
  };
  conflicts: {
    name_collision: NameCollision | null;
    structural_duplicate: StructuralDuplicate | null;
  };
  rollback_risk: {
    non_reversible: RollbackRiskItem[];
    requires_compensation: RollbackRiskItem[];
    compensated: RollbackRiskItem[];
  } | null;
  warnings: string[];
}

export interface ImportDraftSnapshot {
  import_token: string;
  status: ImportStatus;
  progress: string | null;
  report: AnalysisReport | null;
  error: string | null;
}

export type SnippetActionKind = 'stub' | 'generated' | 'map' | 'skip';
export type IntegrationActionKind = 'create_needs_config' | 'map';
export type ConflictResolution = 'rename' | 'replace' | 'keep_existing' | 'fresh_copy';

export interface ResolvedSnippet {
  action: SnippetActionKind;
  target_id?: string;
  generated_snippet?: unknown;
}

export interface ResolvedIntegration {
  action: IntegrationActionKind;
  target_id?: string;
}

export interface CommitImportRequest {
  conflict_resolution: ConflictResolution;
  new_name?: string;
  target_environment?: string;
  duplicate_action?: 'skip' | 'update_existing' | 'import_as_new';
  snippets: Record<string, ResolvedSnippet>;
  integrations: Record<string, ResolvedIntegration>;
}

export interface CommitImportResponse {
  workflow_id: string;
  name: string;
  environment: string;
  warnings: string[];
}

export const importWizard = {
  // Uploads the raw file body. Returns the token immediately; analysis
  // runs asynchronously. The caller polls /status or subscribes via
  // /stream.
  analyze: async (body: Uint8Array | string, formatHint?: string): Promise<{ import_token: string }> => {
    const qs = formatHint ? `?format_hint=${encodeURIComponent(formatHint)}` : '';
    // Wrap the Uint8Array in a Blob so fetch's BodyInit accepts it.
    // Slicing into a fresh ArrayBuffer sidesteps the SharedArrayBuffer
    // type the runtime exposes from File API reads. A Blob is also
    // re-readable, which is what lets `authedFetch` replay it after a refresh.
    const fetchBody: BodyInit = typeof body === 'string'
      ? body
      : new Blob([new Uint8Array(body).slice().buffer as ArrayBuffer]);
    const res = await authedFetch(`${BASE}/workflow/import/analyze${qs}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/octet-stream' },
      body: fetchBody,
    });
    if (!res.ok) {
      const b = await res.json().catch(() => ({ error: res.statusText }));
      throw new ApiError({
        kind: kindForStatus(res.status),
        status: res.status,
        message: (b as { error?: string }).error ?? res.statusText,
        userMessage: (b as { error?: string }).error ?? `Analyze failed (HTTP ${res.status})`,
        body: b,
      });
    }
    return res.json();
  },

  status: (importToken: string) =>
    api<ImportDraftSnapshot>(`/workflow/import/${importToken}`),

  // Returns an EventSource-like helper. Calls `onEvent` on every SSE
  // frame and `onClose` once the stream ends (reason='done' on a clean
  // server close, 'error' on network failure or HTTP error, 'aborted'
  // when the caller invoked `.close()`).
  stream: (
    importToken: string,
    onEvent: (snap: ImportDraftSnapshot & { type: string }) => void,
    onClose?: (reason: 'done' | 'error' | 'aborted') => void,
  ): { close: () => void } => {
    // EventSource doesn't support custom headers, so we use fetch +
    // a streaming reader to keep the Bearer token. Via `authedFetch`, so an
    // access token that expires between starting an analysis and subscribing
    // to its progress refreshes instead of closing the stream — the analysis
    // itself can take longer than the token has left.
    const controller = new AbortController();
    let aborted = false;
    (async () => {
      let closeReason: 'done' | 'error' | 'aborted' = 'done';
      try {
        const res = await authedFetch(`${BASE}/workflow/import/${importToken}/stream`, {
          headers: { 'Accept': 'text/event-stream' },
          signal: controller.signal,
        });
        if (!res.ok || !res.body) {
          closeReason = 'error';
          return;
        }
        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          let nl;
          while ((nl = buffer.indexOf('\n\n')) >= 0) {
            const frame = buffer.slice(0, nl);
            buffer = buffer.slice(nl + 2);
            if (frame.startsWith('data: ')) {
              try {
                const json = JSON.parse(frame.slice(6));
                onEvent({ type: json.type, ...json.data });
              } catch {
                // best-effort; skip malformed frames
              }
            }
          }
        }
      } catch {
        closeReason = aborted ? 'aborted' : 'error';
      } finally {
        try { onClose?.(closeReason); } catch { /* caller's problem */ }
      }
    })();
    return {
      close: () => {
        aborted = true;
        controller.abort();
      },
    };
  },

  generateSnippet: (importToken: string, idInImport: string, promptHint?: string) =>
    api<{
      generated_snippet?: unknown;
      confidence?: number;
      fallback_stub?: unknown;
      error?: string;
    }>(`/workflow/import/${importToken}/generate-snippet`, {
      method: 'POST',
      body: JSON.stringify({ id_in_import: idInImport, prompt_hint: promptHint }),
    }),

  commit: (importToken: string, body: CommitImportRequest) =>
    api<CommitImportResponse>(`/workflow/import/${importToken}/commit`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  delete: (importToken: string) =>
    api<void>(`/workflow/import/${importToken}`, { method: 'DELETE' }),
};

// Auth — /api/auth. Login/refresh bypass the bearer+401-retry interceptor
// (see UNAUTHENTICATED_PATHS), so they can fail cleanly with a 401 without
// looping. `auth.login` is the one call the frontend actually invokes; the
// interceptor calls /refresh internally.
export const auth = {
  login: async (
    username: string,
    password: string,
  ): Promise<LoginResponseShape> => {
    const payload: Record<string, string> = { username, password };
    const body = await api<LoginResponseShape>('/auth/login', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
    authStore.set(sessionFromResponse(body));
    return body;
  },

  logout: async (): Promise<void> => {
    const token = authStore.refreshToken;
    try {
      // Best-effort — if the call fails we still clear the local state.
      await api<void>('/auth/logout', {
        method: 'POST',
        body: JSON.stringify({ refresh_token: token ?? '' }),
      });
    } catch {
      // Swallow — the user wants to log out either way.
    }
    authStore.clear();
  },

  me: () => api<{
    user_id: string;
    username: string;
    email: string;
    role: string;
    password_changed_at: string;
  }>('/auth/me'),

  changePassword: (current_password: string, new_password: string) =>
    api<void>('/auth/change-password', {
      method: 'POST',
      body: JSON.stringify({ current_password, new_password }),
    }),
};

// ─── Messaging channels (Slack/Teams/WhatsApp/Telegram) ───────────────

export interface MessagingChannel {
  messaging_channel_id: string;
  provider: string;
  name: string;
  has_bot_token: boolean;
  has_signing_secret: boolean;
  has_app_token: boolean;
  external_config: Record<string, unknown>;
  default_agent_id: string | null;
  max_role: string | null;
  require_linked_user: boolean;
  allowed_external_ids: string[];
  allow_unsigned: boolean;
  enabled: boolean;
  webhook_url: string;
  last_delivery_at: string | null;
  last_delivery_status: string | null;
  created_at: string;
  updated_at: string;
}

export interface CreateMessagingChannelBody {
  provider: string;
  name: string;
  bot_token?: string;
  signing_secret?: string;
  app_token?: string;
  external_config?: Record<string, unknown>;
  default_agent_id?: string | null;
  max_role?: string | null;
  require_linked_user?: boolean;
  allowed_external_ids?: string[];
  allow_unsigned?: boolean;
  enabled?: boolean;
}

export type UpdateMessagingChannelBody = Partial<CreateMessagingChannelBody>;

export interface MessagingInboundEvent {
  messaging_inbound_event_id: string;
  provider_event_id: string;
  external_thread_id: string | null;
  conversation_id: string | null;
  status: string;
  event: string | null;
  error: string | null;
  at: string;
}

export interface MessagingDelivery {
  messaging_delivery_id: string;
  conversation_id: string | null;
  external_thread_id: string | null;
  status: string;
  attempt: number;
  error: string | null;
  at: string;
}

export interface MessagingChannelActivity {
  inbound: MessagingInboundEvent[];
  outbound: MessagingDelivery[];
}

export interface MessagingIdentityLink {
  messaging_identity_link_id: string;
  external_workspace_id: string | null;
  external_user_id: string;
  linked_user_id: string;
  display_name: string | null;
  created_at: string;
}

export interface MessagingLinkPreview {
  provider: string;
  channel_name: string;
  external_user_id: string;
  expires_at: string;
}

export interface MessagingLinkConfirm {
  linked: boolean;
  messaging_identity_link_id: string;
}

export const messagingChannels = {
  list: async (limit = 50, offset = 0) => {
    const res = await api<{ data: MessagingChannel[]; total?: number; limit?: number; offset?: number }>(
      `/messaging/channels?limit=${limit}&offset=${offset}`,
    );
    return { data: res.data, total: res.total ?? res.data.length, limit, offset };
  },
  get: (id: string) => api<MessagingChannel>(`/messaging/channels/${id}`),
  create: (body: CreateMessagingChannelBody) =>
    api<MessagingChannel>('/messaging/channels', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: string, body: UpdateMessagingChannelBody) =>
    api<MessagingChannel>(`/messaging/channels/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) => api<void>(`/messaging/channels/${id}`, { method: 'DELETE' }),
  activity: (id: string, limit = 50) =>
    api<MessagingChannelActivity>(`/messaging/channels/${id}/deliveries?limit=${limit}`),
  links: (id: string) => api<{ data: MessagingIdentityLink[] }>(`/messaging/channels/${id}/links`),
  revokeLink: (id: string, linkId: string) =>
    api<void>(`/messaging/channels/${id}/links/${linkId}`, { method: 'DELETE' }),
};

export const messagingLink = {
  preview: (token: string) => api<MessagingLinkPreview>(`/messaging/link/${token}`),
  confirm: (token: string) =>
    api<MessagingLinkConfirm>(`/messaging/link/${token}/confirm`, { method: 'POST' }),
};

// ─── Email channels (SMTP relays) ─────────────────────────────────────
// One row per provider account. `provider` picks a connection preset; the
// stored host/port/security are always the effective values. The password is
// encrypted server-side and never returned — only `has_password`.

export type EmailProviderKind =
  | 'gmail'
  | 'outlook365'
  | 'sendgrid'
  | 'ses'
  | 'mailgun'
  | 'smtp';

export type EmailSecurity = 'starttls' | 'ssl' | 'none';

export interface EmailChannel {
  email_channel_id: string;
  name: string;
  provider: EmailProviderKind;
  host: string;
  port: number;
  security: EmailSecurity;
  username: string | null;
  has_password: boolean;
  from_address: string;
  from_name: string | null;
  reply_to: string | null;
  allow_private_network: boolean;
  tls_skip_verify: boolean;
  is_default: boolean;
  enabled: boolean;
  last_send_at: string | null;
  last_send_status: string | null;
  created_at: string;
  updated_at: string;
}

export interface CreateEmailChannelBody {
  name: string;
  provider: EmailProviderKind;
  host?: string;
  port?: number;
  security?: EmailSecurity;
  username?: string;
  password?: string;
  from_address?: string;
  from_name?: string;
  reply_to?: string;
  allow_private_network?: boolean;
  tls_skip_verify?: boolean;
  is_default?: boolean;
  enabled?: boolean;
}

export type UpdateEmailChannelBody = Partial<CreateEmailChannelBody>;

// Connection defaults per provider, served by the backend so the form and the
// handler can never disagree about (say) SendGrid's mandatory `apikey` user.
export interface EmailProviderPreset {
  provider: EmailProviderKind;
  label: string;
  host: string;
  port: number;
  security: EmailSecurity;
  fixed_username: string | null;
  username_hint: string;
  password_hint: string;
  docs_url: string | null;
}

export interface EmailTestResult {
  ok: boolean;
  message_id: string | null;
  detail: string | null;
  error: string | null;
  elapsed_ms: number;
}

export const emailChannels = {
  presets: async () => {
    const res = await api<{ data: EmailProviderPreset[] }>('/email/channels/presets');
    return res.data;
  },
  list: async (limit = 50, offset = 0) => {
    const res = await api<{ data: EmailChannel[]; total?: number; limit?: number; offset?: number }>(
      `/email/channels?limit=${limit}&offset=${offset}`,
    );
    return { data: res.data, total: res.total ?? res.data.length, limit, offset };
  },
  get: (id: string) => api<EmailChannel>(`/email/channels/${id}`),
  create: (body: CreateEmailChannelBody) =>
    api<EmailChannel>('/email/channels', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: string, body: UpdateEmailChannelBody) =>
    api<EmailChannel>(`/email/channels/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  delete: (id: string) => api<void>(`/email/channels/${id}`, { method: 'DELETE' }),
  test: (id: string, body: { to: string; subject?: string; body?: string }) =>
    api<EmailTestResult>(`/email/channels/${id}/test`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
};

// ─── Python module allow-list (admin) ─────────────────────────────────
// Admin-managed extra imports for python_snippet. Adding a `pip` module makes
// the worker install it; only `ready` rows become importable.
export interface AllowedPythonModule {
  allowed_python_module_id: string;
  import_name: string;
  source: string; // 'stdlib' | 'pip'
  pip_spec: string | null;
  status: string; // 'pending' | 'installing' | 'ready' | 'failed'
  installed_version: string | null;
  error: string | null;
  created_at: string;
  updated_at: string;
}

export interface CreateAllowedPythonModuleBody {
  import_name: string;
  source?: string;
  pip_spec?: string;
}

export const allowedPythonModules = {
  list: async (limit = 100, offset = 0) => {
    const res = await api<{ data: AllowedPythonModule[]; total?: number }>(
      `/admin/python-modules?limit=${limit}&offset=${offset}`,
    );
    return { data: res.data, total: res.total ?? res.data.length };
  },
  create: (body: CreateAllowedPythonModuleBody) =>
    api<AllowedPythonModule>('/admin/python-modules', { method: 'POST', body: JSON.stringify(body) }),
  remove: (id: string) =>
    api<void>(`/admin/python-modules/${id}`, { method: 'DELETE' }),
  retry: (id: string) =>
    api<AllowedPythonModule>(`/admin/python-modules/${id}/retry`, { method: 'POST' }),
};

// ─── Colour themes (user-authored) ────────────────────────────────────
// Themes offered in the picker next to the built-in fw-* ones. `colors` holds
// one base hex per palette; the 50→950 ramps are generated in the browser
// ($lib/theme/ramp.ts), never stored. A shared theme is visible to everyone
// and only an admin may publish or unpublish one — `can_edit` says whether
// this caller may touch the row, so the UI never offers a refused action.
export interface Theme {
  theme_id: string;
  name: string;
  description: string | null;
  colors: Record<string, string>;
  // Optional style knobs (roundness, ui_scale, font_*, heading_weight) —
  // see $lib/theme/customTheme.ts ThemeSettings for the exact shape. Null on
  // themes that never set any.
  settings: Record<string, unknown> | null;
  is_shared: boolean;
  owner_user_id: string | null;
  can_edit: boolean;
  created_at: string;
  updated_at: string;
}

export interface SaveThemeBody {
  name?: string;
  description?: string | null;
  colors?: Record<string, string>;
  settings?: Record<string, unknown> | null;
  is_shared?: boolean;
}

export const themes = {
  list: async (limit = 100, offset = 0) => {
    const res = await api<{ data: Theme[]; total?: number }>(
      `/themes?limit=${limit}&offset=${offset}`,
    );
    return { data: res.data, total: res.total ?? res.data.length };
  },
  create: (body: SaveThemeBody) =>
    api<Theme>('/themes', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: string, body: SaveThemeBody) =>
    api<Theme>(`/themes/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  remove: (id: string) => api<void>(`/themes/${id}`, { method: 'DELETE' }),
};

// ─── MCP servers (external Model Context Protocol) ────────────────────
// Register external MCP servers; the agent + workflow `mcp_call` node call
// their tools. Secrets are write-only (has_* booleans on read).

export interface McpServer {
  mcp_server_id: string;
  name: string;
  url: string;
  transport: string; // 'http'
  auth_type: string; // none | api_key | bearer | basic | headers | oauth_client_credentials | oauth_authorization_code
  has_auth: boolean;
  has_api_key: boolean;
  has_token: boolean;
  // basic auth: the non-secret username is echoed; the password is not.
  username: string | null;
  has_password: boolean;
  has_client_secret: boolean;
  has_access_token: boolean;
  headers: Record<string, unknown>;
  tls_skip_verify: boolean;
  allow_private_network: boolean;
  enabled: boolean;
  status: string; // ok | needs_config | needs_authorization | unreachable
  last_tools_synced_at: string | null;
  last_checked_at: string | null;
  tool_count: number;
  // Non-secret OAuth config, echoed for the edit form.
  client_id: string | null;
  authorization_endpoint: string | null;
  token_endpoint: string | null;
  scopes: string[] | null;
  redirect_uri: string | null;
}

export interface McpTool {
  mcp_tool_id: string;
  mcp_server_id: string;
  name: string;
  title: string | null;
  description: string | null;
  input_schema: Record<string, unknown>;
  enabled: boolean;
}

// Write-only auth material (never echoed). Only send fields the user filled.
export interface McpAuthInput {
  api_key_header?: string;
  api_key?: string;
  token?: string;
  username?: string;
  password?: string;
  secret_headers?: Record<string, string>;
  client_id?: string;
  client_secret?: string;
  authorization_endpoint?: string;
  token_endpoint?: string;
  scopes?: string[];
  redirect_uri?: string;
}

export interface CreateMcpServerBody {
  name: string;
  url: string;
  transport?: string;
  auth_type?: string;
  auth?: McpAuthInput;
  headers?: Record<string, unknown>;
  tls_skip_verify?: boolean;
  allow_private_network?: boolean;
  enabled?: boolean;
}

export type UpdateMcpServerBody = Partial<CreateMcpServerBody>;

export interface McpSyncResult {
  status: string;
  tools_synced: number;
  error: string | null;
}

export const mcpServers = {
  list: async (limit = 50, offset = 0) => {
    const res = await api<{ data: McpServer[]; total?: number; limit?: number; offset?: number }>(
      `/mcp-servers?limit=${limit}&offset=${offset}`,
    );
    return { data: res.data, total: res.total ?? res.data.length, limit, offset };
  },
  get: (id: string) => api<McpServer>(`/mcp-servers/${id}`),
  create: (body: CreateMcpServerBody) =>
    api<McpServer>('/mcp-servers', { method: 'POST', body: JSON.stringify(body) }),
  update: (id: string, body: UpdateMcpServerBody) =>
    api<McpServer>(`/mcp-servers/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  remove: (id: string) => api<void>(`/mcp-servers/${id}`, { method: 'DELETE' }),
  tools: async (id: string) => {
    const res = await api<{ data: McpTool[] }>(`/mcp-servers/${id}/tools`);
    return res.data;
  },
  listAllTools: async () => {
    const res = await api<{ data: McpTool[] }>(`/mcp-servers/tools`);
    return res.data;
  },
  sync: (id: string) => api<McpSyncResult>(`/mcp-servers/${id}/sync`, { method: 'POST' }),
  oauthStart: (id: string) =>
    api<{ authorization_url: string }>(`/mcp-servers/${id}/oauth/start`, { method: 'POST' }),
};
