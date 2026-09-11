export interface RunStreamMessage {
  type: 'run_status' | 'steps_update' | 'step_update' | 'run_completed' | 'error';
  status?: string;
  started_at?: string;
  completed_at?: string;
  steps?: unknown[];
  step?: unknown;
  message?: string;
  [k: string]: unknown;
}

/** Real connection state of the run stream, surfaced to the UI so the
 *  "Live" indicator reflects the socket, not just intent. */
export type RunStreamState = 'connecting' | 'open' | 'reconnecting' | 'closed';

export interface RunStreamOptions {
  /** Notified whenever the socket transitions state. */
  onStateChange?: (state: RunStreamState) => void;
  /** Return false to stop reconnecting (e.g. the run reached a terminal
   *  status). Re-evaluated before each reconnect attempt. Defaults to always
   *  reconnect until the caller tears the stream down. */
  shouldReconnect?: () => boolean;
}

/**
 * Open a resilient run-event stream. Reconnects with capped exponential
 * backoff on unexpected drops and reports connection state via
 * `opts.onStateChange`. Returns a cleanup function that closes the socket and
 * cancels any pending reconnect (a deliberate close never reconnects).
 */
export function connectRunStream(
  runId: string,
  onMessage: (data: RunStreamMessage) => void,
  opts: RunStreamOptions = {},
): () => void {
  const { onStateChange, shouldReconnect } = opts;
  const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  const url = `${protocol}//${window.location.host}/api/v1/runs/${runId}/stream`;

  let ws: WebSocket | null = null;
  let closedByCaller = false;
  let attempt = 0;
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null;

  const setState = (s: RunStreamState) => onStateChange?.(s);

  function scheduleReconnect() {
    if (closedByCaller) return;
    if (shouldReconnect && !shouldReconnect()) {
      setState('closed');
      return;
    }
    // 1s, 2s, 4s … capped at 15s, with jitter to avoid thundering herds.
    const delay = Math.min(15000, 1000 * 2 ** attempt) + Math.floor(Math.random() * 400);
    attempt += 1;
    setState('reconnecting');
    reconnectTimer = setTimeout(connect, delay);
  }

  function connect() {
    setState(attempt === 0 ? 'connecting' : 'reconnecting');
    try {
      ws = new WebSocket(url);
    } catch {
      scheduleReconnect();
      return;
    }

    ws.onopen = () => {
      attempt = 0;
      setState('open');
    };

    ws.onmessage = (event) => {
      try {
        onMessage(JSON.parse(event.data) as RunStreamMessage);
      } catch (e) {
        console.error('WebSocket parse error', e);
      }
    };

    // onerror is always followed by onclose; let onclose drive reconnect.
    ws.onerror = () => {};
    ws.onclose = () => {
      if (closedByCaller) {
        setState('closed');
        return;
      }
      scheduleReconnect();
    };
  }

  connect();

  return () => {
    closedByCaller = true;
    if (reconnectTimer) clearTimeout(reconnectTimer);
    ws?.close();
  };
}
