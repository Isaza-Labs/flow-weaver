import { test, expect, type Page } from '@playwright/test';
import { BASE, sse, stubChat, sendPrompt } from './fixtures/chat-stub';

// The chat renderer used to strip every link to plain text — the model is
// untrusted, and a markdown link is the cheapest phishing vector there is.
// Links are back, but only ever as paths into this app's resource pages,
// opened in a new tab (`$lib/markdown.ts` → LINKABLE_ROUTE). These pin both
// halves: the in-app link the agent is now told to emit works, and nothing
// else becomes clickable.

// Scope every assertion to the rendered answer — the sidebar is full of real
// nav links ("Runs", "Workflows") that would otherwise match by accessible
// name, since Playwright's `name` is a substring match.
const thread = (page: Page) => page.locator('.fw-md');

const answer = (markdown: string) =>
  sse(
    { type: 'conversation', id: 'conv-1', is_new: true },
    { type: 'text', content: markdown },
    { type: 'done', tokens_in: 1, tokens_out: 1, iterations: 1 },
  );

const WF = '6b1f0f9c-1111-2222-3333-444455556666';

test('an in-app workflow link renders as a new-tab anchor', async ({ page }) => {
  await stubChat(page, answer(`Here it is: [daily-backup](/workflows/${WF}) — 7 nodes.`));
  await page.goto(BASE + '/ai/chat');
  await sendPrompt(page, 'show me the workflow');

  const link = thread(page).getByRole('link', { name: 'daily-backup' });
  await expect(link).toBeVisible();
  await expect(link).toHaveAttribute('href', `/workflows/${WF}`);
  await expect(link).toHaveAttribute('target', '_blank');
  await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
});

// Tool results build links from Workflow:PublicBaseUrl, which is routinely
// configured to an origin nobody browses (compose defaults to localhost:3000
// while operators reach the app by LAN IP). The renderer keeps the path so
// the link still lands on the app the user is actually looking at.
test('an absolute link is reduced to its path on our own origin', async ({ page }) => {
  await stubChat(page, answer(`Open [daily-backup](http://localhost:3000/workflows/${WF}).`));
  await page.goto(BASE + '/ai/chat');
  await sendPrompt(page, 'show me the workflow');

  await expect(thread(page).getByRole('link', { name: 'daily-backup' })).toHaveAttribute(
    'href',
    `/workflows/${WF}`,
  );
});

test('an off-site link never becomes clickable', async ({ page }) => {
  await stubChat(page, answer('Click [this thing](https://evil.example.com/workflows/x) to continue.'));
  await page.goto(BASE + '/ai/chat');
  await sendPrompt(page, 'anything');

  // The text survives so the answer still reads; the click target does not,
  // and no anchor in the answer points off-origin.
  await expect(thread(page).getByText('to continue.')).toBeVisible();
  const offOrigin = await page.evaluate(
    () =>
      [...document.querySelectorAll('.fw-md a[href]')].filter(
        (a) => new URL((a as HTMLAnchorElement).href).origin !== location.origin,
      ).length,
  );
  expect(offOrigin).toBe(0);
});

// Path-only would not be enough on its own: a route that *acts* rather than
// displays turns a rendered link into a trap.
test('a link to a non-resource route is left as text', async ({ page }) => {
  await stubChat(page, answer('Click [log out now](https://evil.example.com/logout) please.'));
  await page.goto(BASE + '/ai/chat');
  await sendPrompt(page, 'anything');

  await expect(thread(page).getByText('log out now')).toBeVisible();
  await expect(thread(page).getByRole('link', { name: 'log out now' })).toHaveCount(0);
});

test('a javascript: link is left as text', async ({ page }) => {
  await stubChat(page, answer('Click [run it](javascript:alert(1)) now.'));
  await page.goto(BASE + '/ai/chat');
  await sendPrompt(page, 'anything');

  await expect(thread(page).getByRole('link', { name: 'run it' })).toHaveCount(0);
  const jsHrefs = await page.evaluate(
    () => [...document.querySelectorAll('.fw-md a')].filter((a) => (a as HTMLAnchorElement).protocol === 'javascript:').length,
  );
  expect(jsHrefs).toBe(0);
});
