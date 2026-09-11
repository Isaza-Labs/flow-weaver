// Authentication store. Holds the JWT access token, the refresh token, and
// the identity of the logged-in user. Persisted to localStorage under a
// single key so the whole session hydrates atomically.
//
// SSR note: the initial read happens both on server render and client
// hydration. On the server `localStorage` is undefined; we guard with
// `browser` and default to a logged-out state. The real token arrives on
// the client.
//
// The hooks.server.ts proxy forwards /api/* to the backend, so tokens set
// here flow back to Bearer-authorized requests via client.ts.

import { browser } from '$app/environment';

export interface AuthSession {
  accessToken: string;
  refreshToken: string;
  userId: string;
  username: string;
  role: string;
  // Epoch ms when the access token stops being valid.
  expiresAt: number;
}

const STORAGE_KEY = 'flowweaver:auth';

function readSession(): AuthSession | null {
  if (!browser) return null;
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as AuthSession;
    if (!parsed.accessToken || !parsed.userId) return null;
    return parsed;
  } catch {
    return null;
  }
}

function writeSession(session: AuthSession | null): void {
  if (!browser) return;
  try {
    if (session) {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
    } else {
      localStorage.removeItem(STORAGE_KEY);
    }
  } catch {
    // Quota / disabled storage — silently ignore; the session still lives in memory.
  }
}

class AuthStore {
  session = $state<AuthSession | null>(readSession());

  constructor() {
    // Cross-tab session sync. Every tab keeps its OWN in-memory copy of the
    // session, but they all share one localStorage entry — and one ROTATING
    // refresh token. When a tab rotates the token, the others must adopt the
    // new value; otherwise a lagging tab later presents the old, now-revoked
    // token, the backend's reuse detection treats it as theft and revokes the
    // whole chain, and EVERY tab gets logged out. The `storage` event fires
    // only in OTHER tabs, so this never echoes our own set()/clear().
    if (browser) {
      window.addEventListener('storage', (e) => {
        if (e.key === STORAGE_KEY) this.session = readSession();
      });
    }
  }

  get isAuthenticated(): boolean {
    return this.session !== null && this.session.accessToken !== '';
  }

  get accessToken(): string | null {
    return this.session?.accessToken ?? null;
  }

  get refreshToken(): string | null {
    return this.session?.refreshToken ?? null;
  }

  // True when the access token expires within the next 30 seconds. The
  // interceptor in client.ts uses this to preemptively refresh instead of
  // paying a 401 round-trip.
  get isExpiringSoon(): boolean {
    if (!this.session) return false;
    return this.session.expiresAt - Date.now() < 30_000;
  }

  set(session: AuthSession): void {
    this.session = session;
    writeSession(session);
  }

  clear(): void {
    this.session = null;
    writeSession(null);
  }

  // Re-read the persisted session from localStorage into the in-memory copy
  // WITHOUT writing it back (the value is already there). Used inside the
  // cross-tab refresh lock to pick up a rotation another tab just did before
  // we hit the network — so we never refresh with a stale token.
  syncFromStorage(): AuthSession | null {
    const s = readSession();
    this.session = s;
    return s;
  }
}

export const authStore = new AuthStore();

// Shape of the LoginResponse DTO that the backend returns. Kept here so
// both the login page and client.ts can map with it.
export interface LoginResponseShape {
  access_token: string;
  refresh_token: string;
  user_id: string;
  username: string;
  role: string;
  expires_in: number;
}

export function sessionFromResponse(r: LoginResponseShape): AuthSession {
  return {
    accessToken: r.access_token,
    refreshToken: r.refresh_token,
    userId: r.user_id,
    username: r.username,
    role: r.role,
    expiresAt: Date.now() + r.expires_in * 1000,
  };
}
