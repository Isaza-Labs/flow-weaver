import type { Handle } from '@sveltejs/kit';

// When running behind the Node adapter (Docker production build), the Vite
// dev proxy is gone. The browser still fetches /api/... (same origin as the
// frontend container on :3000), so without this hook every call returns 404.
//
// This handler catches /api, /openapi, and /scalar, forwards the request to
// BACKEND_URL (set by docker-compose to http://backend:8080), and streams the
// upstream response back unchanged. Everything else flows through SvelteKit
// normally.
//
// In `npm run dev` this hook is effectively unused because the Vite dev
// server proxies those paths before SvelteKit sees them.

const BACKEND_URL = process.env.BACKEND_URL ?? 'http://localhost:8080';
const PROXY_PREFIXES = ['/api/', '/openapi/', '/scalar'];

export const handle: Handle = async ({ event, resolve }) => {
  const { pathname, search } = event.url;

  if (PROXY_PREFIXES.some((p) => pathname === p.replace(/\/$/, '') || pathname.startsWith(p))) {
    const target = `${BACKEND_URL}${pathname}${search}`;

    // Clone method, headers, and body. Drop hop-by-hop headers Node won't let
    // us forward. Body is passed as-is (stream) for non-GET/HEAD.
    const headers = new Headers(event.request.headers);
    headers.delete('host');
    headers.delete('connection');
    headers.delete('content-length');

    // Tell the backend who actually made the request. Without this every call
    // reaches it from THIS container, so the audit trail recorded one constant
    // address for the whole platform and the per-IP login limiter shared a
    // single 5-per-minute budget between all users.
    //
    // Appending (rather than setting) preserves any chain from a proxy in
    // front of us — cloudflared, nginx, an ALB. The backend only honours these
    // headers from proxies the operator listed in Network__TrustedProxies, so
    // a forged inbound value can't survive: it is either dropped, or the
    // operator has trusted this hop and we are the ones appending the truth.
    const clientAddress = event.getClientAddress();
    const forwardedFor = event.request.headers.get('x-forwarded-for');
    headers.set(
      'x-forwarded-for',
      forwardedFor ? `${forwardedFor}, ${clientAddress}` : clientAddress,
    );
    headers.set('x-forwarded-proto', event.url.protocol.replace(':', ''));
    headers.set('x-forwarded-host', event.request.headers.get('host') ?? event.url.host);

    const init: RequestInit = {
      method: event.request.method,
      headers,
      redirect: 'manual',
    };

    if (event.request.method !== 'GET' && event.request.method !== 'HEAD') {
      init.body = await event.request.arrayBuffer();
    }

    try {
      const upstream = await fetch(target, init);
      // Copy upstream response 1:1. Keep Set-Cookie etc.
      const responseHeaders = new Headers(upstream.headers);
      return new Response(upstream.body, {
        status: upstream.status,
        statusText: upstream.statusText,
        headers: responseHeaders,
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return new Response(
        JSON.stringify({ error: `backend_unreachable: ${message}` }),
        { status: 502, headers: { 'content-type': 'application/json' } },
      );
    }
  }

  return resolve(event);
};
