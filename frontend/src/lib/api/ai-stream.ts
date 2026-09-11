// Streaming helper for POST /api/ai/chat/stream. The backend emits
// Server-Sent Events with a `data: {...json...}` payload per line.
// fetch + ReadableStream gives us POST body support (EventSource doesn't)
// and keeps the auth header logic identical to the REST client.
//
// Event shapes match the server's AiChatController.Stream writer:
//   {type: 'text', content: string}
//   {type: 'tool_start', name: string, args_preview: unknown}
//   {type: 'tool_result', name: string, success: boolean, preview: unknown}
//   {type: 'done', tokens_in: number, tokens_out: number, iterations: number}
//   {type: 'timeout', partial: string}
//   {type: 'error', message: string}

import { authStore } from '$lib/stores/auth.svelte';
import { tryRefresh } from '$lib/api/client';

/**
 * Metadata the backend attaches to a tool_result when the tool produced a
 * downloadable artifact (today only `generate_report`). The chat UI uses
 * this to render a proper download link without touching the truncated
 * preview blob.
 */
export interface ToolAttachment {
  kind: 'report';
  report_artifact_id: string;
  filename: string;
  content_type?: string;
  format?: string;
  size_bytes?: number;
  download_url: string;
}

export type ChatStreamEvent =
  | { type: 'conversation'; id: string; is_new: boolean }
  | { type: 'text'; content: string }
  | { type: 'tool_start'; name: string; args_preview?: unknown }
  | { type: 'tool_result'; name: string; success: boolean; preview?: unknown; attachment?: ToolAttachment }
  | { type: 'done'; tokens_in?: number; tokens_out?: number; iterations?: number }
  | { type: 'timeout'; partial: string; deadline_s?: number }
  // `code` lets callers pivot on machine-readable reasons (e.g. show a
  // "Configure AI provider" CTA when `code === 'no_provider'`) while
  // still defaulting to the raw message for anything unrecognized.
  | { type: 'error'; message: string; code?: string };

export interface ChatStreamRequest {
  message: string;
  agent_id?: string;
  conversation_id?: string;
}

// Async generator-style consumer. Usage:
//   for await (const evt of streamChat(req, ctrl.signal)) { … }
// Aborting the caller's AbortController cleanly closes the reader and
// ends the generator; the backend's cancellation token fires too.
export async function* streamChat(
  request: ChatStreamRequest,
  signal?: AbortSignal,
): AsyncGenerator<ChatStreamEvent, void, void> {
  // The SSE fetch bypasses client.ts's auth interceptor, so a near-expired
  // access token never gets refreshed here — and a long chat session then
  // sends an EXPIRED JWT, which the backend 401s. The chat AND its internal
  // fw_* self-calls (RestOperationExecutor resolves session:current:jwt
  // from this request's bearer) both inherit this token, so refreshing it
  // up front fixes the "constant 401 after yes" failures. Retry once in
  // case it lapsed between the check and the send.
  const openChatStream = () => {
    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      Accept: 'text/event-stream',
    };
    const token = authStore.accessToken;
    if (token) headers['Authorization'] = `Bearer ${token}`;
    return fetch('/api/ai/chat/stream', {
      method: 'POST',
      headers,
      body: JSON.stringify(request),
      signal,
    });
  };

  if (authStore.isExpiringSoon) await tryRefresh();
  let resp = await openChatStream();
  if (resp.status === 401 && (await tryRefresh())) {
    resp = await openChatStream();
  }

  if (!resp.ok || !resp.body) {
    // Tries to surface the backend error body as a single `error` event so
    // the caller can render it in the chat the same way it renders mid-stream
    // failures. Doesn't throw — keeps the UI code path uniform.
    const message = await safeText(resp);
    yield { type: 'error', message: `HTTP ${resp.status}: ${message || resp.statusText}` };
    return;
  }

  const reader = resp.body.getReader();
  const decoder = new TextDecoder('utf-8');
  let buffer = '';

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });

      // SSE frame boundary is "\n\n". Accumulate until we have at least
      // one complete frame, process all complete frames, leave the
      // remainder for the next chunk.
      let boundary = buffer.indexOf('\n\n');
      while (boundary !== -1) {
        const frame = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);
        const evt = parseFrame(frame);
        if (evt) yield evt;
        boundary = buffer.indexOf('\n\n');
      }
    }
  } finally {
    // Abort leaves the reader in a lock; release it explicitly so the
    // connection can be reclaimed by the HTTP stack.
    try { reader.releaseLock(); } catch { /* already released */ }
  }
}

// Each SSE frame can contain multiple `data:` lines (per spec, concatenated
// with newlines). Our server only sends one per frame, but we stay strict
// to the spec so a proxy that re-frames can't break us.
function parseFrame(frame: string): ChatStreamEvent | null {
  const lines = frame.split('\n').filter((l) => l.startsWith('data:'));
  if (lines.length === 0) return null;
  const payload = lines.map((l) => l.slice(5).trimStart()).join('\n');
  if (!payload) return null;
  try {
    return JSON.parse(payload) as ChatStreamEvent;
  } catch {
    // Malformed frame — skip rather than tear the whole stream down.
    return null;
  }
}

async function safeText(resp: Response): Promise<string> {
  try { return await resp.text(); }
  catch { return ''; }
}
