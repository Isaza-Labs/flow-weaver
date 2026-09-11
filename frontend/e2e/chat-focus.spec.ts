import { test, expect, type Page } from '@playwright/test';
import { BASE, PLAIN, sse, stubChat as stub } from './fixtures/chat-stub';

// Asserts the caret lands back in the prompt box after a turn, which is what
// `refocusInput()` on the chat page exists to guarantee. The prompt box is
// `disabled` while streaming and the browser drops focus off a disabled
// element, so without that call every turn ends with the caret on <body>.
//
// Harness + how to run: e2e/fixtures/chat-stub.ts.
//
// The two Stop / mid-turn cases need a stream slow enough to interact with:
//   node e2e/fixtures/sse-server.mjs      (port 8099)
// They are skipped when it isn't running.

const SLOW_STREAM = 'http://localhost:8099/stream';

// What a real answer looks like: tool calls, a fenced code block, a mermaid
// diagram — all of which render asynchronously after the stream closes.
const RICH = sse(
  { type: 'conversation', id: 'conv-1', is_new: true },
  { type: 'tool_start', name: 'get_run_details', args_preview: { run_id: 'r1' } },
  { type: 'tool_result', name: 'get_run_details', success: true, preview: { status: 'failed' } },
  { type: 'text', content: '## Diagnosis\n\nThe step failed.\n\n```json\n{"a":1}\n```\n\n' },
  { type: 'text', content: '```mermaid\nflowchart TD\n  a --> b\n```\n\nDone.' },
  { type: 'done', tokens_in: 10, tokens_out: 20, iterations: 2 },
);

function activeDescriptor(page: Page) {
  return page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null;
    if (!el) return 'null';
    const ph = el.getAttribute('placeholder');
    return `${el.tagName}${el.id ? '#' + el.id : ''}${ph ? '[' + ph.slice(0, 18) + ']' : ''}`;
  });
}

test('caret returns to the prompt box after a turn (Enter to send)', async ({ page }) => {
  await stub(page, PLAIN);
  await page.goto(BASE + '/ai/chat');
  const box = page.getByPlaceholder('Ask the agent');
  await expect(box).toBeVisible();

  await box.click();
  await box.fill('hola');
  await box.press('Enter');

  await expect(box).toBeEnabled();
  // Past the 500 ms sidebar refresh — a late re-render must not steal focus.
  await page.waitForTimeout(1500);

  console.log('active (enter):', await activeDescriptor(page));
  await expect(box).toBeFocused();
});

test('caret returns to the prompt box after a turn (click Send)', async ({ page }) => {
  await stub(page, PLAIN);
  await page.goto(BASE + '/ai/chat');
  const box = page.getByPlaceholder('Ask the agent');
  await expect(box).toBeVisible();

  await box.fill('hola');
  await page.getByRole('button', { name: 'Send' }).click();

  await expect(box).toBeEnabled();
  await page.waitForTimeout(1500);

  console.log('active (click):', await activeDescriptor(page));
  await expect(box).toBeFocused();
});

test('caret lands in the prompt box after the ?fix= auto-send flow', async ({ page }) => {
  await stub(page, RICH);
  const fix = encodeURIComponent(
    JSON.stringify({ run_id: 'r1', node_id: 'make-html-report', error: 'boom', workflow_id: 'w1' }),
  );
  await page.goto(`${BASE}/ai/chat?fix=${fix}`);
  const box = page.getByPlaceholder('Ask the agent');
  await expect(box).toBeVisible();

  // No user interaction at all — the page sends on its own.
  await expect(box).toBeEnabled();
  await page.waitForTimeout(2500);

  console.log('active (fix flow):', await activeDescriptor(page));
  await expect(box).toBeFocused();
});

// The scenarios below need a stream that lasts long enough to interact with
// mid-turn — see e2e/fixtures/sse-server.mjs (one frame every 900 ms).
async function stubSlow(page: Page) {
  const up = await fetch(SLOW_STREAM, { method: 'POST' }).then(
    () => true,
    () => false,
  );
  test.skip(!up, 'slow SSE fixture not running — node e2e/fixtures/sse-server.mjs');
  await stub(page, PLAIN);
  await page.route(
    (url) => url.pathname === '/api/ai/chat/stream',
    (route) => route.continue({ url: SLOW_STREAM }),
  );
}

test('caret returns to the prompt box after Stop', async ({ page }) => {
  await stubSlow(page);
  await page.goto(BASE + '/ai/chat');
  const box = page.getByPlaceholder('Ask the agent');
  await expect(box).toBeVisible();

  await box.click();
  await box.fill('hola');
  await box.press('Enter');

  await page.getByRole('button', { name: 'Stop' }).click();

  await expect(box).toBeEnabled();
  await page.waitForTimeout(1500);

  console.log('active (stop):', await activeDescriptor(page));
  await expect(box).toBeFocused();
});

test('caret returns to the prompt box when the user reads the thread mid-turn', async ({ page }) => {
  await stubSlow(page);
  await page.goto(BASE + '/ai/chat');
  const box = page.getByPlaceholder('Ask the agent');
  await expect(box).toBeVisible();

  await box.click();
  await box.fill('diagnose run r1');
  await box.press('Enter');

  // What people actually do while waiting: poke at something in the thread.
  // Focus whatever focusable the streaming answer put there.
  await page.waitForTimeout(2200);
  const what = await page.evaluate(() => {
    const el = document.querySelector<HTMLElement>(
      'main button, main summary, main a[href], [class*="overflow-y"] button',
    );
    if (!el) return 'nothing focusable in the thread';
    el.focus();
    return `${el.tagName}.${el.className.slice(0, 40)}`;
  });
  console.log('focused mid-turn:', what, '->', await activeDescriptor(page));

  await expect(box).toBeEnabled({ timeout: 15000 });
  await page.waitForTimeout(1500);

  console.log('active (read thread mid-turn):', await activeDescriptor(page));
  await expect(box).toBeFocused();
});

test('caret returns to the prompt box after a failed turn', async ({ page }) => {
  await stub(page, sse({ type: 'error', message: 'no provider configured', code: 'no_provider' }));
  await page.goto(BASE + '/ai/chat');
  const box = page.getByPlaceholder('Ask the agent');
  await expect(box).toBeVisible();

  await box.click();
  await box.fill('hola');
  await box.press('Enter');

  await expect(box).toBeEnabled();
  await page.waitForTimeout(1500);

  console.log('active (error):', await activeDescriptor(page));
  await expect(box).toBeFocused();
});

test('caret returns to the prompt box after a rich answer (tools + code + mermaid)', async ({
  page,
}) => {
  await stub(page, RICH);
  await page.goto(BASE + '/ai/chat');
  const box = page.getByPlaceholder('Ask the agent');
  await expect(box).toBeVisible();

  await box.click();
  await box.fill('diagnose run r1');
  await box.press('Enter');

  await expect(box).toBeEnabled();
  await page.waitForTimeout(2500);

  console.log('active (rich):', await activeDescriptor(page));
  await expect(box).toBeFocused();
});
