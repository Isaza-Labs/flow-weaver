import type { Page } from '@playwright/test';

// Shared harness for the chat e2e specs. Unlike the rest of e2e/, these run
// against a stubbed API — no backend, no AI provider — so a chat turn is
// reproducible and instant.
//
// Run the app first, either way:
//   npm run build && npm run preview -- --port 4173
//   npm run dev -- --port 5174   →   E2E_BASE=http://localhost:5174

export const BASE = process.env.E2E_BASE || 'http://localhost:4173';

/** Builds an SSE body from the event objects the chat page consumes. */
export function sse(...events: object[]) {
  return events.map((e) => `data: ${JSON.stringify(e)}\n\n`).join('');
}

/** One plain turn: conversation id, a line of text, done. */
export const PLAIN = sse(
  { type: 'conversation', id: 'conv-1', is_new: true },
  { type: 'text', content: 'hola' },
  { type: 'done', tokens_in: 1, tokens_out: 1, iterations: 1 },
);

const CONVERSATIONS = {
  data: Array.from({ length: 12 }, (_, i) => ({
    conversation_id: `conv-${i}`,
    title: `Thread ${i}`,
    source: 'web',
    created_at: '2026-08-04T10:00:00Z',
    updated_at: '2026-08-04T10:00:00Z',
    message_count: 4,
  })),
  total: 12,
};

/** Logs in, stubs every API call, and answers the chat stream with `body`. */
export async function stubChat(page: Page, body: string) {
  await page.addInitScript(() => {
    localStorage.setItem(
      'flowweaver:auth',
      JSON.stringify({
        accessToken: 'e2e-token',
        refreshToken: 'e2e-refresh',
        userId: '00000000-0000-0000-0000-000000000001',
        username: 'e2e',
        role: 'admin',
        expiresAt: Date.now() + 86_400_000,
      }),
    );
  });

  // Match on pathname, not a glob: `**/api/**` also swallows the app's own
  // modules under /src/lib/api/. Catch-all first — later routes win.
  await page.route(
    (url) => url.pathname.startsWith('/api/'),
    (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ data: [], items: [], total: 0 }),
      }),
  );
  await page.route(
    (url) => url.pathname.includes('/ai/conversations'),
    (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(CONVERSATIONS),
      }),
  );
  await page.route(
    (url) => url.pathname === '/api/ai/chat/stream',
    (route) => route.fulfill({ status: 200, contentType: 'text/event-stream', body }),
  );
}

/** Sends one prompt and waits for the turn to finish. */
export async function sendPrompt(page: Page, text: string) {
  const box = page.getByPlaceholder('Ask the agent');
  await box.click();
  await box.fill(text);
  await box.press('Enter');
  await box.isEnabled();
  return box;
}
